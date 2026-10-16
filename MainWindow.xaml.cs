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
        ("Low", 480, 1_500_000), ("Medium", 720, 3_000_000), ("High", 1080, 6_000_000),
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
        // Auto: smallest level that still covers the on-screen size → sharp image, minimal CPU/bandwidth
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double shown = Math.Max(200, (ScreenArea.ActualWidth > 0 ? ScreenArea.ActualWidth : 400) * dpi);
        // Over the network latency beats sharpness: never above 720p in Auto
        int max = d.IsWifi ? 1 : original;
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
        _ = input.InitAsync();
        // Over the network: lower bitrate so Wi-Fi jitter doesn't stall the stream
        int bitrate = d.IsWifi ? Levels[_level].Bitrate * 3 / 5 : Levels[_level].Bitrate;
        var s = new ScreenStream(d.Serial, w, h, bitrate);
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
        StatsText.Text = $"{(ms >= 0 ? ms : "–")}\nms\n{dec - _lastFrames}\nfps";
        StatsText.Foreground = ms < 0 ? Brushes.Gray : ms < 40 ? Brushes.MediumSeaGreen : ms < 100 ? Brushes.Goldenrod : Brushes.IndianRed;
        StatsText.Visibility = _gotFrame ? Visibility.Visible : Visibility.Collapsed;
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
        StatsText.Visibility = Visibility.Collapsed;
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
        int code = e.Key switch
        {
            System.Windows.Input.Key.Enter => 66, System.Windows.Input.Key.Back => 67, System.Windows.Input.Key.Delete => 112, System.Windows.Input.Key.Escape => 4, System.Windows.Input.Key.Tab => 61,
            System.Windows.Input.Key.Left => 21, System.Windows.Input.Key.Right => 22, System.Windows.Input.Key.Up => 19, System.Windows.Input.Key.Down => 20, _ => 0,
        };
        if (code != 0) { SendKey(code); e.Handled = true; }
    }

    // ================= Buttons =================
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
