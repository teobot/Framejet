using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using Framejet.Core;

namespace Framejet.Platform.Audio.Interop
{
    // Minimal Core Audio declarations for Windows low-latency shared mode (IAudioClient3, Windows 10+), which NAudio
    // 2.2.1 lacks. COM interfaces don't inherit vtables in .NET interop, so each one lists every method in vtable order.
    //
    // Objects come back as raw pointers and are wrapped in RCWs of their own (CoreAudio.Unique). The device enumerator
    // is a per-process singleton and devices are cached, so an ordinary RCW would be the very one NAudio holds: the
    // cast to these interfaces then fails, and releasing it would break NAudio's.

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);
        void OpenPropertyStore(int access, out IntPtr properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient3
    {
        // IAudioClient
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid iid, out IntPtr service);

        // IAudioClient2
        void IsOffloadCapable(int category, [MarshalAs(UnmanagedType.Bool)] out bool offloadCapable);
        void SetClientProperties(IntPtr properties);
        void GetBufferSizeLimits(IntPtr format, [MarshalAs(UnmanagedType.Bool)] bool eventDriven, out long minDuration, out long maxDuration);

        // IAudioClient3
        void GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodFrames, out uint fundamentalPeriodFrames, out uint minPeriodFrames, out uint maxPeriodFrames);
        void GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);
        void InitializeSharedAudioStream(int streamFlags, uint periodFrames, IntPtr format, IntPtr audioSessionGuid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out int flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, int flags);
    }

    internal static class CoreAudio
    {
        public const int ClsCtxAll = 0x17;
        public static readonly Guid ClsidMMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IidMMDeviceEnumerator = typeof(IMMDeviceEnumerator).GUID;
        public const int StreamFlagsEventCallback = 0x00040000;
        public const int BufferFlagsSilent = 0x2;
        public static readonly Guid IidAudioClient3 = typeof(IAudioClient3).GUID;
        public static readonly Guid IidAudioCaptureClient = typeof(IAudioCaptureClient).GUID;
        public static readonly Guid IidAudioRenderClient = typeof(IAudioRenderClient).GUID;

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid, out IntPtr instance);

        public static IMMDeviceEnumerator CreateEnumerator()
        {
            var clsid = ClsidMMDeviceEnumerator; var iid = IidMMDeviceEnumerator;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out IntPtr p));
            return Unique<IMMDeviceEnumerator>(p);
        }

        /// <summary>Wraps a COM pointer in an RCW nobody else shares, and drops the pointer's own reference.</summary>
        public static T Unique<T>(IntPtr p) where T : class
        {
            if (p == IntPtr.Zero) throw new COMException("Null COM pointer", unchecked((int)0x80004003));
            object o = null;
            try
            {
                o = Marshal.GetUniqueObjectForIUnknown(p);
                return (T)o;
            }
            catch
            {
                Release(o);
                throw;
            }
            finally { Marshal.Release(p); }
        }

        public static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
    }

    /// <summary>
    /// One endpoint's IAudioClient3, initialised for event-driven shared mode at the engine's minimum period.
    /// Owns the COM objects, the mix format and the event; the stream classes own the thread.
    /// </summary>
    internal sealed class LowLatencyClient : IDisposable
    {
        const int ENoInterface = unchecked((int)0x80004002);
        const int PeriodicityLocked = unchecked((int)0x88890028); // AUDCLNT_E_ENGINE_PERIODICITY_LOCKED
        const int JoinTimeoutMs = 2000;

        IMMDeviceEnumerator _enumerator;
        IMMDevice _device;
        IntPtr _format;

        LowLatencyClient() { }

        public IAudioClient3 Client { get; private set; }
        public WaveFormat Format { get; private set; }
        public AutoResetEvent Event { get; private set; }
        public uint PeriodFrames { get; private set; }
        public uint BufferFrames { get; private set; }
        public double PeriodMs => PeriodFrames * 1000.0 / Format.SampleRate;
        public double StreamLatencyMs { get; private set; }

        /// <summary>Returns null, with the reason, when the endpoint has no IAudioClient3, offers no shorter period, or won't initialise.</summary>
        public static LowLatencyClient Open(string endpointId, out string whyNot)
        {
            var c = new LowLatencyClient();
            try
            {
                c._enumerator = CoreAudio.CreateEnumerator();
                c._enumerator.GetDevice(endpointId, out IntPtr device);
                c._device = CoreAudio.Unique<IMMDevice>(device);
                var iid = CoreAudio.IidAudioClient3;
                c._device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out IntPtr client);
                c.Client = CoreAudio.Unique<IAudioClient3>(client);
                c.Client.GetMixFormat(out c._format);
                c.Format = WaveFormat.MarshalFromPtr(c._format);
                c.Client.GetSharedModeEnginePeriod(c._format, out uint def, out _, out uint min, out _);
                if (min == 0 || min >= def)
                {
                    whyNot = FormattableString.Invariant($"the driver offers no period below its default ({def * 1000.0 / c.Format.SampleRate:0.00} ms)");
                    c.Dispose();
                    return null;
                }
                c.Client.InitializeSharedAudioStream(CoreAudio.StreamFlagsEventCallback, min, c._format, IntPtr.Zero);
                c.Event = new AutoResetEvent(false);
                c.Client.SetEventHandle(c.Event.SafeWaitHandle.DangerousGetHandle());
                c.Client.GetCurrentSharedModeEnginePeriod(out IntPtr current, out uint period);
                if (current != IntPtr.Zero) Marshal.FreeCoTaskMem(current);
                c.PeriodFrames = period;
                c.Client.GetBufferSize(out uint buffer);
                c.BufferFrames = buffer;
                c.Client.GetStreamLatency(out long latency);
                c.StreamLatencyMs = latency / 10000.0;
                whyNot = null;
                return c;
            }
            catch (Exception ex)
            {
                whyNot = Describe(ex);
                c.Dispose();
                return null;
            }
        }

        public static string Describe(Exception ex)
        {
            switch (ex.HResult)
            {
                case ENoInterface: return "no IAudioClient3";
                case PeriodicityLocked: return "another app holds the engine at a different period";
            }
            return ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message.Trim();
        }

        public T Service<T>(Guid iid) where T : class
        {
            Client.GetService(ref iid, out IntPtr service);
            return CoreAudio.Unique<T>(service);
        }

        /// <summary>
        /// Starts a stream thread: registered with MMCSS as "Pro Audio" when it can be (logged once per thread if not),
        /// and AboveNormal priority either way.
        /// </summary>
        public static Thread StartThread(string name, Action body, ILog log)
        {
            var t = new Thread(() =>
            {
                int index = 0;
                IntPtr mmcss = IntPtr.Zero;
                try { mmcss = CoreAudio.AvSetMmThreadCharacteristics("Pro Audio", ref index); }
                catch (Exception) { }
                if (mmcss == IntPtr.Zero) log.Info(name + " thread runs without MMCSS Pro Audio (error " + Marshal.GetLastWin32Error() + ")");
                try { body(); }
                finally { if (mmcss != IntPtr.Zero) CoreAudio.AvRevertMmThreadCharacteristics(mmcss); }
            }) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
            t.Start();
            return t;
        }

        /// <summary>Waits for a stream thread to finish; false (and logged) if it didn't within 2 s.</summary>
        public static bool Join(Thread t, string name, ILog log)
        {
            if (t == null || t == Thread.CurrentThread) return true;
            if (t.Join(JoinTimeoutMs)) return true;
            log.Error(name + " thread did not stop within " + JoinTimeoutMs + " ms", null);
            return false;
        }

        /// <summary>Releases the COM objects; the event too unless a stream thread may still be waiting on it.</summary>
        public void Dispose(bool releaseEvent = true)
        {
            try { Client?.Stop(); } catch (Exception) { }
            CoreAudio.Release(Client); Client = null;
            CoreAudio.Release(_device); _device = null;
            CoreAudio.Release(_enumerator); _enumerator = null;
            if (_format != IntPtr.Zero) { Marshal.FreeCoTaskMem(_format); _format = IntPtr.Zero; }
            if (releaseEvent) Event?.Dispose();
        }

        void IDisposable.Dispose() => Dispose();
    }
}
