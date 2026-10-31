using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MyASR.Audio;

/// <summary>
/// NativeAOT-compatible WASAPI microphone enumerator and event-driven capture using direct COM
/// vtable calls instead of classic [ComImport] RCWs.
/// </summary>
public sealed unsafe class WasapiMicCapture : IMicCapture
{
    private const int RingCapacity = 16000 * 31; // 31 sec headroom

    private const uint ClsctxAll = 23;
    private const uint CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private const int EDataFlowCapture = 1;
    private const uint DeviceStateActive = 0x00000001;
    private const uint StgmRead = 0;
    private const ushort VtLpwstr = 31;

    private const int AudclntSharemodeShared = 0;
    private const uint AudclntStreamflagsEventcallback = 0x00040000;
    private const uint AudclntBufferflagsSilent = 0x00000002;
    private const ushort WaveFormatExtensibleTag = 0xFFFE;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidImmDeviceEnumerator  = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidIAudioClient         = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioCaptureClient  = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid SubtypeIeeeFloat        = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypePcm              = new("00000001-0000-0010-8000-00AA00389B71");

    private static readonly PropertyKey PkeyDeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    private readonly AudioRingBuffer ring = new(RingCapacity);
    private Thread? captureThread;
    private nint stopEvent;
    private volatile float rmsDb = -120f;

    public double CurrentRmsDb => rmsDb;

    public IReadOnlyList<DeviceInfo> EnumerateDevices()
    {
        var uninit = InitComForCurrentThread();
        void* enumerator = null;
        void* collection = null;
        try
        {
            var clsid = ClsidMmDeviceEnumerator;
            var iid = IidImmDeviceEnumerator;
            var hr = CoCreateInstance(&clsid, null, ClsctxAll, &iid, &enumerator);
            if (hr < 0 || enumerator is null) return [];

            // IMMDeviceEnumerator::EnumAudioEndpoints (slot 3)
            var vtbl = *(void***)enumerator;
            hr = ((delegate* unmanaged[Stdcall]<void*, int, uint, void**, int>)vtbl[3])(
                enumerator, EDataFlowCapture, DeviceStateActive, &collection);
            if (hr < 0 || collection is null) return [];

            // IMMDeviceCollection::GetCount (slot 3)
            var colVtbl = *(void***)collection;
            uint count = 0;
            hr = ((delegate* unmanaged[Stdcall]<void*, uint*, int>)colVtbl[3])(collection, &count);
            if (hr < 0 || count == 0) return [];

            var result = new List<DeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                void* device = null;
                try
                {
                    // IMMDeviceCollection::Item (slot 4)
                    if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)colVtbl[4])(collection, i, &device) < 0
                        || device is null)
                    {
                        continue;
                    }

                    var id = ReadDeviceId(device);
                    if (string.IsNullOrEmpty(id)) continue;
                    var friendly = ReadFriendlyName(device) ?? id;
                    result.Add(new DeviceInfo(id, friendly));
                }
                finally
                {
                    ReleaseCom(device);
                }
            }

            return result;
        }
        finally
        {
            ReleaseCom(collection);
            ReleaseCom(enumerator);
            if (uninit) CoUninitialize();
        }
    }

    public void Start(string deviceId)
    {
        Stop();

        stopEvent = CreateEventW(0, bManualReset: 1, bInitialState: 0, null);
        if (stopEvent == 0)
            throw new InvalidOperationException("Failed to create WASAPI stop event.");

        using var ready = new ManualResetEventSlim(false);
        Exception? initError = null;

        ring.Reset();
        rmsDb = -120f;

        captureThread = new Thread(() => CaptureLoop(deviceId, stopEvent, ready, ex => initError = ex))
        {
            IsBackground = true,
            Name = "MyASR-WASAPI-Capture"
        };
        captureThread.Start();
        ready.Wait();

        if (initError is not null)
        {
            Stop();
            throw new InvalidOperationException($"Не удалось запустить захват микрофона: {initError.Message}", initError);
        }
    }

    public float[] StopAndDrain()
    {
        Stop();
        return ring.Snapshot();
    }

    public static float[] DecodeWaveBuffer(WaveFormat fmt, ReadOnlySpan<byte> bytes, bool silent)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            var count = bytes.Length / 4;
            var floats = new float[count];
            if (silent) return floats;
            for (var i = 0; i < count; i++)
                floats[i] = BitConverter.ToSingle(bytes.Slice(i * 4, 4));
            return floats;
        }

        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            var count = bytes.Length / 2;
            var floats = new float[count];
            if (silent) return floats;
            for (var i = 0; i < count; i++)
            {
                var sample = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                floats[i] = sample / 32768f;
            }
            return floats;
        }

        throw new NotSupportedException($"Unsupported capture format: {fmt}");
    }

    private void CaptureLoop(
        string deviceId,
        nint stopHandle,
        ManualResetEventSlim ready,
        Action<Exception> reportError)
    {
        var uninit = InitComForCurrentThread();
        void* enumerator = null;
        void* device = null;
        void* audioClient = null;
        void* captureClient = null;
        WaveFormatEx* mixFormatPtr = null;
        nint sampleEvent = 0;

        try
        {
            var clsid = ClsidMmDeviceEnumerator;
            var iidEnum = IidImmDeviceEnumerator;
            CheckHr(CoCreateInstance(&clsid, null, ClsctxAll, &iidEnum, &enumerator), "CoCreateInstance(MMDeviceEnumerator)");

            fixed (char* pId = deviceId)
            {
                // IMMDeviceEnumerator::GetDevice (slot 5)
                var enumVtbl = *(void***)enumerator;
                CheckHr(((delegate* unmanaged[Stdcall]<void*, char*, void**, int>)enumVtbl[5])(
                    enumerator, pId, &device), "IMMDeviceEnumerator::GetDevice");
            }

            // IMMDevice::Activate (slot 3)
            var devVtbl = *(void***)device;
            var iidClient = IidIAudioClient;
            CheckHr(((delegate* unmanaged[Stdcall]<void*, Guid*, uint, void*, void**, int>)devVtbl[3])(
                device, &iidClient, ClsctxAll, null, &audioClient), "IMMDevice::Activate(IAudioClient)");

            // IAudioClient::GetMixFormat (slot 8)
            var clientVtbl = *(void***)audioClient;
            CheckHr(((delegate* unmanaged[Stdcall]<void*, WaveFormatEx**, int>)clientVtbl[8])(
                audioClient, &mixFormatPtr), "IAudioClient::GetMixFormat");

            var waveFormat = ParseWaveFormat(mixFormatPtr);
            var blockAlign = mixFormatPtr->BlockAlign;

            sampleEvent = CreateEventW(0, bManualReset: 0, bInitialState: 0, null);
            if (sampleEvent == 0)
                throw new InvalidOperationException("CreateEventW failed.");

            // IAudioClient::Initialize (slot 3) — 100 ms buffer in 100-ns units
            CheckHr(((delegate* unmanaged[Stdcall]<void*, int, uint, long, long, WaveFormatEx*, Guid*, int>)clientVtbl[3])(
                audioClient,
                AudclntSharemodeShared,
                AudclntStreamflagsEventcallback,
                1_000_000L,
                0L,
                mixFormatPtr,
                null), "IAudioClient::Initialize");

            // IAudioClient::SetEventHandle (slot 13)
            CheckHr(((delegate* unmanaged[Stdcall]<void*, nint, int>)clientVtbl[13])(
                audioClient, sampleEvent), "IAudioClient::SetEventHandle");

            // IAudioClient::GetService (slot 14)
            var iidCapture = IidIAudioCaptureClient;
            CheckHr(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)clientVtbl[14])(
                audioClient, &iidCapture, &captureClient), "IAudioClient::GetService(IAudioCaptureClient)");

            // IAudioClient::Start (slot 10)
            CheckHr(((delegate* unmanaged[Stdcall]<void*, int>)clientVtbl[10])(audioClient), "IAudioClient::Start");

            ready.Set();

            var handles = stackalloc nint[2] { stopHandle, sampleEvent };
            var capVtbl = *(void***)captureClient;

            while (true)
            {
                var wait = WaitForMultipleObjects(2, handles, bWaitAll: 0, dwMilliseconds: 200);
                if (wait == 0) break; // stopHandle signaled

                while (true)
                {
                    uint packetFrames = 0;
                    // IAudioCaptureClient::GetNextPacketSize (slot 5)
                    var hr = ((delegate* unmanaged[Stdcall]<void*, uint*, int>)capVtbl[5])(captureClient, &packetFrames);
                    if (hr < 0 || packetFrames == 0) break;

                    byte* dataPtr = null;
                    uint numFrames = 0;
                    uint flags = 0;
                    ulong devPos = 0;
                    ulong qpcPos = 0;

                    // IAudioCaptureClient::GetBuffer (slot 3)
                    hr = ((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, ulong*, ulong*, int>)capVtbl[3])(
                        captureClient, &dataPtr, &numFrames, &flags, &devPos, &qpcPos);
                    if (hr < 0) break;

                    try
                    {
                        if (numFrames > 0)
                        {
                            var byteLen = checked((int)numFrames * blockAlign);
                            var silent = (flags & AudclNTBufferSilentMask) != 0 || dataPtr is null;
                            var rawSpan = dataPtr is not null ? new ReadOnlySpan<byte>(dataPtr, byteLen) : new byte[byteLen];
                            var samples = DecodeWaveBuffer(waveFormat, rawSpan, silent);
                            var mono16 = Resampler.ToMono16k(waveFormat, samples);
                            ring.Write(mono16);
                            rmsDb = (float)ComputeRmsDb(mono16);
                        }
                    }
                    finally
                    {
                        // IAudioCaptureClient::ReleaseBuffer (slot 4)
                        ((delegate* unmanaged[Stdcall]<void*, uint, int>)capVtbl[4])(captureClient, numFrames);
                    }
                }
            }

            // IAudioClient::Stop (slot 11)
            ((delegate* unmanaged[Stdcall]<void*, int>)clientVtbl[11])(audioClient);
        }
        catch (Exception ex)
        {
            reportError(ex);
            ready.Set();
        }
        finally
        {
            if (sampleEvent != 0) CloseHandle(sampleEvent);
            if (mixFormatPtr is not null) Marshal.FreeCoTaskMem((nint)mixFormatPtr);
            ReleaseCom(captureClient);
            ReleaseCom(audioClient);
            ReleaseCom(device);
            ReleaseCom(enumerator);
            if (uninit) CoUninitialize();
        }
    }

    private const uint AudclNTBufferSilentMask = AudclntBufferflagsSilent;

    private static WaveFormat ParseWaveFormat(WaveFormatEx* wfx)
    {
        if (wfx->FormatTag == 3 && wfx->BitsPerSample == 32)
            return WaveFormat.CreateIeeeFloatWaveFormat((int)wfx->SamplesPerSec, wfx->Channels);

        if (wfx->FormatTag == 1)
            return new WaveFormat((int)wfx->SamplesPerSec, wfx->BitsPerSample, wfx->Channels);

        if (wfx->FormatTag == WaveFormatExtensibleTag && wfx->CbSize >= 22)
        {
            var ext = (WaveFormatExtensible*)wfx;
            if (ext->SubFormat == SubtypeIeeeFloat && wfx->BitsPerSample == 32)
                return WaveFormat.CreateIeeeFloatWaveFormat((int)wfx->SamplesPerSec, wfx->Channels);
            if (ext->SubFormat == SubtypePcm)
                return new WaveFormat((int)wfx->SamplesPerSec, wfx->BitsPerSample, wfx->Channels);
        }

        throw new NotSupportedException($"Unsupported WASAPI mix format tag 0x{wfx->FormatTag:X4} ({wfx->BitsPerSample}-bit).");
    }

    private static string? ReadDeviceId(void* device)
    {
        // IMMDevice::GetId (slot 5)
        var vtbl = *(void***)device;
        char* strPtr = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, char**, int>)vtbl[5])(device, &strPtr);
        if (hr < 0 || strPtr is null) return null;
        try
        {
            return Marshal.PtrToStringUni((nint)strPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)strPtr);
        }
    }

    private static string? ReadFriendlyName(void* device)
    {
        // IMMDevice::OpenPropertyStore (slot 4)
        var devVtbl = *(void***)device;
        void* store = null;
        if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)devVtbl[4])(device, StgmRead, &store) < 0
            || store is null)
        {
            return null;
        }

        PropVariant pv = default;
        try
        {
            // IPropertyStore::GetValue (slot 5)
            var storeVtbl = *(void***)store;
            var key = PkeyDeviceFriendlyName;
            if (((delegate* unmanaged[Stdcall]<void*, PropertyKey*, PropVariant*, int>)storeVtbl[5])(store, &key, &pv) < 0)
                return null;

            return pv.Vt == VtLpwstr && pv.PointerValue != 0
                ? Marshal.PtrToStringUni(pv.PointerValue)
                : null;
        }
        finally
        {
            PropVariantClear(&pv);
            ReleaseCom(store);
        }
    }

    private static double ComputeRmsDb(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return -120.0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        return 20.0 * Math.Log10(Math.Max(rms, 1e-10));
    }

    private static bool InitComForCurrentThread()
    {
        var hr = CoInitializeEx(0, CoinitMultithreaded);
        if (hr == RpcEChangedMode) return false; // STA already active on UI thread
        return hr >= 0;
    }

    private static void CheckHr(int hr, string call)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static void ReleaseCom(void* ptr)
    {
        if (ptr is null) return;
        var vtbl = *(void***)ptr;
        ((delegate* unmanaged[Stdcall]<void*, uint>)vtbl[2])(ptr);
    }

    private void Stop()
    {
        if (stopEvent != 0)
        {
            SetEvent(stopEvent);
            captureThread?.Join(TimeSpan.FromSeconds(2));
            captureThread = null;
            CloseHandle(stopEvent);
            stopEvent = 0;
        }
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort SamplesUnion;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint PointerValue;
        public nint Padding;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* rclsid, void* pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(PropVariant* pvar);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, char* lpName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int SetEvent(nint hEvent);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, nint* lpHandles, int bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(nint hObject);
}
