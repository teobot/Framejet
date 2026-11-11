using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Framejet.Core;
using Framejet.Core.Video;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3DDevice = SharpDX.Direct3D11.Device;
using DxgiDevice1 = SharpDX.DXGI.Device1;

namespace Framejet.Platform.Video
{
    /// <summary>
    /// Draws the newest captured frame on a window the moment it arrives, from its own render thread,
    /// through a flip-model swap chain. Construct, Start, Stop and Dispose on the UI thread; the
    /// settings, GetStats and ResetStats may be used from any thread.
    /// </summary>
    public sealed class D3D11Presenter : IDisposable
    {
        const int BufferCount = 2;
        const Format BufferFormat = Format.B8G8R8A8_UNorm;
        const SwapChainFlags AllowTearingFlag = (SwapChainFlags)2048; // DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING
        const PresentFlags AllowTearingPresent = (PresentFlags)0x200; // DXGI_PRESENT_ALLOW_TEARING
        const int DeviceRemoved = unchecked((int)0x887A0005);         // DXGI_ERROR_DEVICE_REMOVED
        const int DeviceHung = unchecked((int)0x887A0006);            // DXGI_ERROR_DEVICE_HUNG
        const int DeviceReset = unchecked((int)0x887A0007);           // DXGI_ERROR_DEVICE_RESET
        const int MaxSamples = 2000;
        const int LumaEvery = 15;
        const int RecentPresents = 64;
        const int StopTimeoutMs = 2000;
        static readonly Guid SwapChainMediaIid = new Guid("dd95b90b-f05f-4f6a-bd65-25bfb264bd84"); // IDXGISwapChainMedia
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetFrameStatisticsMediaFn(IntPtr self, out FrameStatisticsMedia stats);

        [StructLayout(LayoutKind.Sequential)]
        struct FrameStatisticsMedia
        {
            public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
            public long SyncQPCTime, SyncGPUTime;
            public int CompositionMode; // 0 composed, 1 overlay, 2 none, 3 composition failure
            public uint ApprovedPresentDuration;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);

        readonly IntPtr _hwnd;
        readonly ILog _log;
        bool _disposed;

        // Created by the constructor; while the render thread runs, only it touches these.
        D3DDevice _device;
        DeviceContext _context;
        SwapChain1 _swapChain;
        SwapChainFlags _swapChainFlags;
        RenderTargetView _backBufferView;
        int _bufferWidth, _bufferHeight;
        FrameRenderer _renderer;
        IntPtr _media;
        GetFrameStatisticsMediaFn _getFrameStatisticsMedia;

        // Render thread state.
        Thread _thread;
        volatile bool _stopRequested;
        // Set to 1 by the render thread as it exits, and by Dispose when it stops waiting for that thread.
        // The second of the two to set it releases the device objects, so exactly one side does.
        int _exitHandoff;
        CaptureFormat _format;
        LatestFrameSlot _slot;
        byte[] _frame;
        bool _frameInFlight;
        int _framesSinceLuma;
        long _nextRecreateAttempt;
        bool _drawFailureLogged, _lumaFailureLogged, _recreateFailureLogged;
        readonly uint[] _presentIds = new uint[RecentPresents];
        readonly long[] _presentTimes = new long[RecentPresents];
        readonly bool[] _presentTimed = new bool[RecentPresents];
        int _presentNext;
        uint _lastStatsPresentCount, _lastStatsSyncCount, _lastExactPresent;

        // Set from the UI thread, read by the render thread.
        volatile bool _vsync = true, _paused, _measureLatency, _tearingSupported;
        int _simulateDeviceLost;
        long _firstPresent; // written by the render thread once per start, read from any thread

        // Stats, guarded by _statsLock.
        readonly object _statsLock = new object();
        long _drawn, _skipped, _publishedAtReset, _replacedAtReset, _resetTime;
        int _overlay, _composed, _deviceResets;
        readonly SampleRing _arrivalToPresent = new SampleRing(MaxSamples);
        readonly SampleRing _presentToScreen = new SampleRing(MaxSamples);
        readonly long[] _drawTimes = new long[1024];
        int _drawTimesNext, _drawTimesCount;
        bool _hasVblankBase;
        long _vblankBaseCount, _vblankBaseQpc, _vblankLastCount, _vblankLastQpc;

        public D3D11Presenter(IntPtr hwnd, ILog log)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("A window handle is needed", nameof(hwnd));
            _hwnd = hwnd;
            _log = log ?? throw new ArgumentNullException(nameof(log));
            CreateDevice();
        }

        /// <summary>The swap chain allows tearing, so V-Sync Off never waits for a refresh.</summary>
        public bool TearingSupported => _tearingSupported;

        /// <summary>On (the default): Present(1) waits for the next refresh. Off: Present(0), tearing when supported.</summary>
        public bool VSync { get => _vsync; set => _vsync = value; }

        /// <summary>While paused, arriving frames are counted as dropped and not drawn.</summary>
        public bool Paused { get => _paused; set => _paused = value; }

        /// <summary>Polls DXGI frame statistics every millisecond for Present → screen, presentation mode and refresh rate.</summary>
        public bool MeasureLatency { get => _measureLatency; set => _measureLatency = value; }

        /// <summary>
        /// Raised on the render thread every 15th drawn frame. Handlers must return at once and never block:
        /// the render thread waits for them, so post to the UI thread with BeginInvoke, never Invoke.
        /// </summary>
        public event Action<LumaStats> LumaSampled;

        public void Start(CaptureFormat format, LatestFrameSlot slot)
        {
            if (format == null) throw new ArgumentNullException(nameof(format));
            if (slot == null) throw new ArgumentNullException(nameof(slot));
            if (slot.FrameBytes != format.FrameBytes)
                throw new ArgumentException($"The slot holds {slot.FrameBytes}-byte frames, but the format needs {format.FrameBytes}", nameof(slot));
            if (_disposed) throw new ObjectDisposedException(nameof(D3D11Presenter));
            if (_thread != null) throw new InvalidOperationException("The presenter is already running");
            if (_device == null) CreateDevice(); // the last recreation after a device loss had not succeeded
            _renderer.Configure(format);
            _format = format;
            _slot = slot;
            _frame = new byte[slot.FrameBytes];
            _frameInFlight = false;
            _framesSinceLuma = 0;
            _nextRecreateAttempt = 0;
            _drawFailureLogged = _lumaFailureLogged = _recreateFailureLogged = false; // a restart logs its own failures
            Interlocked.Exchange(ref _firstPresent, 0);
            ResetStats();
            _stopRequested = false;
            _exitHandoff = 0; // any earlier render thread has been joined
            _thread = new Thread(RenderLoop) { Name = "Framejet render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>
        /// Signals the render thread and waits up to 2 s for it to finish. If it is still running after that,
        /// Dispose leaves the release of the Direct3D objects to the thread for when it exits.
        /// </summary>
        public void Stop()
        {
            var thread = _thread;
            if (thread == null) return;
            _stopRequested = true;
            if (!thread.Join(StopTimeoutMs))
            {
                _log.Error("The render thread did not stop within 2 s");
                return; // keep _thread: Start and Dispose must not touch what it is still using
            }
            _thread = null;
        }

        public PresenterStats GetStats()
        {
            var slot = _slot;
            long published = slot?.Published ?? 0, replaced = slot?.Replaced ?? 0;
            var stats = new PresenterStats
            {
                SizeMismatches = slot?.SizeMismatches ?? 0,
                ExpectedFrameBytes = slot?.FrameBytes ?? 0,
                MismatchedFrameBytes = slot?.FirstMismatchBytes ?? 0,
            };
            double[] arrivalToPresent, presentToScreen;
            lock (_statsLock)
            {
                long vblanks = _vblankLastCount - _vblankBaseCount, ticks = _vblankLastQpc - _vblankBaseQpc;
                stats.Arrived = published - _publishedAtReset;
                stats.Drawn = _drawn;
                stats.Dropped = replaced - _replacedAtReset + _skipped;
                stats.MeasuredFps = DrawRateLocked();
                stats.RefreshHz = _hasVblankBase && vblanks > 0 && ticks > 0 ? vblanks * (double)Stopwatch.Frequency / ticks : 0;
                stats.Overlay = _overlay;
                stats.Composed = _composed;
                stats.DeviceResets = _deviceResets;
                arrivalToPresent = _arrivalToPresent.ToArray();
                presentToScreen = _presentToScreen.ToArray();
            }
            stats.ArrivalToPresent = TimingStats.From(arrivalToPresent);
            stats.PresentToScreen = TimingStats.From(presentToScreen);
            return stats;
        }

        /// <summary>
        /// The draw rate over the last second, as in <see cref="GetStats"/>, without copying or sorting anything: for a
        /// frequent status poll. GetStats allocates, so it is for the occasional report.
        /// </summary>
        public double MeasuredFps
        {
            get { lock (_statsLock) return DrawRateLocked(); }
        }

        /// <summary>Frames published by capture since Start; never reset. Lock-free, so it never waits behind a frame copy.</summary>
        public long FramesArrived => _slot?.Published ?? 0;

        /// <summary>The <see cref="Stopwatch"/> timestamp when the first frame since Start was presented; 0 until then.</summary>
        public long FirstPresentTimestamp => Interlocked.Read(ref _firstPresent);

        /// <summary>Frames drawn per second over the last second, as a rate (59.94, not a whole count). Call under _statsLock.</summary>
        double DrawRateLocked()
            => TimingStats.RatePerSecond(_drawTimes, _drawTimesCount, Stopwatch.GetTimestamp() - Stopwatch.Frequency, Stopwatch.Frequency);

        public void ResetStats()
        {
            var slot = _slot;
            long published = slot?.Published ?? 0, replaced = slot?.Replaced ?? 0;
            lock (_statsLock)
            {
                _publishedAtReset = published;
                _replacedAtReset = replaced;
                _resetTime = Stopwatch.GetTimestamp();
                _drawn = _skipped = 0;
                _overlay = _composed = _deviceResets = 0;
                _arrivalToPresent.Clear();
                _presentToScreen.Clear();
                _drawTimesNext = _drawTimesCount = 0;
                _hasVblankBase = false;
            }
        }

        /// <summary>Makes the next present fail with DXGI_ERROR_DEVICE_REMOVED, so the device is recreated.</summary>
        internal void SimulateDeviceLost() => Interlocked.Exchange(ref _simulateDeviceLost, 1);

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            if (_thread != null)
            {
                // Stop gave up waiting. Never free objects under a running thread: hand it the release, unless it
                // exited after the join gave up (it then got 0 from the exchange and left the release to this side).
                if (Interlocked.Exchange(ref _exitHandoff, 1) == 0)
                {
                    _log.Info("The render thread will release Direct3D when it exits");
                    return;
                }
                _thread = null;
            }
            ReleaseDevice();
        }

        void RenderLoop()
        {
            timeBeginPeriod(1); // makes the 1 ms measuring wait 1 ms, as in the spike
            try
            {
                var frameReady = _slot.FrameReady;
                while (!_stopRequested)
                {
                    bool measuring = _measureLatency;
                    // Only a signalled wake takes a frame, as in the spike. TryTake waits on the slot's lock, which capture
                    // holds for its whole frame copy, so taking on every 1 ms measuring wake contended with it. The event
                    // is an AutoResetEvent set after the copy: a frame published while this loop was busy leaves it set,
                    // so the next wait returns at once and that frame is drawn.
                    bool frameSignalled = frameReady.WaitOne(measuring ? 1 : 50);
                    if (_stopRequested) break;
                    try
                    {
                        RenderStep(measuring, frameSignalled);
                    }
                    catch (SharpDXException ex) when (IsDeviceLost(ex.ResultCode.Code))
                    {
                        DropFrameInFlight();
                        RecoverFromDeviceLoss(ex);
                    }
                    catch (Exception ex)
                    {
                        DropFrameInFlight();
                        if (!_drawFailureLogged) _log.Error("Drawing a frame failed", ex);
                        _drawFailureLogged = true;
                    }
                }
            }
            finally
            {
                timeEndPeriod(1);
                if (Interlocked.Exchange(ref _exitHandoff, 1) == 1) ReleaseAfterLateExit();
            }
        }

        /// <summary>Dispose stopped waiting for this thread, so it releases the device objects as their last user.</summary>
        void ReleaseAfterLateExit()
        {
            try
            {
                ReleaseDevice();
                _log.Info("The render thread released Direct3D after Dispose stopped waiting for it");
            }
            catch (Exception ex)
            {
                _log.Error("The render thread could not release Direct3D", ex);
            }
        }

        /// <summary>
        /// One wake of the render loop. Every wake keeps the device, the buffer size and (while measuring) the stats up
        /// to date; only a wake signalled by a new frame touches the slot.
        /// </summary>
        void RenderStep(bool measuring, bool frameSignalled)
        {
            if (_device == null && !TryRecreateDevice())
            {
                if (frameSignalled && _slot.TryTake(ref _frame, out _)) CountSkipped();
                return;
            }
            bool hasArea = UpdateBufferSize();
            if (!frameSignalled || !_slot.TryTake(ref _frame, out long arrival))
            {
                if (measuring) PollStats();
                return;
            }
            if (_paused || !hasArea)
            {
                CountSkipped();
                if (measuring) PollStats();
                return;
            }

            _frameInFlight = true;
            _renderer.Upload(_frame);
            _renderer.Draw(_backBufferView, _bufferWidth, _bufferHeight);
            bool vsync = _vsync, tearing = !vsync && _tearingSupported;
            if (Interlocked.Exchange(ref _simulateDeviceLost, 0) != 0)
                throw new SharpDXException(new Result(DeviceRemoved), "Simulated device loss");
            long presentTime = Stopwatch.GetTimestamp();
            if (vsync) _swapChain.Present(1, PresentFlags.None);
            else _swapChain.Present(0, tearing ? AllowTearingPresent : PresentFlags.None);
            if (Interlocked.Read(ref _firstPresent) == 0) Interlocked.Exchange(ref _firstPresent, Stopwatch.GetTimestamp());
            _frameInFlight = false;
            _drawFailureLogged = false;
            RecordDrawn(arrival, presentTime);

            if (measuring)
            {
                // With tearing, DXGI's display time can come before the Present call, so it is not a latency.
                RememberPresent((uint)_swapChain.LastPresentCount, presentTime, timed: !tearing);
                PollMedia();
                PollStats();
            }
            if (++_framesSinceLuma >= LumaEvery)
            {
                _framesSinceLuma = 0;
                RaiseLumaSampled();
            }
        }

        /// <summary>Resizes the swap chain to the window's client area. False when there is no area to draw on.</summary>
        bool UpdateBufferSize()
        {
            if (!GetClientRect(_hwnd, out RECT client)) return false;
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            if (width <= 0 || height <= 0) return false;
            if (width != _bufferWidth || height != _bufferHeight || _backBufferView == null)
            {
                _context.OutputMerger.SetRenderTargets((RenderTargetView)null);
                _backBufferView?.Dispose();
                _backBufferView = null;
                _swapChain.ResizeBuffers(BufferCount, width, height, BufferFormat, _swapChainFlags);
                _bufferWidth = width;
                _bufferHeight = height;
                _backBufferView = CreateBackBufferView();
            }
            return true;
        }

        void RecordDrawn(long arrival, long presentTime)
        {
            double arrivalToPresentMs = (presentTime - arrival) * MsPerTick;
            lock (_statsLock)
            {
                _drawn++;
                _arrivalToPresent.Add(arrivalToPresentMs);
                _drawTimes[_drawTimesNext] = presentTime;
                _drawTimesNext = (_drawTimesNext + 1) % _drawTimes.Length;
                if (_drawTimesCount < _drawTimes.Length) _drawTimesCount++;
            }
        }

        void CountSkipped()
        {
            lock (_statsLock) _skipped++;
        }

        void DropFrameInFlight()
        {
            if (!_frameInFlight) return;
            _frameInFlight = false;
            CountSkipped();
        }

        void RaiseLumaSampled()
        {
            var handler = LumaSampled;
            if (handler == null) return;
            try
            {
                handler(LumaSampler.Sample(_frame, _format));
            }
            catch (Exception ex)
            {
                if (!_lumaFailureLogged) _log.Error("A LumaSampled handler failed", ex);
                _lumaFailureLogged = true;
            }
        }

        void RememberPresent(uint presentId, long presentTime, bool timed)
        {
            _presentIds[_presentNext] = presentId;
            _presentTimes[_presentNext] = presentTime;
            _presentTimed[_presentNext] = timed;
            _presentNext = (_presentNext + 1) % RecentPresents;
        }

        /// <summary>
        /// Reads DXGI frame statistics as the spike's PollStats did. A sample is exact when the frame it
        /// names was first shown at the latest vblank (PresentRefreshCount == SyncRefreshCount).
        /// </summary>
        void PollStats()
        {
            if (_swapChain.TryGetFrameStatistics(out FrameStatistics fs).Failure) return;
            uint presentCount = (uint)fs.PresentCount, presentRefresh = (uint)fs.PresentRefreshCount, syncRefresh = (uint)fs.SyncRefreshCount;
            if (syncRefresh == 0) return;
            if (presentCount == _lastStatsPresentCount && syncRefresh == _lastStatsSyncCount) return;
            _lastStatsPresentCount = presentCount;
            _lastStatsSyncCount = syncRefresh;
            bool exact = false;
            long presentTime = 0;
            if (presentRefresh == syncRefresh && presentCount != _lastExactPresent)
            {
                _lastExactPresent = presentCount;
                exact = TryFindTimedPresent(presentCount, out presentTime) && fs.SyncQPCTime >= presentTime;
            }
            lock (_statsLock)
            {
                if (!_hasVblankBase)
                {
                    _hasVblankBase = true;
                    _vblankBaseCount = syncRefresh;
                    _vblankBaseQpc = fs.SyncQPCTime;
                }
                _vblankLastCount = syncRefresh;
                _vblankLastQpc = fs.SyncQPCTime;
                // Only frames presented since the last reset, so the window matches ArrivalToPresent.
                if (exact && presentTime >= _resetTime) _presentToScreen.Add((fs.SyncQPCTime - presentTime) * MsPerTick);
            }
        }

        bool TryFindTimedPresent(uint presentId, out long presentTime)
        {
            for (int i = 0; i < RecentPresents; i++)
                if (_presentTimed[i] && _presentIds[i] == presentId)
                {
                    presentTime = _presentTimes[i];
                    return true;
                }
            presentTime = 0;
            return false;
        }

        /// <summary>Counts how the latest frame reached the screen, as the spike's PollMedia did.</summary>
        void PollMedia()
        {
            var getStats = _getFrameStatisticsMedia;
            if (getStats == null || getStats(_media, out FrameStatisticsMedia stats) != 0) return;
            if (stats.CompositionMode != 0 && stats.CompositionMode != 1) return;
            lock (_statsLock)
            {
                if (stats.CompositionMode == 1) _overlay++;
                else _composed++;
            }
        }

        static bool IsDeviceLost(int code) => code == DeviceRemoved || code == DeviceHung || code == DeviceReset;

        void RecoverFromDeviceLoss(SharpDXException error)
        {
            string reason = "";
            try
            {
                if (_device != null) reason = $", removed reason 0x{_device.DeviceRemovedReason.Code:X8}";
            }
            catch (SharpDXException) { }
            _log.Error($"Direct3D device lost (0x{error.ResultCode.Code:X8}{reason})", error);
            ReleaseDevice();
            if (_stopRequested) return; // stopping anyway: Start creates a device if the presenter runs again
            _nextRecreateAttempt = 0;
            TryRecreateDevice();
        }

        /// <summary>Recreates the device, swap chain and renderer; after a failure, tries again a second later.</summary>
        bool TryRecreateDevice()
        {
            long now = Stopwatch.GetTimestamp();
            if (_stopRequested || now < _nextRecreateAttempt) return false;
            try
            {
                CreateDevice();
                _renderer.Configure(_format);
            }
            catch (Exception ex)
            {
                ReleaseDevice();
                _nextRecreateAttempt = now + Stopwatch.Frequency;
                if (!_recreateFailureLogged) _log.Error("Could not recreate the Direct3D device; trying again every second", ex);
                _recreateFailureLogged = true;
                return false;
            }
            _recreateFailureLogged = false;
            lock (_statsLock)
            {
                _deviceResets++;
                _hasVblankBase = false; // a new swap chain starts its own counts
            }
            _log.Info("Direct3D device recreated after a device loss");
            return true;
        }

        /// <summary>How long the last device creation took in total, in its swap chain, and in its shaders (all in ms). Diagnostic.</summary>
        public double CreateDeviceMs { get; private set; }
        public double CreateSwapChainMs { get; private set; }
        public double CreateShadersMs { get; private set; }

        void CreateDevice()
        {
            var timer = Stopwatch.StartNew();
            try
            {
                _device = new D3DDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                _context = _device.ImmediateContext;
                GetClientSize(out int width, out int height);
                using (var dxgiDevice = _device.QueryInterface<DxgiDevice1>())
                using (var adapter = dxgiDevice.Adapter)
                using (var factory = adapter.GetParent<Factory2>())
                {
                    dxgiDevice.MaximumFrameLatency = 1;
                    double before = timer.Elapsed.TotalMilliseconds;
                    _swapChain = CreateSwapChain(factory, width, height);
                    factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
                    CreateSwapChainMs = timer.Elapsed.TotalMilliseconds - before;
                }
                _bufferWidth = width;
                _bufferHeight = height;
                _backBufferView = CreateBackBufferView();
                OpenSwapChainMedia();
                _renderer = new FrameRenderer(_device);
                CreateShadersMs = _renderer.ShaderMs;
                CreateDeviceMs = timer.Elapsed.TotalMilliseconds;
            }
            catch
            {
                ReleaseDevice();
                throw;
            }
        }

        SwapChain1 CreateSwapChain(Factory2 factory, int width, int height)
        {
            var description = new SwapChainDescription1
            {
                Width = width, Height = height, Format = BufferFormat, Stereo = false,
                SampleDescription = new SampleDescription(1, 0), Usage = Usage.RenderTargetOutput,
                BufferCount = BufferCount, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore, Flags = AllowTearingFlag,
            };
            SwapChain1 swapChain;
            try
            {
                swapChain = new SwapChain1(factory, _device, _hwnd, ref description);
            }
            catch (SharpDXException)
            {
                description.Flags = SwapChainFlags.None;
                swapChain = new SwapChain1(factory, _device, _hwnd, ref description);
            }
            _swapChainFlags = description.Flags;
            _tearingSupported = description.Flags == AllowTearingFlag;
            return swapChain;
        }

        RenderTargetView CreateBackBufferView()
        {
            using (var backBuffer = _swapChain.GetBackBuffer<Texture2D>(0))
                return new RenderTargetView(_device, backBuffer);
        }

        void OpenSwapChainMedia()
        {
            var iid = SwapChainMediaIid;
            if (Marshal.QueryInterface(_swapChain.NativePointer, ref iid, out IntPtr media) != 0 || media == IntPtr.Zero)
            {
                _log.Info("IDXGISwapChainMedia is not available, so the presentation mode is not measured");
                return;
            }
            _media = media;
            _getFrameStatisticsMedia = Marshal.GetDelegateForFunctionPointer<GetFrameStatisticsMediaFn>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(media), 3 * IntPtr.Size));
        }

        void GetClientSize(out int width, out int height)
        {
            width = height = 0;
            if (GetClientRect(_hwnd, out RECT client))
            {
                width = client.Right - client.Left;
                height = client.Bottom - client.Top;
            }
            width = Math.Max(1, width); // a minimised window gets a 1x1 swap chain until it has an area again
            height = Math.Max(1, height);
        }

        /// <summary>Releases every device object. ClearState and Flush make the old swap chain really go before a new one takes the window.</summary>
        void ReleaseDevice()
        {
            _renderer?.Dispose();
            _renderer = null;
            _backBufferView?.Dispose();
            _backBufferView = null;
            _getFrameStatisticsMedia = null;
            if (_media != IntPtr.Zero)
            {
                Marshal.Release(_media);
                _media = IntPtr.Zero;
            }
            _swapChain?.Dispose();
            _swapChain = null;
            if (_context != null)
            {
                _context.ClearState();
                _context.Flush();
                _context.Dispose();
                _context = null;
            }
            _device?.Dispose();
            _device = null;
            _bufferWidth = _bufferHeight = 0;
            // A new swap chain counts presents from the start again.
            Array.Clear(_presentTimed, 0, RecentPresents);
            _lastStatsPresentCount = _lastStatsSyncCount = _lastExactPresent = 0;
        }

        /// <summary>The most recent samples in a fixed buffer, so recording never allocates.</summary>
        sealed class SampleRing
        {
            readonly double[] _items;
            int _next, _count;

            public SampleRing(int capacity) { _items = new double[capacity]; }

            public void Add(double value)
            {
                _items[_next] = value;
                _next = (_next + 1) % _items.Length;
                if (_count < _items.Length) _count++;
            }

            public void Clear() { _next = 0; _count = 0; }

            /// <summary>A copy of the samples, in no particular order.</summary>
            public double[] ToArray()
            {
                var copy = new double[_count];
                Array.Copy(_items, copy, _count);
                return copy;
            }
        }
    }
}
