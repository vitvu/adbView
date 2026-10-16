using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AdbView;

public partial class MainWindow : Window
{
    readonly ObservableCollection<DeviceItem> _devices = new();
    readonly Settings _cfg = Settings.Load();
    ScreenStream? _stream;
    InputInjector? _input;
    WriteableBitmap? _bmp;
    DeviceItem? _viewing;
    Process? _tracker;
    bool _closing, _loading = true, _gotFrame;
    int _level; // index into Levels for the running stream

    // Quality levels: key, short side px (0 = native), bitrate
    static readonly (string Key, int Short, int Bitrate)[] Levels =
    {
        ("Low", 480, 1_000_000), ("Medium", 720, 3_000_000), ("High", 1080, 6_000_000),
        ("Ultra", 1440, 10_000_000), ("Original", 0, 12_000_000),
    };
    List<string> _qualityKeys = new() { "Auto" }; // what the quality box currently lists
    static readonly string[] Themes = { "System", "Light", "Dark" };

    public MainWindow()
    {
        InitializeComponent();
        L.Vi = _cfg.Language == "vi";
        ApplyTheme(_cfg.Theme);
        DeviceBox.ItemsSource = _devices;
        LanguageBox.SelectedIndex = L.Vi ? 1 : 0;
        ApplyTexts();
        ShowPlaceholder(L.S("scanning"));
        _loading = false;

        Loaded += async (_, _) =>
        {
            // PC clipboard → phone: Windows tells us about every copy (WM_CLIPBOARDUPDATE), no polling
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            AddClipboardFormatListener(hwnd);
            System.Windows.Interop.HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x031D) OnPcClipboardChanged();
                return IntPtr.Zero;
            });
            Tools.Locate();
            await Tools.RunAdb("start-server", 20000);
            _ = TrackLoop();
            foreach (var host in _cfg.WifiHosts.ToList()) _ = Tools.RunAdb($"connect {host}", 8000);
        };
        Closing += (_, _) =>
        {
            _closing = true;
            _cfg.Save();
            StopView(showMessage: false);
            try { _tracker?.Kill(); } catch { }
        };
    }

    // ================= Texts / language =================
    void ApplyTexts()
    {
        BackBtn.ToolTip = L.S("back"); HomeBtn.ToolTip = L.S("home"); RecentsBtn.ToolTip = L.S("recents");
        PowerBtn.ToolTip = L.S("power"); VolUpBtn.ToolTip = L.S("volUp"); VolDownBtn.ToolTip = L.S("volDown");
        ShotBtn.ToolTip = L.S("shot"); SettingsBtn.ToolTip = L.S("settings");
        DeviceLabel.Text = L.S("device");
        WirelessLabel.Text = L.S("wireless");
        ConnectBtn.Content = L.S("connect"); PairBtn.Content = L.S("pair"); TcpipBtn.Content = L.S("tcpip");
        AddrBox.ToolTip = L.S("addrTip"); PairCodeBox.ToolTip = L.S("codeTip");
        QualityLabel.Text = L.S("quality"); LanguageLabel.Text = L.S("language"); ThemeLabel.Text = L.S("theme");

        bool was = _loading; _loading = true;
        FillQualities(Selected);
        ThemeBox.ItemsSource = Themes.Select(t => L.S("t." + t)).ToList();
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, _cfg.Theme));
        _loading = was;
    }

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _cfg.Language = LanguageBox.SelectedIndex == 1 ? "vi" : "en";
        L.Vi = _cfg.Language == "vi";
        ApplyTexts();
        _cfg.Save();
        if (_viewing == null) RefreshPlaceholder();
    }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0) return;
        _cfg.Theme = Themes[ThemeBox.SelectedIndex];
        ApplyTheme(_cfg.Theme);
        _cfg.Save();
    }

    void ApplyTheme(string t)
    {
#pragma warning disable WPF0001
        Application.Current.ThemeMode = t switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
    }

    /// <summary>Only offers levels below the phone's real resolution, plus "Original (Np)".</summary>
    void FillQualities(DeviceItem? d)
    {
        int shortSide = d is { ScreenW: > 0 } ? Math.Min(d.ScreenW, d.ScreenH) : 0;
        _qualityKeys = new() { "Auto" };
        var labels = new List<string> { L.S("q.Auto") };
        foreach (var lv in Levels)
        {
            if (lv.Short == 0)
            {
                _qualityKeys.Add(lv.Key);
                labels.Add(L.S("q.Original") + (shortSide > 0 ? $" · {shortSide}p" : ""));
            }
            else if (shortSide == 0 || lv.Short < shortSide)
            {
                _qualityKeys.Add(lv.Key);
                labels.Add(L.S("q." + lv.Key));
            }
        }
        bool was = _loading; _loading = true;
        QualityBox.ItemsSource = labels;
        int idx = _qualityKeys.IndexOf(_cfg.Quality);
        QualityBox.SelectedIndex = idx >= 0 ? idx : _qualityKeys.Count - 1; // level too high for this phone → Original
        _loading = was;
    }

    void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QualityBox.SelectedIndex < 0) return;
        _cfg.Quality = _qualityKeys[QualityBox.SelectedIndex];
        _cfg.Save();
        if (_viewing != null) StartView(_viewing); // restart with new quality
    }

    // ================= USB device tracking (event-driven, auto) =================
    async Task TrackLoop()
    {
        while (!_closing)
        {
            try
            {
                using var p = Process.Start(Tools.Psi(Tools.Adb, "track-devices"))!;
                _tracker = p;
                var r = p.StandardOutput;
                var len = new char[4];
                while (!_closing && await ReadExact(r, len))
                {
                    int n = Convert.ToInt32(new string(len), 16);
                    var buf = new char[n];
                    if (n > 0 && !await ReadExact(r, buf)) break;
                    ApplyDevices(new string(buf));
                }
            }
            catch { }
            if (!_closing) { ApplyDevices(""); await Task.Delay(2000); }
        }
    }

    static async Task<bool> ReadExact(StreamReader r, char[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await r.ReadAsync(buf.AsMemory(off));
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    void ApplyDevices(string text)
    {
        var now = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t')).Where(a => a.Length >= 2)
            .Select(a => new DeviceItem { Serial = a[0].Trim(), State = a[1].Trim() })
            .ToDictionary(d => d.Serial, d => d.State);

        foreach (var d in _devices.Where(d => !now.ContainsKey(d.Serial)).ToList())
        {
            if (d == _viewing) StopView(showMessage: false);
            _devices.Remove(d);
        }
        foreach (var (serial, state) in now)
        {
            var d = _devices.FirstOrDefault(x => x.Serial == serial);
            if (d == null) { d = new DeviceItem { Serial = serial }; _devices.Add(d); }
            bool cameOnline = state == "device" && d.State != "device";
            d.State = state;
            if (cameOnline) _ = OnDeviceOnline(d);
            else if (state != "device" && d == _viewing) StopView(showMessage: false);
        }

        if (_pendingSelect != null && _devices.FirstOrDefault(x => x.Serial == _pendingSelect) is { } want)
        {
            _pendingSelect = null;
            DeviceBox.SelectedItem = want;
        }
        if (DeviceBox.SelectedItem == null && _devices.Count > 0) DeviceBox.SelectedIndex = 0;
        if (_viewing == null) RefreshPlaceholder();
    }

    void RefreshPlaceholder()
    {
        if (_devices.Count == 0) ShowPlaceholder(L.S("noDevice"));
        else if (Selected is { State: "unauthorized" }) ShowPlaceholder(L.S("unauthorized"));
        else if (Selected is { IsOnline: false }) ShowPlaceholder(L.S("offline"));
        else ShowPlaceholder(L.S("stopped"));
    }

    async Task OnDeviceOnline(DeviceItem d)
    {
        var (c1, model) = await Tools.RunAdb($"-s {d.Serial} shell getprop ro.product.model", 8000);
        if (c1 == 0 && model.Length is > 0 and < 60) d.Model = model;
        var (_, size) = await Tools.RunAdb($"-s {d.Serial} shell wm size", 8000);
        var m = Regex.Matches(size, @"(\d+)x(\d+)").LastOrDefault();
        if (m != null) { d.ScreenW = int.Parse(m.Groups[1].Value); d.ScreenH = int.Parse(m.Groups[2].Value); }
        if (Selected == d && _viewing == null) StartView(d);
    }

    DeviceItem? Selected => DeviceBox.SelectedItem as DeviceItem;

    void DeviceBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { IsOnline: true, ScreenW: > 0 } d && d != _viewing) StartView(d);
    }

    // ================= Streaming =================
    int PickLevel(DeviceItem d)
    {
        int shortSide = Math.Min(d.ScreenW, d.ScreenH);
        int original = Levels.Length - 1;
        int q = Array.FindIndex(Levels, l => l.Key == _cfg.Quality);
        if (q >= 0) return Levels[q].Short == 0 || Levels[q].Short >= shortSide ? original : q;
        // Auto = lowest latency: smallest level that covers the on-screen size, never above 720p on USB
        // and always the lowest level over the network (a slightly soft image is fine, speed comes first)
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double shown = Math.Max(200, (ScreenArea.ActualWidth > 0 ? ScreenArea.ActualWidth : 400) * dpi);
        int max = d.IsWifi ? 0 : 1;
        for (int i = 0; i < original; i++)
        {
            if (Levels[i].Short >= shortSide) return Math.Min(original, max);
            if (Levels[i].Short >= shown || i == max) return i;
        }
        return original;
    }

    static (int W, int H) StreamSize(DeviceItem d, int level)
    {
        int shortSide = Math.Min(d.ScreenW, d.ScreenH);
        int target = Levels[level].Short == 0 ? shortSide : Math.Min(Levels[level].Short, shortSide);
        if (target >= shortSide) return (d.ScreenW, d.ScreenH); // native: exact size, no scaling blur
        double k = (double)target / shortSide;
        static int R8(double v) => Math.Max(16, (int)Math.Round(v / 8) * 8);
        return (R8(d.ScreenW * k), R8(d.ScreenH * k));
    }

    void StartView(DeviceItem d, int? level = null)
    {
        StopView(showMessage: false);
        if (!d.IsOnline || d.ScreenW == 0) return;
        bool hasOnScreenNav = (double)Math.Max(d.ScreenW, d.ScreenH) / Math.Min(d.ScreenW, d.ScreenH) > 16.0 / 9 + 0.02;
        NavBar.Visibility = hasOnScreenNav ? Visibility.Collapsed : Visibility.Visible;
        FillQualities(d);
        FitWindow(d);
        _level = level ?? PickLevel(d);
        var (w, h) = StreamSize(d, _level);
        _viewing = d;
        _gotFrame = false;
        var input = new InputInjector(d.Serial, d.ScreenW, d.ScreenH);
        _input = input;
        input.ClipboardChanged += t => Dispatcher.BeginInvoke(() => { if (_input == input) OnPhoneClipboardChanged(t); });
        _ = input.InitAsync();
        // Over the network: lower bitrate so Wi-Fi jitter doesn't stall the stream
        int bitrate = d.IsWifi ? Levels[_level].Bitrate * 3 / 5 : Levels[_level].Bitrate;
        var s = new ScreenStream(d.Serial, w, h, bitrate, d.IsWifi);
        s.FrameReady += () => Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Render(s));
        s.Failed += err => Dispatcher.BeginInvoke(() => OnStreamFailed(s, err));
        _stream = s;
        ShowPlaceholder(L.S("connecting"));
        SetStatus($"{L.S("viewing")} {d.Name} · {w}×{h}");
        s.Start();
        _statsTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowStats(), Dispatcher);
        _statsTimer.Start();
    }

    static readonly bool Debug = Environment.GetEnvironmentVariable("PV_DEBUG") == "1";
    DispatcherTimer? _statsTimer;
    long _lastFrames, _shown, _lastShown;

    void ShowStats()
    {
        if (_stream is not { } s || _viewing is not { } d) { _statsTimer?.Stop(); return; }
        long dec = s.FramesDecoded;
        _input?.Ping();
        int ms = _input?.LatencyMs ?? -1;
        string ping = ms >= 0 ? $"{ms} ms" : "– ms";
        Title = $"{d.Name} · {ping} · {dec - _lastFrames} fps · {(d.IsWifi ? "Wi-Fi" : "USB")}";
        SetStatus($"{d.Name} · {s.Width}×{s.Height} · {dec - _lastFrames} fps · {ping}");
        if (Debug)
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "pv_stats.log"),
                $"{DateTime.Now:HH:mm:ss} bytes={s.BytesIn} nals={s.NalsIn} decoded={dec} shown={_shown} decMs={s.DecodeTicks * 1000 / Stopwatch.Frequency} convMs={s.ConvTicks * 1000 / Stopwatch.Frequency} size={s.Width}x{s.Height}{Environment.NewLine}");
        _lastFrames = dec; _lastShown = _shown;
    }

    void Render(ScreenStream s)
    {
        if (s != _stream || s.TakeFrame(out int w, out int h) is not { } buf) return;
        if (_bmp == null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
        {
            _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            ScreenImg.Source = _bmp;
        }
        _bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        _shown++;
        s.ReturnFrame(buf);
        if (!_gotFrame) { _gotFrame = true; Placeholder.Visibility = Visibility.Collapsed; }
    }

    void OnStreamFailed(ScreenStream s, string err)
    {
        if (s != _stream || _viewing is not { } d) return;
        // Encoder refused this size before any frame → step down quality automatically
        if (!_gotFrame && _level > 0) { StartView(d, _level - 1); return; }
        StopView(showMessage: false);
        ShowPlaceholder(L.S("failed") + "\n" + err.Split('\n').FirstOrDefault()?.Trim());
    }

    void StopView(bool showMessage = true)
    {
        _stream?.Stop();
        _stream = null;
        _input?.Dispose();
        _input = null;
        _viewing = null;
        Title = "adbView";
        ScreenImg.Source = null;
        _bmp = null;
        if (showMessage) ShowPlaceholder(L.S("stopped"));
    }

    void ShowPlaceholder(string text)
    {
        PlaceholderText.Text = text;
        Placeholder.Visibility = Visibility.Visible;
    }

    /// <summary>Resize the window so the phone screen fills it with no black bars.</summary>
    void FitWindow(DeviceItem d)
    {
        if (WindowState != WindowState.Normal || _fitted == d.Serial) return;
        _fitted = d.Serial;
        UpdateLayout();
        double chromeW = ActualWidth - ScreenArea.ActualWidth;                     // sidebar + borders
        double navH = NavBar.Visibility == Visibility.Visible ? NavBar.ActualHeight > 0 ? NavBar.ActualHeight : 38 : 0;
        double chromeH = ActualHeight - ScreenArea.ActualHeight + navH;           // title bar + nav
        double screenH = Math.Min(SystemParameters.WorkArea.Height * 0.88 - chromeH, 900);
        double screenW = screenH * Math.Min(d.ScreenW, d.ScreenH) / Math.Max(d.ScreenW, d.ScreenH);
        Width = screenW + chromeW;
        Height = screenH + chromeH;
        Left = Math.Max(SystemParameters.WorkArea.Left, (SystemParameters.WorkArea.Width - Width) / 2);
        Top = Math.Max(SystemParameters.WorkArea.Top, (SystemParameters.WorkArea.Height - Height) / 2);
        UpdateLayout(); // so Auto quality sees the final on-screen size
    }

    string? _fitted;

    // ================= Touch / keyboard =================
    Point _downPos;
    DateTime _downAt;

    bool ToDevice(Point p, out int x, out int y)
    {
        x = y = 0;
        if (_viewing is not { } d || ScreenImg.ActualWidth <= 0) return false;
        x = (int)Math.Clamp(p.X / ScreenImg.ActualWidth * d.ScreenW, 0, d.ScreenW - 1);
        y = (int)Math.Clamp(p.Y / ScreenImg.ActualHeight * d.ScreenH, 0, d.ScreenH - 1);
        return true;
    }

    void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ScreenArea.Focus();
        _downPos = e.GetPosition(ScreenImg);
        _downAt = DateTime.UtcNow;
        ScreenImg.CaptureMouse();
        if (_input is { DirectTouch: true } && ToDevice(_downPos, out int x, out int y)) _input.TouchDown(x, y);
    }

    DateTime _lastMove;

    void Screen_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ScreenImg.IsMouseCaptured || _input is not { DirectTouch: true }) return;
        var now = DateTime.UtcNow;
        if ((now - _lastMove).TotalMilliseconds < 12) return; // ~80 Hz is plenty
        _lastMove = now;
        if (ToDevice(e.GetPosition(ScreenImg), out int x, out int y)) _input.TouchMove(x, y);
    }

    void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!ScreenImg.IsMouseCaptured) return;
        ScreenImg.ReleaseMouseCapture();
        if (_input is { DirectTouch: true })
        {
            if (ToDevice(e.GetPosition(ScreenImg), out int ux, out int uy)) _input.TouchMove(ux, uy);
            _input.TouchUp();
            return;
        }
        // Fallback: Android "input" command (slower)
        var up = e.GetPosition(ScreenImg);
        if (!ToDevice(_downPos, out int x1, out int y1) || !ToDevice(up, out int x2, out int y2)) return;
        int ms = (int)(DateTime.UtcNow - _downAt).TotalMilliseconds;
        if ((up - _downPos).Length < 6)
            _input?.Send(ms > 450 ? $"input swipe {x1} {y1} {x1} {y1} {ms}" : $"input tap {x1} {y1}");
        else
            _input?.Send($"input swipe {x1} {y1} {x2} {y2} {Math.Clamp(ms, 60, 2000)}");
    }

    void Screen_RightUp(object sender, MouseButtonEventArgs e) => SendKey(4);

    void Screen_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewing is not { } d) return;
        int x = d.ScreenW / 2, y1 = d.ScreenH / 2;
        int y2 = y1 + (e.Delta > 0 ? 1 : -1) * d.ScreenH / 4;
        _ = _input?.Swipe(x, y1, x, y2);
    }

    void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_viewing == null || !ScreenArea.IsKeyboardFocused || string.IsNullOrEmpty(e.Text) || e.Text[0] < ' ') return;
        _input?.Text(e.Text);
        e.Handled = true;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewing == null || !ScreenArea.IsKeyboardFocused) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Alt && AltShortcut(key)) { e.Handled = true; return; }
        int code = key switch
        {
            System.Windows.Input.Key.Enter => 66, System.Windows.Input.Key.Back => 67, System.Windows.Input.Key.Delete => 112, System.Windows.Input.Key.Escape => 4, System.Windows.Input.Key.Tab => 61,
            System.Windows.Input.Key.Left => 21, System.Windows.Input.Key.Right => 22, System.Windows.Input.Key.Up => 19, System.Windows.Input.Key.Down => 20,
            System.Windows.Input.Key.Home => 122, System.Windows.Input.Key.End => 123, System.Windows.Input.Key.PageUp => 92, System.Windows.Input.Key.PageDown => 93,
            >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z when (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0
                => 29 + (key - System.Windows.Input.Key.A), // KEYCODE_A.. — plain letters go through text input instead
            _ => 0,
        };
        if (code == 0) return;
        e.Handled = true;
        if (_input is not { CanChord: true } input) { if (mods == ModifierKeys.None) SendKey(code); return; }

        // Ctrl+V: push the PC clipboard first so the phone pastes exactly what was copied on the PC
        if (mods == ModifierKeys.Control && key == System.Windows.Input.Key.V)
        {
            _forceClip = true; // phone clipboard may have been replaced (e.g. by Vietnamese typing)
            OnPcClipboardChanged();
            _forceClip = false;
            input.Key(279); // KEYCODE_PASTE
            return;
        }
        // Android KeyEvent meta state: same shortcuts as a hardware keyboard on the phone
        int meta = 0;
        if ((mods & ModifierKeys.Control) != 0) meta |= 0x1000 | 0x2000; // META_CTRL_ON | META_CTRL_LEFT_ON
        if ((mods & ModifierKeys.Shift) != 0) meta |= 0x1 | 0x40;        // META_SHIFT_ON | META_SHIFT_LEFT_ON
        if ((mods & ModifierKeys.Alt) != 0) meta |= 0x2 | 0x10;          // META_ALT_ON | META_ALT_LEFT_ON
        input.Key(code, meta);
    }

    // ================= Buttons =================
    /// <summary>adbView's own shortcuts (Alt + key), like scrcpy's. Not forwarded to the phone.</summary>
    bool AltShortcut(System.Windows.Input.Key key)
    {
        var serial = _viewing?.Serial;
        switch (key)
        {
            case System.Windows.Input.Key.H: SendKey(3); break;      // Home
            case System.Windows.Input.Key.B: SendKey(4); break;      // Back
            case System.Windows.Input.Key.M: SendKey(187); break;    // Multitasking / recent apps
            case System.Windows.Input.Key.P: SendKey(26); break;     // Power (screen on / off)
            case System.Windows.Input.Key.Up: SendKey(24); break;    // Volume up
            case System.Windows.Input.Key.Down: SendKey(25); break;  // Volume down
            case System.Windows.Input.Key.S: Shot_Click(this, new RoutedEventArgs()); break; // Screenshot to PC
            case System.Windows.Input.Key.N: _ = Tools.RunAdb($"-s {serial} shell cmd statusbar expand-notifications", 8000); break;
            case System.Windows.Input.Key.Q: _ = Tools.RunAdb($"-s {serial} shell cmd statusbar expand-settings", 8000); break;
            case System.Windows.Input.Key.C: _ = Tools.RunAdb($"-s {serial} shell cmd statusbar collapse", 8000); break;
            case System.Windows.Input.Key.R: _ = Tools.RunAdb($"-s {serial} shell \"settings put system accelerometer_rotation 0; settings put system user_rotation $(( ($(settings get system user_rotation) + 1) % 4 ))\"", 8000); break; // Rotate
            default: return false;
        }
        return true;
    }

    void SendKey(int code)
    {
        if (_input != null) _input.Key(code);
        else if (Selected is { IsOnline: true } d) _ = Tools.RunAdb($"-s {d.Serial} shell input keyevent {code}", 8000);
    }
    void Back_Click(object s, RoutedEventArgs e) => SendKey(4);
    void Home_Click(object s, RoutedEventArgs e) => SendKey(3);
    void Recents_Click(object s, RoutedEventArgs e) => SendKey(187);
    void VolDown_Click(object s, RoutedEventArgs e) => SendKey(25);
    void VolUp_Click(object s, RoutedEventArgs e) => SendKey(24);
    void Power_Click(object s, RoutedEventArgs e) => SendKey(26);

    async void Shot_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsOnline: true } d) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "adbView");
        Directory.CreateDirectory(dir);
        var safe = Regex.Replace(d.Name, @"[^\w\-]+", "_");
        var file = Path.Combine(dir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        if (await Tools.AdbToFile($"-s {d.Serial} exec-out screencap -p", file))
            SetStatus($"{L.S("shotSaved")}: Pictures\\adbView\\{Path.GetFileName(file)}");
        else
        {
            try { File.Delete(file); } catch { }
            SetStatus(L.S("shotFail"));
        }
    }

    void SetStatus(string s) => Status.Text = s;

    // ================= Clipboard sync (both ways, UTF-8 so Vietnamese / emoji survive) =================
    bool _forceClip;
    string? _lastClip; // last text synced in either direction, so a copy never bounces back

    void OnPcClipboardChanged()
    {
        if (_input == null) return;
        string? t = null;
        for (int i = 0; i < 3 && t == null; i++) // the clipboard can be briefly locked by the app that wrote it
            try { t = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { Thread.Sleep(20); }
        if (string.IsNullOrEmpty(t) || t.Length > 1_000_000) return;
        if (t == _lastClip && !_forceClip) return;
        _lastClip = t;
        _input.SetClipboard(t);
    }

    void OnPhoneClipboardChanged(string t)
    {
        if (string.IsNullOrEmpty(t) || t == _lastClip) return;
        _lastClip = t;
        for (int i = 0; i < 5; i++)
            try { Clipboard.SetDataObject(t, true); return; } catch { Thread.Sleep(30); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    // ================= Wireless (adb connect / pair / tcpip) =================
    string? _pendingSelect;

    static string WithPort(string addr, int port = 5555)
    {
        addr = addr.Trim();
        return addr.Length == 0 || addr.Contains(':') ? addr : $"{addr}:{port}";
    }

    async Task<bool> ConnectTo(string host)
    {
        SetStatus($"{L.S("connecting")} {host}");
        var (_, o) = await Tools.RunAdb($"connect {host}", 12000);
        bool ok = o.Contains("connected to", StringComparison.OrdinalIgnoreCase) && !o.Contains("cannot", StringComparison.OrdinalIgnoreCase);
        SetStatus(o.Split('\n').FirstOrDefault()?.Trim() ?? "");
        if (!ok) return false;
        _cfg.WifiHosts.Remove(host);
        _cfg.WifiHosts.Insert(0, host);
        if (_cfg.WifiHosts.Count > 8) _cfg.WifiHosts.RemoveRange(8, _cfg.WifiHosts.Count - 8);
        _cfg.Save();
        _pendingSelect = host;
        if (_devices.FirstOrDefault(x => x.Serial == host) is { } d) { _pendingSelect = null; DeviceBox.SelectedItem = d; }
        return true;
    }

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = WithPort(AddrBox.Text);
        if (host.Length == 0) return;
        ConnectBtn.IsEnabled = false;
        try { await ConnectTo(host); } finally { ConnectBtn.IsEnabled = true; }
    }

    void AddrBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) Connect_Click(sender, e);
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var host = AddrBox.Text.Trim();
        var code = PairCodeBox.Text.Trim();
        if (!host.Contains(':') || code.Length == 0) { SetStatus(L.S("codeTip")); return; }
        PairBtn.IsEnabled = false;
        try
        {
            var (_, o) = await Tools.RunAdb($"pair {host} {code}", 20000);
            SetStatus(o.Split('\n').FirstOrDefault()?.Trim() ?? "");
            if (o.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)) PairCodeBox.Clear();
        }
        finally { PairBtn.IsEnabled = true; }
    }

    /// <summary>USB device → "adb tcpip 5555" → connect to its Wi-Fi IP, so the cable can be unplugged.</summary>
    async void Tcpip_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsOnline: true, IsWifi: false } d) return;
        TcpipBtn.IsEnabled = false;
        try
        {
            var (_, a) = await Tools.RunAdb($"-s {d.Serial} shell ip -f inet addr show wlan0", 8000);
            var m = Regex.Match(a, @"inet (\d+\.\d+\.\d+\.\d+)");
            if (!m.Success)
            {
                var (_, r) = await Tools.RunAdb($"-s {d.Serial} shell ip route", 8000);
                m = Regex.Match(r, @"wlan\S*.*?src (\d+\.\d+\.\d+\.\d+)");
            }
            if (!m.Success) { SetStatus(L.S("noIp")); return; }
            var host = $"{m.Groups[1].Value}:5555";
            AddrBox.Text = host;
            await Tools.RunAdb($"-s {d.Serial} tcpip 5555", 10000);
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(1000);
                if (await ConnectTo(host)) break;
            }
        }
        finally { TcpipBtn.IsEnabled = true; }
    }
}
