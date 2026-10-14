using System.Diagnostics;
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

    public bool DirectTouch => _touchDev != null;

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
            foreach (Match k in Regex.Matches(line, @"\b((?:KEY|BTN|ABS)_[A-Z0-9_]+)")) names[dev].Add(k.Groups[1].Value);
            var mx = Regex.Match(line, @"ABS_MT_POSITION_([XY])\s*:.*\bmax (\d+)");
            if (mx.Success)
            {
                maxes.TryGetValue(dev, out var v);
                int val = int.Parse(mx.Groups[2].Value);
                maxes[dev] = mx.Groups[1].Value == "X" ? (val, v.Y) : (v.X, val);
            }
        }

        var touch = maxes.FirstOrDefault(kv => kv.Value.X > 0 && kv.Value.Y > 0 && names[kv.Key].Contains("ABS_MT_TRACKING_ID"));
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

    // ---------- touch ----------
    int Tx(int x) => (int)((long)x * (_maxX + 1) / _screenW);
    int Ty(int y) => (int)((long)y * (_maxY + 1) / _screenH);

    public void TouchDown(int x, int y)
    {
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
        var e = new EventWriter(_is64);
        e.Add(3, 0x35, Tx(x)); e.Add(3, 0x36, Ty(y)); e.Add(0, 0, 0);
        Write(_touchDev!, e);
    }

    public void TouchUp()
    {
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
        if (_keys.TryGetValue(androidKeyCode, out var k))
        {
            var e = new EventWriter(_is64);
            e.Add(1, k.Code, 1); e.Add(0, 0, 0);
            e.Add(1, k.Code, 0); e.Add(0, 0, 0);
            Write(k.Dev, e);
        }
        else Send($"input keyevent {androidKeyCode}");
    }

    // Each input device is opened once on the shell side (exec N> dev); writes then cost < 1 ms.
    readonly Dictionary<string, int> _fds = new();

    void Write(string dev, EventWriter e)
    {
        EnsureShell();
        if (!_fds.TryGetValue(dev, out int fd))
        {
            fd = 3 + _fds.Count;
            _fds[dev] = fd;
            Send($"exec {fd}> {dev}");
        }
        Send($"print -n -u{fd} '{e}'");
    }

    void EnsureShell()
    {
        if (_p == null || _p.HasExited) { Send(""); }
    }

    public void Send(string cmd)
    {
        try
        {
            if (_p == null || _p.HasExited)
            {
                _fds.Clear();
                _p?.Dispose();
                var psi = Tools.Psi(Tools.Adb, $"-s {_serial} shell");
                psi.RedirectStandardInput = true;
                _p = Process.Start(psi)!;
                ChildJob.Add(_p);
                _p.OutputDataReceived += (_, _) => { };
                _p.ErrorDataReceived += (_, _) => { };
                _p.BeginOutputReadLine();
                _p.BeginErrorReadLine();
            }
            _p.StandardInput.Write(cmd + "\n");
            _p.StandardInput.Flush();
        }
        catch { }
    }

    public void Dispose()
    {
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
