using System;
using System.Globalization;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DirectShowLib;
using Framejet.Core;
using Framejet.Core.Devices;
using Framejet.Core.Video;

namespace Framejet.Platform.Video
{
    /// <summary>
    /// Runs a DirectShow capture graph (capture filter → Sample Grabber → Null Renderer, with no reference clock) and
    /// draws each frame on the host's video window with a <see cref="D3D11Presenter"/> the moment it arrives.
    /// Create it and use every member on the UI (STA) thread, which must pump messages: graph events arrive there
    /// and become <see cref="DeviceLost"/>.
    /// </summary>
    public sealed class DirectShowVideoBackend : IVideoBackend, IDisposable
    {
        const int NoInterface = unchecked((int)0x80004002); // E_NOINTERFACE

        readonly IVideoHost _host;
        readonly ILog _log;
        bool _vsync = true, _paused, _measureLatency;

        // The running graph and everything attached to it; all null while stopped.
        IFilterGraph2 _graph;
        ICaptureGraphBuilder2 _builder;
        IBaseFilter _source, _nullRenderer;
        object _streamConfig;
        ISampleGrabber _grabber;
        GrabberCallback _callback; // the grabber holds it only through COM, so keep it reachable here
        GraphEventWindow _eventWindow;
        D3D11Presenter _presenter;
        bool _videoShown;

        public DirectShowVideoBackend(IVideoHost host, ILog log)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Raised on the UI thread, at most once per start, when the graph reports the device gone or its stream failed.</summary>
        public event Action DeviceLost;
        public event Action DeviceTaken;

        /// <summary>
        /// Raised on the render thread every 15th drawn frame. Handlers must return at once and never block:
        /// post to the UI thread with BeginInvoke, never Invoke.
        /// </summary>
        public event Action<LumaStats> LumaSampled;

        /// <summary>On (the default): each frame waits for the next refresh. Off: frames show at once and may tear.</summary>
        public bool VSync
        {
            get => _vsync;
            set { _vsync = value; if (_presenter != null) _presenter.VSync = value; }
        }

        /// <summary>While paused, frames keep arriving but are counted as dropped instead of drawn.</summary>
        public bool Paused
        {
            get => _paused;
            set { _paused = value; if (_presenter != null) _presenter.Paused = value; }
        }

        /// <summary>Measures Present → screen times, the presentation mode and the refresh rate.</summary>
        public bool MeasureLatency
        {
            get => _measureLatency;
            set { _measureLatency = value; if (_presenter != null) _presenter.MeasureLatency = value; }
        }

        /// <summary>The frames the running graph delivers; null when stopped.</summary>
        public CaptureFormat Format { get; private set; }

        /// <summary>Counters and timings since the start or the last reset; all zero when stopped. It copies and sorts the timing samples: for reports, not for polling.</summary>
        public PresenterStats GetStats() => _presenter?.GetStats() ?? new PresenterStats();

        /// <summary>The draw rate over the last second; 0 when stopped. Cheap and allocation-free, for the status text.</summary>
        public double MeasuredFps => _presenter?.MeasuredFps ?? 0;

        /// <summary>Frames delivered since the last successful Start; 0 when stopped. A lock-free read.</summary>
        public long FramesArrived => _presenter?.FramesArrived ?? 0;

        /// <summary>The Stopwatch timestamp of the first frame presented since the last Start; 0 until then, and when stopped.</summary>
        public long FirstPresentTimestamp => _presenter?.FirstPresentTimestamp ?? 0;

        public void ResetStats() => _presenter?.ResetStats();

        /// <summary>Never throws: a failure is logged and gives an empty list.</summary>
        public IReadOnlyList<VideoDeviceInfo> ListDevices()
        {
            try
            {
                return DirectShowDevices.List();
            }
            catch (Exception ex)
            {
                _log.Error("Listing video devices failed", ex);
                return Array.Empty<VideoDeviceInfo>();
            }
        }

        /// <summary>The device's raw modes. Throws only <see cref="VideoStartException"/>.</summary>
        public IReadOnlyList<VideoMode> ListModes(VideoDeviceInfo device)
        {
            if (device == null) throw new VideoStartException("Listing modes needs a video device");
            try
            {
                return DirectShowDevices.ListRawModes(device);
            }
            catch (VideoStartException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Failure(device, "list modes failed", ex.HResult, ex);
            }
        }

        /// <summary>
        /// Builds and runs the graph for the mode and starts drawing, stopping anything already running first.
        /// Throws only <see cref="VideoStartException"/>. A failed start leaves nothing behind (no graph, presenter
        /// or swap chain), so it can simply be tried again.
        /// </summary>
        public void Start(VideoDeviceInfo device, VideoMode mode)
        {
            if (device == null || mode == null) throw new VideoStartException("Starting video needs a device and a mode");
            Stop();
            string step = "find the device";
            DsDevice[] devices = null;
            // Where the start spends its time, logged once after a successful start (a diagnostic only).
            var timer = Stopwatch.StartNew();
            double devicesMs = 0, graphMs = 0, presenterMs = 0;
            try
            {
                devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                var match = Array.Find(devices, d => string.Equals(d.DevicePath, device.Path, StringComparison.OrdinalIgnoreCase));
                if (match == null) throw Failure(device, "device not found", DsResults.E_NotFound);
                devicesMs = timer.Elapsed.TotalMilliseconds;

                // 1. The capture filter.
                step = "open the device";
                _graph = (IFilterGraph2)new FilterGraph();
                _builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
                Check(_builder.SetFiltergraph(_graph), device, step);
                Check(_graph.AddSourceFilterForMoniker(match.Mon, null, match.Name, out _source), device, step);

                // 2. The mode, set on the capture pin before anything is connected.
                step = "set the mode";
                Check(_builder.FindInterface(PinCategory.Capture, MediaType.Video, _source, typeof(IAMStreamConfig).GUID, out _streamConfig), device, step);
                if (!(_streamConfig is IAMStreamConfig config)) throw Failure(device, "the capture pin has no stream configuration", NoInterface);
                SetMode(config, device, mode);

                // 3. The grabber asks for the raw format, or for RGB32 so DirectShow adds a decoder. Only the major type and
                //    subtype are set. Then the Null Renderer.
                step = "add the sample grabber";
                var grabberFormat = PixelFormats.GrabberFormatFor(mode.Format);
                _grabber = (ISampleGrabber)new SampleGrabber();
                var wanted = new AMMediaType { majorType = MediaType.Video, subType = SubtypeFor(grabberFormat) };
                try { Check(_grabber.SetMediaType(wanted), device, step); }
                finally { DsUtils.FreeAMMediaType(wanted); }
                Check(_graph.AddFilter((IBaseFilter)_grabber, "Sample Grabber"), device, step);
                step = "add the null renderer";
                _nullRenderer = (IBaseFilter)new NullRenderer();
                Check(_graph.AddFilter(_nullRenderer, "Null Renderer"), device, step);

                // 4. Connect. The builder inserts a Smart Tee, and for MJPG the MJPEG Decompressor.
                step = "connect the graph";
                Check(_builder.RenderStream(PinCategory.Capture, MediaType.Video, _source, (IBaseFilter)_grabber, _nullRenderer), device, step);

                // 5. What really arrives, and the slot it arrives in.
                step = "read the connected format";
                var format = ConnectedFormat(_grabber, device, grabberFormat);
                var slot = new LatestFrameSlot(format.FrameBytes);

                // 6. Every frame goes to BufferCB, and the grabber keeps nothing.
                step = "set up the sample grabber";
                Check(_grabber.SetBufferSamples(false), device, step);
                Check(_grabber.SetOneShot(false), device, step);
                _callback = new GrabberCallback(slot, _log);
                Check(_grabber.SetCallback(_callback, 1), device, step);

                // 7. No reference clock, so each frame is delivered the moment it arrives. Graph events go to the UI thread.
                step = "remove the reference clock";
                Check(((IMediaFilter)_graph).SetSyncSource(null), device, step);
                step = "watch graph events";
                _eventWindow = new GraphEventWindow((IMediaEventEx)_graph, _log);
                _eventWindow.DeviceLost += OnGraphDeviceLost;
                _eventWindow.DeviceTaken += OnGraphDeviceTaken;
                Check(((IMediaEventEx)_graph).SetNotifyWindow(_eventWindow.Handle, GraphEventWindow.NotifyMessage, IntPtr.Zero), device, step);

                // 8. The presenter, with the current settings.
                graphMs = timer.Elapsed.TotalMilliseconds - devicesMs;
                step = "create the presenter";
                _presenter = new D3D11Presenter(_host.VideoHandle, _log) { VSync = _vsync, Paused = _paused, MeasureLatency = _measureLatency };
                _presenter.LumaSampled += OnLumaSampled;
                _presenter.Start(format, slot);
                presenterMs = timer.Elapsed.TotalMilliseconds - devicesMs - graphMs;

                // 9. Show the video, then run.
                step = "start capturing";
                _host.ShowVideo(true);
                _videoShown = true;
                Check(((IMediaControl)_graph).Run(), device, step);
                double runMs = timer.Elapsed.TotalMilliseconds - devicesMs - graphMs - presenterMs;
                Format = format;
                _log.Info(string.Format(CultureInfo.InvariantCulture,
                    "Video start timings: devices {0:0} ms, graph {1:0} ms, presenter {2:0} ms (device and views {3:0}, swap chain {4:0}, shaders {5:0} ms), run {6:0} ms",
                    devicesMs, graphMs, presenterMs, _presenter.CreateDeviceMs - _presenter.CreateSwapChainMs - _presenter.CreateShadersMs, _presenter.CreateSwapChainMs, _presenter.CreateShadersMs, runMs));
                _log.Info($"Video running: {device.Name} {mode.Key}, frames {format.Width}x{format.Height} {format.Format}{(format.BottomUp ? " bottom-up" : "")}");
            }
            catch (VideoStartException)
            {
                Stop();
                throw;
            }
            catch (Exception ex)
            {
                Stop();
                throw Failure(device, step + " failed", ex.HResult, ex);
            }
            finally
            {
                if (devices != null) foreach (var d in devices) Quietly("release a device moniker", d.Dispose);
            }
        }

        /// <summary>
        /// Stops capture and drawing and releases the graph. Idempotent, and never throws: failures are logged.
        /// Every field is taken and cleared first, before anything that can block: closing the presenter joins the render
        /// thread, and a blocking wait on the UI thread can pump queued messages. A Start run from one of them finds a
        /// stopped backend and builds a graph of its own, which this Stop then leaves alone.
        /// </summary>
        public void Stop()
        {
            var graph = _graph; _graph = null;
            var builder = _builder; _builder = null;
            var source = _source; _source = null;
            var nullRenderer = _nullRenderer; _nullRenderer = null;
            var streamConfig = _streamConfig; _streamConfig = null;
            var grabber = _grabber; _grabber = null;
            var callback = _callback; _callback = null;
            var window = _eventWindow; _eventWindow = null;
            var presenter = _presenter; _presenter = null;
            bool videoShown = _videoShown; _videoShown = false;
            Format = null;

            if (graph != null)
            {
                Quietly("stop the capture graph", () => ((IMediaControl)graph).Stop());
                Quietly("stop graph event notifications", () => ((IMediaEventEx)graph).SetNotifyWindow(IntPtr.Zero, 0, IntPtr.Zero));
            }
            // Before anything below waits: no DeviceLost can arrive in the middle of the teardown.
            if (window != null)
            {
                window.DeviceLost -= OnGraphDeviceLost;
                window.DeviceTaken -= OnGraphDeviceTaken;
                Quietly("close the graph event window", window.Dispose);
            }
            if (grabber != null) Quietly("detach the frame callback", () => grabber.SetCallback(null, 0));
            GC.KeepAlive(callback); // the grabber held it only through COM, until the detach

            if (presenter != null)
            {
                presenter.LumaSampled -= OnLumaSampled;
                Quietly("close the presenter", presenter.Dispose); // Dispose alone: it stops the render thread itself
            }
            // A Start run during that wait shows its own video: hide only what this Stop took down.
            if (videoShown && !_videoShown) Quietly("hide the video", () => _host.ShowVideo(false));

            Release(ref streamConfig);
            Release(ref nullRenderer);
            Release(ref grabber);
            Release(ref source);
            Release(ref builder);
            Release(ref graph);
        }

        /// <summary>Stops video. The backend holds nothing else.</summary>
        public void Dispose() => Stop();

        /// <summary>
        /// Queues a graph event as a capture filter would, so tests can exercise device-lost handling.
        /// Only for events whose parameters are not pointers, such as EC_ERRORABORT.
        /// </summary>
        internal void SimulateGraphEvent(int eventCode)
        {
            if (_graph == null) throw new InvalidOperationException("Video is not running");
            int hr = ((IMediaEventSink)_graph).Notify((EventCode)eventCode, IntPtr.Zero, IntPtr.Zero);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        void OnGraphDeviceLost() => DeviceLost?.Invoke();

        void OnGraphDeviceTaken() => DeviceTaken?.Invoke();

        void OnLumaSampled(LumaStats stats) => LumaSampled?.Invoke(stats);

        /// <summary>Picks the capability with the mode's subtype and size and sets it at the mode's frame interval.</summary>
        static void SetMode(IAMStreamConfig config, VideoDeviceInfo device, VideoMode mode)
        {
            const string step = "set the mode";
            Check(config.GetNumberOfCapabilities(out int count, out int capsSize), device, step);
            IntPtr caps = Marshal.AllocCoTaskMem(Math.Max(capsSize, Marshal.SizeOf(typeof(VideoStreamConfigCaps))));
            try
            {
                int index = FindCapability(config, count, caps, capsSize, mode);
                if (index < 0) throw Failure(device, "no " + mode.Key + " capability", DsResults.E_NotFound);
                Check(config.GetStreamCaps(index, out AMMediaType chosen, caps), device, step);
                try
                {
                    SetFrameInterval(chosen, mode.FrameInterval);
                    Check(config.SetFormat(chosen), device, step);
                }
                finally
                {
                    DsUtils.FreeAMMediaType(chosen);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(caps);
            }
        }

        /// <summary>
        /// The first capability with the mode's subtype and size whose frame interval range includes the mode's, else
        /// the first with that subtype and size, or -1. The Ripsaw lists each one twice, as VIDEOINFOHEADER and VIDEOINFOHEADER2.
        /// </summary>
        static int FindCapability(IAMStreamConfig config, int count, IntPtr caps, int capsSize, VideoMode mode)
        {
            int sameSize = -1;
            for (int i = 0; i < count; i++)
            {
                if (config.GetStreamCaps(i, out AMMediaType mt, caps) != 0 || mt == null) continue;
                try
                {
                    var header = BitmapHeader(mt);
                    if (mt.subType != mode.Subtype || header == null || header.Width != mode.Width || Math.Abs(header.Height) != mode.Height) continue;
                    if (CoversInterval(caps, capsSize, mode.FrameInterval)) return i;
                    if (sameSize < 0) sameSize = i;
                }
                finally
                {
                    DsUtils.FreeAMMediaType(mt);
                }
            }
            return sameSize;
        }

        static bool CoversInterval(IntPtr caps, int capsSize, long interval)
        {
            if (capsSize < Marshal.SizeOf(typeof(VideoStreamConfigCaps))) return false;
            var range = (VideoStreamConfigCaps)Marshal.PtrToStructure(caps, typeof(VideoStreamConfigCaps));
            return range.MinFrameInterval <= interval && interval <= range.MaxFrameInterval;
        }

        static void SetFrameInterval(AMMediaType mt, long interval)
        {
            if (mt.formatType == FormatType.VideoInfo)
            {
                var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader));
                vih.AvgTimePerFrame = interval;
                Marshal.StructureToPtr(vih, mt.formatPtr, false);
            }
            else if (mt.formatType == FormatType.VideoInfo2)
            {
                var vih = (VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2));
                vih.AvgTimePerFrame = interval;
                Marshal.StructureToPtr(vih, mt.formatPtr, false);
            }
        }

        /// <summary>Size, layout and orientation of the frames the grabber receives.</summary>
        static CaptureFormat ConnectedFormat(ISampleGrabber grabber, VideoDeviceInfo device, GrabberFormat grabberFormat)
        {
            var connected = new AMMediaType();
            try
            {
                Check(grabber.GetConnectedMediaType(connected), device, "read the connected format");
                var header = BitmapHeader(connected);
                if (header == null || header.Width <= 0 || header.Height == 0)
                    throw Failure(device, "the connected format has no video header", DsResults.E_InvalidMediaType);
                // RGB rows run bottom-up when biHeight is positive; YUV is always top-down.
                bool bottomUp = grabberFormat == GrabberFormat.Rgb32 && header.Height > 0;
                return new CaptureFormat(header.Width, Math.Abs(header.Height), grabberFormat, bottomUp);
            }
            finally
            {
                DsUtils.FreeAMMediaType(connected);
            }
        }

        /// <summary>The bitmap header of a VIDEOINFOHEADER or VIDEOINFOHEADER2 format block; null for anything else.</summary>
        static BitmapInfoHeader BitmapHeader(AMMediaType mt)
        {
            if (mt.formatPtr == IntPtr.Zero) return null;
            if (mt.formatType == FormatType.VideoInfo && mt.formatSize >= Marshal.SizeOf(typeof(VideoInfoHeader)))
                return ((VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader))).BmiHeader;
            if (mt.formatType == FormatType.VideoInfo2 && mt.formatSize >= Marshal.SizeOf(typeof(VideoInfoHeader2)))
                return ((VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2))).BmiHeader;
            return null;
        }

        static Guid SubtypeFor(GrabberFormat format)
        {
            switch (format)
            {
                case GrabberFormat.Nv12: return MediaSubType.NV12;
                case GrabberFormat.Yuy2: return MediaSubType.YUY2;
                case GrabberFormat.Uyvy: return MediaSubType.UYVY;
                default: return MediaSubType.RGB32;
            }
        }

        static void Check(int hr, VideoDeviceInfo device, string step)
        {
            if (hr < 0) throw Failure(device, step + " failed", hr);
        }

        static VideoStartException Failure(VideoDeviceInfo device, string message, int hr, Exception inner = null)
            => new VideoStartException($"{device.Name}: {message} (0x{hr:X8})", inner);

        /// <summary>Runs one teardown step. A failed HRESULT or an exception is logged, never thrown.</summary>
        void Quietly(string what, Func<int> step)
        {
            try
            {
                int hr = step();
                if (hr < 0) _log.Error($"Couldn't {what} (0x{hr:X8})");
            }
            catch (Exception ex)
            {
                _log.Error("Couldn't " + what, ex);
            }
        }

        void Quietly(string what, Action step) => Quietly(what, () => { step(); return 0; });

        void Release<T>(ref T com) where T : class
        {
            var o = com;
            com = null;
            if (o != null) Quietly("release a DirectShow object", () => { if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); });
        }
    }
}
