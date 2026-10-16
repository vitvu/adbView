using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbView;

/// <summary>
/// Low-latency input for a device. Touch and hardware keys are written straight to the kernel
/// input device (/dev/input/eventX) with the shell's builtin `print` through one persistent
/// `adb shell` — no process is spawned on the phone per event (unlike `input tap`, which boots a JVM).
/// Falls back to `input` commands when the device can't be written.
/// </summary>
public sealed class InputInjector : IDisposable
{
    readonly string _serial;
    readonly int _screenW, _screenH;
    Process? _p;

    string? _touchDev;
    int _maxX, _maxY, _trackId;
    bool _hasSlot, _hasMajor, _hasToolFinger, _is64;
    readonly Dictionary<int, (string Dev, int Code)> _keys = new(); // Android keycode -> (device, linux code)

    // Preferred path: a tiny Java helper (helper/AdbViewInput.java) kept running via app_process that injects
    // through InputManager. Works on Android 10+ where /dev/input is no longer writable by the shell.
    volatile bool _helper;
    const string HelperPath = "/data/local/tmp/adbview-input.dex";
    const string HelperCmd = "CLASSPATH=" + HelperPath + " app_process / AdbViewInput";

    public bool DirectTouch => _helper || _touchDev != null;

    public InputInjector(string serial, int screenW, int screenH)
    {
        _serial = serial; _screenW = screenW; _screenH = screenH;
    }

    // Android keycode -> candidate linux key names/codes
    static readonly (int Android, string Name, int Code)[] KeyMap =
    {
        (4, "KEY_BACK", 158), (3, "KEY_HOMEPAGE", 172), (3, "KEY_HOME", 102), (187, "KEY_APPSELECT", 580),
        (26, "KEY_POWER", 116), (24, "KEY_VOLUMEUP", 115), (25, "KEY_VOLUMEDOWN", 114),
    };

    public async Task InitAsync()
    {
        if (await StartHelperAsync()) return;
        var (_, abi) = await Tools.RunAdb($"-s {_serial} shell getprop ro.product.cpu.abi", 8000);
        _is64 = abi.Contains("64");
        var (code, info) = await Tools.RunAdb($"-s {_serial} shell getevent -pl", 10000);
        if (code != 0) return;

        var names = new Dictionary<string, HashSet<string>>();
        var maxes = new Dictionary<string, (int X, int Y)>();
        string? dev = null;
        foreach (var line in info.Split('\n'))
        {
            var m = Regex.Match(line, @"add device \d+: (\S+)");
            if (m.Success) { dev = m.Groups[1].Value; names[dev] = new(); continue; }
            if (dev == null) continue;
            foreach (Match k in Regex.Matches(line, @"\b((?:KEY|BTN|ABS|INPUT_PROP)_[A-Z0-9_]+)")) names[dev].Add(k.Groups[1].Value);
            var mx = Regex.Match(line, @"ABS_MT_POSITION_([XY])\s*:.*\bmax (\d+)");
            if (mx.Success)
            {
                maxes.TryGetValue(dev, out var v);
                int val = int.Parse(mx.Groups[2].Value);
                maxes[dev] = mx.Groups[1].Value == "X" ? (val, v.Y) : (v.X, val);
            }
        }

        // Prefer the real touchscreen (INPUT_PROP_DIRECT): Samsung etc. also expose a virtual
        // touchpad (INPUT_PROP_POINTER) that would swallow every touch.
        var touch = maxes.Where(kv => kv.Value.X > 0 && kv.Value.Y > 0 && names[kv.Key].Contains("ABS_MT_TRACKING_ID"))
            .OrderByDescending(kv => names[kv.Key].Contains("INPUT_PROP_DIRECT"))
            .ThenBy(kv => names[kv.Key].Contains("INPUT_PROP_POINTER"))
            .FirstOrDefault();
        // The shell may not be allowed to write input devices (SELinux, newer Android)
        if (touch.Key != null && !(await Tools.RunAdb($"-s {_serial} shell \"exec 3> {touch.Key} && echo __ok\"", 8000)).Output.Contains("__ok"))
            touch = default;
        if (touch.Key != null)
        {
            _touchDev = touch.Key;
            (_maxX, _maxY) = touch.Value;
            var n = names[_touchDev];
            _hasSlot = n.Contains("ABS_MT_SLOT");
            _hasMajor = n.Contains("ABS_MT_TOUCH_MAJOR");
            _hasToolFinger = n.Contains("BTN_TOOL_FINGER");
        }
        foreach (var (android, name, linux) in KeyMap)
        {
            if (_keys.ContainsKey(android)) continue;
            var d = names.FirstOrDefault(kv => kv.Value.Contains(name)).Key;
            if (d != null) _keys[android] = (d, linux);
        }
    }

    async Task<bool> StartHelperAsync()
    {
        try
        {
            var local = Path.Combine(Path.GetTempPath(), "adbview-input.dex");
            await using (var res = typeof(InputInjector).Assembly.GetManifestResourceStream("adbview-input.dex")!)
            await using (var fs = File.Create(local))
                await res.CopyToAsync(fs);
            var (code, _) = await Tools.RunAdb($"-s {_serial} push \"{local}\" {HelperPath}", 15000);
            if (code != 0) return false;
            var (_, o) = await Tools.RunAdb($"-s {_serial} shell {HelperCmd} --check", 15000);
            if (!o.Contains("__pv_ready")) return false;
            lock (_q)
            {
                _helper = true;
                _q.Clear(); _pendingMove = -1; // shell-style commands queued meanwhile mean nothing to the helper
                try { _p?.Kill(); } catch { }   // writer restarts the channel as the helper
            }
            return true;
        }
        catch { return false; }
    }

    // ---------- touch ----------
    int Tx(int x) => (int)((long)x * (_maxX + 1) / _screenW);
    int Ty(int y) => (int)((long)y * (_maxY + 1) / _screenH);

    public void TouchDown(int x, int y)
    {
        _lastX = x; _lastY = y;
        if (_helper) { Queue($"d {x} {y}"); return; }
        var e = new EventWriter(_is64);
        if (_hasSlot) e.Add(3, 0x2f, 0);
        e.Add(3, 0x39, ++_trackId & 0xFFFF);
        e.Add(3, 0x35, Tx(x)); e.Add(3, 0x36, Ty(y));
        if (_hasMajor) e.Add(3, 0x30, 6);
        e.Add(1, 330, 1);
        if (_hasToolFinger) e.Add(1, 325, 1);
        e.Add(0, 0, 0);
        Write(_touchDev!, e);
    }

    public void TouchMove(int x, int y)
    {
        _lastX = x; _lastY = y;
        if (_helper) { Queue($"m {x} {y}", coalesce: true); return; }
        var e = new EventWriter(_is64);
        e.Add(3, 0x35, Tx(x)); e.Add(3, 0x36, Ty(y)); e.Add(0, 0, 0);
        Write(_touchDev!, e, coalesce: true);
    }

    int _lastX, _lastY;

    public void TouchUp()
    {
        if (_helper) { Queue($"u {_lastX} {_lastY}"); return; }
        var e = new EventWriter(_is64);
        if (_hasSlot) e.Add(3, 0x2f, 0);
        e.Add(3, 0x39, -1);
        e.Add(1, 330, 0);
        if (_hasToolFinger) e.Add(1, 325, 0);
        e.Add(0, 0, 0);
        Write(_touchDev!, e);
    }

    /// <summary>Smooth swipe made of real touch events (used for mouse wheel).</summary>
    public async Task Swipe(int x1, int y1, int x2, int y2, int steps = 8, int stepMs = 12)
    {
        if (!DirectTouch) { Send($"input swipe {x1} {y1} {x2} {y2} {steps * stepMs}"); return; }
        TouchDown(x1, y1);
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(stepMs);
            TouchMove(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
        }
        TouchUp();
    }

    // ---------- keys ----------
    public void Key(int androidKeyCode)
    {
        if (_helper) { Queue($"k {androidKeyCode}"); return; }
        if (_keys.TryGetValue(androidKeyCode, out var k))
        {
            var e = new EventWriter(_is64);
            e.Add(1, k.Code, 1); e.Add(0, 0, 0);
            e.Add(1, k.Code, 0); e.Add(0, 0, 0);
            Write(k.Dev, e);
        }
        else Send($"input keyevent {androidKeyCode}");
    }

    public void Text(string text)
    {
        if (_helper) { Queue("t " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text))); return; }
        Send($"input text '{text.Replace("'", "'\\''").Replace(" ", "%s")}'");
    }

    // Each input device is opened once on the shell side (exec N> dev); writes then cost < 1 ms.
    readonly Dictionary<string, int> _fds = new();

    void Write(string dev, EventWriter e, bool coalesce = false)
    {
        lock (_q)
        {
            if (!_fds.TryGetValue(dev, out int fd))
            {
                fd = 3 + _fds.Count;
                _fds[dev] = fd;
                Enqueue($"exec {fd}> {dev}");
            }
            Queue($"print -n -u{fd} '{e}'", coalesce);
        }
    }

    void Queue(string cmd, bool coalesce = false)
    {
        lock (_q)
        {
            // A newer move replaces one still queued: on a slow link we drop stale points instead of lagging behind.
            if (coalesce && _pendingMove >= 0 && _pendingMove == _q.Count - 1) { _q[_pendingMove] = cmd; return; }
            Enqueue(cmd);
            _pendingMove = coalesce ? _q.Count - 1 : -1;
        }
    }

    // Commands go out on one background thread (never blocks the UI); everything pending is sent in one write.
    readonly List<string> _q = new();
    int _pendingMove = -1;
    Thread? _writer;
    volatile bool _disposed;
    long _pingSent;

    /// <summary>Round-trip time PC → phone → PC of the input channel, in ms (-1 = unknown).</summary>
    public int LatencyMs { get; private set; } = -1;

    public void Send(string cmd)
    {
        lock (_q) { Enqueue(cmd); _pendingMove = -1; }
    }

    /// <summary>Measures the real round trip on the same channel touches use.</summary>
    public void Ping()
    {
        long t = Interlocked.Read(ref _pingSent);
        if (t != 0 && Stopwatch.GetElapsedTime(t).TotalSeconds < 5) return; // previous ping still in flight
        Interlocked.Exchange(ref _pingSent, Stopwatch.GetTimestamp());
        Send(_helper ? "p" : "echo __pv_pong");
    }

    void Enqueue(string cmd)
    {
        _q.Add(cmd);
        Monitor.Pulse(_q);
        if (_writer == null)
        {
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "InputWriter", Priority = ThreadPriority.AboveNormal };
            _writer.Start();
        }
    }

    void WriterLoop()
    {
        var sb = new StringBuilder();
        while (true)
        {
            bool fresh = _p == null || _p.HasExited;
            lock (_q)
            {
                while (_q.Count == 0 && !_disposed) Monitor.Wait(_q);
                if (_disposed) return;
                if (fresh && !_helper) // new shell: device fds must be opened again
                {
                    _q.RemoveAll(c => c.StartsWith("exec "));
                    _q.InsertRange(0, _fds.Select(kv => $"exec {kv.Value}> {kv.Key}"));
                }
                foreach (var c in _q) sb.Append(c).Append('\n');
                _q.Clear();
                _pendingMove = -1;
            }
            try
            {
                if (fresh) StartShell();
                _p!.StandardInput.Write(sb.ToString());
                _p.StandardInput.Flush();
            }
            catch { }
            sb.Clear();
        }
    }

    void StartShell()
    {
        _p?.Dispose();
        var psi = Tools.Psi(Tools.Adb, _helper ? $"-s {_serial} shell {HelperCmd}" : $"-s {_serial} shell");
        psi.RedirectStandardInput = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        var p = Process.Start(psi)!;
        ChildJob.Add(p);
        p.OutputDataReceived += (_, a) =>
        {
            if (a.Data == null || !a.Data.Contains("__pv_pong")) return;
            long t = Interlocked.Exchange(ref _pingSent, 0);
            if (t != 0) LatencyMs = (int)Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _p = p;
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_q) Monitor.PulseAll(_q);
        try { if (_p is { HasExited: false }) _p.Kill(); } catch { }
        _p?.Dispose();
    }

    /// <summary>Builds struct input_event records as \xHH escapes for the shell's builtin print.</summary>
    sealed class EventWriter(bool is64)
    {
        readonly StringBuilder _sb = new();

        public void Add(int type, int code, int value)
        {
            for (int i = 0; i < (is64 ? 16 : 8); i++) _sb.Append("\\x00"); // timeval (kernel ignores it)
            Bytes(type, 2); Bytes(code, 2); Bytes(value, 4);
        }

        void Bytes(int v, int n)
        {
            for (int i = 0; i < n; i++) _sb.Append("\\x").Append(((v >> (8 * i)) & 0xFF).ToString("x2"));
        }

        public override string ToString() => _sb.ToString();
    }
}
