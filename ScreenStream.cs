using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AdbView;

/// <summary>
/// Streams the phone screen: adb exec-out screenrecord (H.264) → Annex-B NAL splitter →
/// Windows H.264 decoder → BGRA frame. Runs on its own background thread.
/// screenrecord stops after 180 s, so it is restarted transparently.
/// </summary>
public sealed unsafe class ScreenStream
{
    readonly string _serial;
    readonly int _width, _height, _bitrate;
    volatile bool _stop;
    Process? _proc;
    Thread? _thread;

    // Latest-frame handoff: the decoder always publishes the newest frame, the UI always shows the newest.
    byte[]? _latest, _spare;
    int _frameW, _frameH;

    /// <summary>Raised on the stream thread when a new frame is ready; call <see cref="TakeFrame"/> on the UI thread.</summary>
    public event Action? FrameReady;

    public int Width => _width;
    public int Height => _height;

    // Stats (for the status line / diagnostics)
    public long BytesIn, NalsIn, FramesDecoded, DecodeTicks, ConvTicks;
    /// <summary>Raised on the stream thread when streaming fails or ends unexpectedly.</summary>
    public event Action<string>? Failed;

    int _idleMs; // how long the pipe must stay quiet before the trailing NAL counts as complete

    public ScreenStream(string serial, int width, int height, int bitrate, bool network = false)
    {
        _serial = serial; _width = width; _height = height; _bitrate = bitrate;
        // Network data arrives in bursts with gaps. Start short for low latency; a cut NAL is detected
        // and doubles this (see RunOnce), so it settles at the smallest value that is safe for the link.
        _idleMs = network ? 16 : 3;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ScreenStream", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
    }

    /// <summary>UI thread: gets the newest frame (or null). Give it back with <see cref="ReturnFrame"/>.</summary>
    public byte[]? TakeFrame(out int w, out int h)
    {
        w = Volatile.Read(ref _frameW); h = Volatile.Read(ref _frameH);
        return Interlocked.Exchange(ref _latest, null);
    }

    public void ReturnFrame(byte[] buf) => Interlocked.CompareExchange(ref _spare, buf, null);

    void Run()
    {
        H264Decoder? dec = null;
        try
        {
            dec = new H264Decoder(_width, _height);
            while (!_stop)
            {
                long bytes = RunOnce(dec, out var err);
                if (_stop) break;
                if (bytes < 1024) { Failed?.Invoke(err); break; } // device refused / unsupported
            }
        }
        catch (Exception ex)
        {
            if (!_stop) Failed?.Invoke(ex.Message);
        }
        finally
        {
            dec?.Dispose();
        }
    }

    long RunOnce(H264Decoder dec, out string error)
    {
        error = "";
        var psi = Tools.Psi(Tools.Adb,
            $"-s {_serial} exec-out screenrecord --output-format=h264 --size {_width}x{_height} " +
            $"--bit-rate {_bitrate} --time-limit 180 -");
        psi.StandardOutputEncoding = null;
        using var p = Process.Start(psi)!;
        _proc = p;
        ChildJob.Add(p);
        var errTask = p.StandardError.ReadToEndAsync();
        var stream = p.StandardOutput.BaseStream;
        var pipe = (stream as FileStream)?.SafeFileHandle;

        var buf = new byte[1 << 20];
        int len = 0;
        long total = 0;
        H264Decoder.FrameHandler onFrame = OnFrame;
        bool early = false;

        while (!_stop)
        {
            if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
            int n = stream.Read(buf, len, buf.Length - len);
            if (n <= 0) break;
            total += n;
            BytesIn += n;
            int scanFrom = Math.Max(0, len - 3);
            len += n;

            fixed (byte* b = buf)
            {
                // After an early decode the next bytes must open a new NAL. If they don't, that NAL was cut
                // mid-way (network burst gap): wait longer from now on and restart for a clean keyframe,
                // otherwise the picture stays smeared with blocks until the next one.
                if (early && len >= 4)
                {
                    early = false;
                    if (FindStart(b, 0, Math.Min(len, 4)) != 0)
                    {
                        _idleMs = Math.Min(_idleMs * 2, 400);
                        try { p.Kill(); } catch { }
                        break;
                    }
                }
                int nalStart = FindStart(b, 0, len);
                if (nalStart < 0) { if (len > 64 && total < 4096) error = System.Text.Encoding.UTF8.GetString(buf, 0, len); continue; }
                int consumed = nalStart;
                int i = Math.Max(nalStart + 3, scanFrom);
                while (true)
                {
                    int next = FindStart(b, i, len);
                    if (next < 0) break;
                    long t0 = Stopwatch.GetTimestamp();
                    dec.Decode(b + consumed, next - consumed, onFrame); // one complete NAL unit
                    DecodeTicks += Stopwatch.GetTimestamp() - t0;
                    NalsIn++;
                    consumed = next;
                    i = next + 3;
                }
                // The encoder writes whole frames at once. If the pipe is now idle, the trailing NAL is
                // complete: decode it immediately instead of waiting for the next frame (which, on a
                // static screen, may never come → stale image).
                if (len - consumed > 4 && PipeIdle(pipe, _idleMs))
                {
                    dec.Decode(b + consumed, len - consumed, onFrame);
                    NalsIn++;
                    consumed = len;
                    early = true;
                }
                if (consumed > 0)
                {
                    Buffer.MemoryCopy(b + consumed, b, buf.Length, len - consumed);
                    len -= consumed;
                }
            }
        }
        try { p.WaitForExit(500); } catch { }
        if (string.IsNullOrEmpty(error)) error = errTask.IsCompleted ? errTask.Result.Trim() : "";
        if (string.IsNullOrEmpty(error)) error = "screenrecord stopped";
        return total;
    }

    static bool PipeIdle(SafeFileHandle? h, int ms)
    {
        if (h == null) return false;
        // Must be idle for a while: decoding a half-received NAL corrupts the picture until the next keyframe.
        // During motion the next frame arrives first and ends the wait, so this adds no delay.
        for (int k = 0; k < ms; k++)
        {
            if (!PeekNamedPipe(h, IntPtr.Zero, 0, IntPtr.Zero, out uint avail, IntPtr.Zero) || avail > 0) return false;
            Thread.Sleep(1);
        }
        return PeekNamedPipe(h, IntPtr.Zero, 0, IntPtr.Zero, out uint last, IntPtr.Zero) && last == 0;
    }

    /// <summary>Returns index of the next 00 00 01 / 00 00 00 01 start code at or after <paramref name="from"/>.</summary>
    static int FindStart(byte* b, int from, int len)
    {
        for (int i = from; i + 2 < len; i++)
        {
            if (b[i + 2] > 1) { i += 2; continue; }
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
                return i > 0 && b[i - 1] == 0 ? i - 1 : i;
        }
        return -1;
    }

    void OnFrame(byte* nv12, int stride, int planeH, int w, int h)
    {
        FramesDecoded++;
        w = Math.Min(w, _width);
        h = Math.Min(h, _height);
        int size = w * h * 4;
        var dst = Interlocked.Exchange(ref _spare, null);
        if (dst == null || dst.Length != size) dst = new byte[size];
        long c0 = Stopwatch.GetTimestamp();
        fixed (byte* d = dst)
            Nv12ToBgra(nv12, stride, planeH, d, w, h);
        ConvTicks += Stopwatch.GetTimestamp() - c0;
        Volatile.Write(ref _frameW, w);
        Volatile.Write(ref _frameH, h);
        var old = Interlocked.Exchange(ref _latest, dst);
        if (old != null) Interlocked.CompareExchange(ref _spare, old, null); // UI hasn't taken it yet: recycle, a render is already queued
        else FrameReady?.Invoke();
    }

    static void Nv12ToBgra(byte* src, int stride, int planeH, byte* dst, int w, int h)
    {
        nint s = (nint)src, uvp = (nint)(src + stride * planeH), d = (nint)dst;
        // A few coarse chunks (not per-row tasks) → parallel without thread-pool spin overhead.
        int pairs = h / 2, chunks = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        Parallel.For(0, chunks, chunk =>
        {
        for (int pair = pairs * chunk / chunks, end = pairs * (chunk + 1) / chunks; pair < end; pair++)
        {
            byte* uv = (byte*)uvp + pair * stride;
            for (int r = 0; r < 2; r++)
            {
                int y = pair * 2 + r;
                byte* yr = (byte*)s + y * stride;
                uint* o = (uint*)((byte*)d + y * w * 4);
                for (int x = 0; x < w; x++)
                {
                    int c = 298 * (yr[x] - 16);
                    int du = uv[x & ~1] - 128, dv = uv[(x & ~1) + 1] - 128;
                    int R = (c + 409 * dv + 128) >> 8;
                    int G = (c - 100 * du - 208 * dv + 128) >> 8;
                    int B = (c + 516 * du + 128) >> 8;
                    R = R < 0 ? 0 : R > 255 ? 255 : R;
                    G = G < 0 ? 0 : G > 255 ? 255 : G;
                    B = B < 0 ? 0 : B > 255 ? 255 : B;
                    o[x] = 0xFF000000u | (uint)(R << 16) | (uint)(G << 8) | (uint)B;
                }
            }
        }
        });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PeekNamedPipe(SafeFileHandle h, IntPtr buf, uint size, IntPtr read, out uint avail, IntPtr left);
}
