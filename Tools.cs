using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AdbView;

/// <summary>Locates adb and runs short-lived commands.</summary>
public static class Tools
{
    public static string Adb { get; private set; } = "adb";

    public static void Locate()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Adb = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            FromPath("adb.exe"),
            Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"),
        }.FirstOrDefault(p => p != null && File.Exists(p)) ?? "adb";
    }

    static string? FromPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => { try { return Path.Combine(d.Trim(), exe); } catch { return null; } })
            .FirstOrDefault(p => p != null && File.Exists(p));

    public static ProcessStartInfo Psi(string file, string args) => new(file, args)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
    };

    public static async Task<(int Code, string Output)> RunAdb(string args, int timeoutMs = 15000)
    {
        try
        {
            using var p = Process.Start(Psi(Adb, args))!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, ((await o) + (await e)).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>Streams binary stdout (e.g. screencap) straight to a file.</summary>
    public static async Task<bool> AdbToFile(string args, string file)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Adb, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            await using (var fs = File.Create(file))
                await p.StandardOutput.BaseStream.CopyToAsync(fs);
            await p.WaitForExitAsync();
            return p.ExitCode == 0 && new FileInfo(file).Length > 100;
        }
        catch { return false; }
    }
}

/// <summary>
/// Persistent "adb shell" per device so taps/keys don't spawn a new adb process each time.
/// </summary>
public sealed class InputShell : IDisposable
{
    readonly string _serial;
    Process? _p;

    public InputShell(string serial) => _serial = serial;

    public void Send(string cmd)
    {
        try
        {
            if (_p == null || _p.HasExited)
            {
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
}

/// <summary>Kills child adb processes automatically if this app dies.</summary>
public static class ChildJob
{
    static IntPtr _job;

    public static void Add(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                int len = Marshal.SizeOf(info);
                var ptr = Marshal.AllocHGlobal(len);
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_job, 9, ptr, (uint)len);
                Marshal.FreeHGlobal(ptr);
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
}

public sealed class Settings
{
    public string Quality { get; set; } = "Auto";
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "System";
    public bool SidebarOpen { get; set; } = true;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "adbView", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

public sealed class DeviceItem : INotifyPropertyChanged
{
    public required string Serial { get; init; }
    string _state = "", _model = "";

    public string State { get => _state; set { _state = value; Raise(); } }
    public string Model { get => _model; set { _model = value; Raise(); } }
    public int ScreenW { get; set; }
    public int ScreenH { get; set; }

    public bool IsWifi => Serial.Contains(':') || Serial.Contains("._adb-tls");
    public bool IsOnline => State == "device";
    public string Name => string.IsNullOrEmpty(Model) ? Serial : Model;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise()
    {
        foreach (var n in new[] { nameof(State), nameof(Model), nameof(IsOnline), nameof(Name) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

/// <summary>Very small EN/VI string table.</summary>
public static class L
{
    public static bool Vi { get; set; }

    static readonly Dictionary<string, (string En, string Vi)> T = new()
    {
        ["scanning"] = ("Scanning USB devices…", "Đang quét thiết bị USB…"),
        ["noDevice"] = ("No USB device.\nPlug in a cable and enable USB debugging.", "Chưa có thiết bị USB.\nCắm cáp và bật USB debugging."),
        ["unauthorized"] = ("Tap \"Allow\" for USB debugging on the phone.", "Bấm \"Cho phép\" gỡ lỗi USB trên điện thoại."),
        ["offline"] = ("Device is offline.", "Thiết bị offline."),
        ["connecting"] = ("Connecting…", "Đang kết nối…"),
        ["stopped"] = ("Waiting for device…", "Đang chờ thiết bị…"),
        ["failed"] = ("Could not stream the screen.", "Không stream được màn hình."),
        ["viewing"] = ("Viewing", "Đang xem"),
        ["shotSaved"] = ("Saved", "Đã lưu"),
        ["shotFail"] = ("Screenshot failed", "Chụp màn hình thất bại"),
        ["back"] = ("Back", "Quay lại"),
        ["home"] = ("Home", "Màn hình chính"),
        ["recents"] = ("Recent apps", "Ứng dụng gần đây"),
        ["power"] = ("Power", "Nguồn"),
        ["volUp"] = ("Volume up", "Tăng âm lượng"),
        ["volDown"] = ("Volume down", "Giảm âm lượng"),
        ["shot"] = ("Screenshot", "Chụp màn hình"),
        ["play"] = ("Start / stop viewing", "Xem / Dừng xem"),
        ["settings"] = ("Settings", "Cài đặt"),
        ["device"] = ("Device", "Thiết bị"),
        ["sidebar"] = ("Show / hide toolbar", "Ẩn / hiện thanh công cụ"),
        ["quality"] = ("Quality", "Chất lượng"),
        ["language"] = ("Language", "Ngôn ngữ"),
        ["theme"] = ("Theme", "Giao diện"),
        ["q.Auto"] = ("Auto (smart)", "Tự động (thông minh)"),
        ["q.Low"] = ("Low · 480p", "Thấp · 480p"),
        ["q.Medium"] = ("Medium · 720p", "Trung bình · 720p"),
        ["q.High"] = ("High · 1080p", "Cao · 1080p"),
        ["q.Ultra"] = ("Very high · 1440p", "Rất cao · 1440p"),
        ["q.Original"] = ("Original", "Gốc"),
        ["t.System"] = ("System", "Theo hệ thống"),
        ["t.Light"] = ("Light", "Sáng"),
        ["t.Dark"] = ("Dark", "Tối"),
    };

    public static string S(string key) => T.TryGetValue(key, out var v) ? (Vi ? v.Vi : v.En) : key;
}
