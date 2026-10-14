using System.Runtime.InteropServices;

namespace AdbView;

/// <summary>
/// Minimal H.264 decoder on top of the Windows built-in Media Foundation decoder MFT.
/// COM methods are called through raw vtable slots (no interop wrappers, no allocations per call).
/// Output: NV12 frames.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    public delegate void FrameHandler(byte* nv12, int stride, int planeHeight, int width, int height);

    IntPtr _mft, _spare, _spareBuf;
    int _outSize;
    bool _providesSamples;
    int _width, _height, _stride;

    static readonly Guid CLSID_H264Decoder = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");
    static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    static readonly Guid MT_MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    const int NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    const int STREAM_CHANGE = unchecked((int)0xC00D6D61);
    const int NOT_ACCEPTING = unchecked((int)0xC00D36B5);

    public H264Decoder(int width, int height)
    {
        CoInitializeEx(IntPtr.Zero, 0);
        Check(MFStartup(0x20070, 0));
        Guid clsid = CLSID_H264Decoder, iid = IID_IMFTransform;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out _mft));

        IntPtr attrs;
        if (GetAttributes(&attrs) >= 0 && attrs != IntPtr.Zero)
        {
            SetU32(attrs, LOW_LATENCY, 1);
            Release(attrs);
        }

        Check(MFCreateMediaType(out var type));
        SetGuid(type, MT_MAJOR, Video);
        SetGuid(type, MT_SUBTYPE, H264);
        SetU64(type, MT_FRAME_SIZE, ((ulong)(uint)width << 32) | (uint)height);
        int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Vt(_mft)[15])(_mft, 0, type, 0); // SetInputType
        Release(type);
        Check(hr);

        SelectOutputType();
        Message(0x10000000); // NOTIFY_BEGIN_STREAMING
        Message(0x10000003); // NOTIFY_START_OF_STREAM
    }

    int GetAttributes(IntPtr* attrs) => ((delegate* unmanaged<IntPtr, IntPtr*, int>)Vt(_mft)[8])(_mft, attrs);

    void Message(int msg) => ((delegate* unmanaged<IntPtr, int, nuint, int>)Vt(_mft)[23])(_mft, msg, 0);

    void SelectOutputType()
    {
        for (uint i = 0; ; i++)
        {
            IntPtr t;
            Check(((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Vt(_mft)[14])(_mft, 0, i, &t)); // GetOutputAvailableType
            Guid sub;
            GetGuid(t, MT_SUBTYPE, &sub);
            if (sub == NV12)
            {
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Vt(_mft)[16])(_mft, 0, t, 0); // SetOutputType
                ulong size = 0;
                GetU64(t, MT_FRAME_SIZE, &size);
                _width = (int)(size >> 32);
                _height = (int)(size & 0xFFFFFFFF);
                int stride = 0;
                _stride = GetU32(t, MT_DEFAULT_STRIDE, &stride) >= 0 && stride > 0 ? stride : _width;
                Release(t);
                Check(hr);
                break;
            }
            Release(t);
        }

        // MFT_OUTPUT_STREAM_INFO { flags, cbSize, cbAlignment }
        uint* info = stackalloc uint[3];
        Check(((delegate* unmanaged<IntPtr, uint, uint*, int>)Vt(_mft)[7])(_mft, 0, info));
        _providesSamples = (info[0] & 0x300) != 0; // PROVIDES_SAMPLES | CAN_PROVIDE_SAMPLES
        _outSize = (int)Math.Max(info[1], (uint)(_stride * _height * 3 / 2));
    }

    /// <summary>Feeds one Annex-B NAL unit (with start code) and emits any decoded frames.</summary>
    public void Decode(byte* data, int len, FrameHandler onFrame)
    {
        Check(MFCreateMemoryBuffer(len, out var buf));
        byte* p;
        ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Vt(buf)[3])(buf, &p, null, null); // Lock
        Buffer.MemoryCopy(data, p, len, len);
        ((delegate* unmanaged<IntPtr, int>)Vt(buf)[4])(buf);                                    // Unlock
        ((delegate* unmanaged<IntPtr, uint, int>)Vt(buf)[6])(buf, (uint)len);                   // SetCurrentLength
        Check(MFCreateSample(out var sample));
        AddBuffer(sample, buf);
        Release(buf);

        int hr = ProcessInput(sample);
        if (hr < 0) Dbg($"ProcessInput hr={hr:X8}");
        if (hr == NOT_ACCEPTING)
        {
            Drain(onFrame);
            hr = ProcessInput(sample);
        }
        Release(sample);
        Drain(onFrame);
    }

    int ProcessInput(IntPtr sample) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Vt(_mft)[24])(_mft, 0, sample, 0);

    [StructLayout(LayoutKind.Sequential)]
    struct OutputDataBuffer { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }

    void Drain(FrameHandler onFrame)
    {
        while (true)
        {
            // The MS decoder fails (E_FAIL) when the same output sample is reused, so give it a fresh one.
            // Reuse one output sample; its buffer length must be reset to 0 or the decoder returns E_FAIL.
            IntPtr own = IntPtr.Zero;
            if (!_providesSamples)
            {
                if (_spare == IntPtr.Zero)
                {
                    Check(MFCreateSample(out _spare));
                    Check(MFCreateMemoryBuffer(_outSize, out _spareBuf));
                    AddBuffer(_spare, _spareBuf);
                }
                ((delegate* unmanaged<IntPtr, uint, int>)Vt(_spareBuf)[6])(_spareBuf, 0); // SetCurrentLength(0)
                own = _spare;
            }
            var ob = new OutputDataBuffer { Sample = own };
            uint status;
            int hr = ((delegate* unmanaged<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)Vt(_mft)[25])(_mft, 0, 1, &ob, &status);
            if (ob.Events != IntPtr.Zero) Release(ob.Events);
            if (hr != NEED_MORE_INPUT) Dbg($"ProcessOutput hr={hr:X8} status={ob.Status:X} sample={ob.Sample != IntPtr.Zero}");
            if (hr == STREAM_CHANGE) { FreeSpare(); SelectOutputType(); continue; }
            if (hr < 0 || ob.Sample == IntPtr.Zero) break;

            IntPtr cbuf;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Vt(ob.Sample)[41])(ob.Sample, &cbuf) >= 0) // ConvertToContiguousBuffer
            {
                byte* p; uint max, cur;
                if (((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Vt(cbuf)[3])(cbuf, &p, &max, &cur) >= 0)
                {
                    // NV12: Y plane is stride × (aligned) height, interleaved UV plane follows right after.
                    if (cur >= (uint)(_stride * _height * 3 / 2)) onFrame(p, _stride, _height, _width, _height);
                    ((delegate* unmanaged<IntPtr, int>)Vt(cbuf)[4])(cbuf);
                }
                Release(cbuf);
            }
            if (_providesSamples) Release(ob.Sample);
        }
    }

    static void Dbg(string m)
    {
        if (Environment.GetEnvironmentVariable("PV_DEBUG") == "1")
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pv_dec.log"), m + Environment.NewLine);
    }

    void FreeSpare()
    {
        if (_spareBuf != IntPtr.Zero) { Release(_spareBuf); _spareBuf = IntPtr.Zero; }
        if (_spare != IntPtr.Zero) { Release(_spare); _spare = IntPtr.Zero; }
    }

    public void Dispose()
    {
        FreeSpare();
        if (_mft != IntPtr.Zero) { Release(_mft); _mft = IntPtr.Zero; }
    }

    // ---- raw COM helpers ----
    static void** Vt(IntPtr p) => *(void***)p;
    static void Release(IntPtr p) => ((delegate* unmanaged<IntPtr, uint>)Vt(p)[2])(p);
    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    // IMFAttributes slots: GetUINT32=7, GetUINT64=8, GetGUID=10, SetUINT32=21, SetUINT64=22, SetGUID=24
    static int GetU32(IntPtr a, Guid k, int* v) => ((delegate* unmanaged<IntPtr, Guid*, int*, int>)Vt(a)[7])(a, &k, v);
    static int GetU64(IntPtr a, Guid k, ulong* v) => ((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)Vt(a)[8])(a, &k, v);
    static int GetGuid(IntPtr a, Guid k, Guid* v) => ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Vt(a)[10])(a, &k, v);
    static void SetU32(IntPtr a, Guid k, uint v) => ((delegate* unmanaged<IntPtr, Guid*, uint, int>)Vt(a)[21])(a, &k, v);
    static void SetU64(IntPtr a, Guid k, ulong v) => ((delegate* unmanaged<IntPtr, Guid*, ulong, int>)Vt(a)[22])(a, &k, v);
    static void SetGuid(IntPtr a, Guid k, Guid v) => ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Vt(a)[24])(a, &k, &v);
    // IMFSample.AddBuffer = slot 42
    static void AddBuffer(IntPtr s, IntPtr b) => Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Vt(s)[42])(s, b));

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr r, int coinit);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int ctx, ref Guid iid, out IntPtr ppv);
    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int max, out IntPtr buffer);
}
