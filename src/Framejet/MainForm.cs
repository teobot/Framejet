using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using static System.FormattableString;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using Framejet.Core;
using Framejet.Core.Audio;
using Framejet.Core.Settings;
using Framejet.Core.Ui;
using Framejet.Core.Timing;
using Framejet.Core.Video;
using Framejet.Platform.Audio;
using Framejet.Platform.Devices;
using Framejet.Platform.Os;
using Framejet.Platform.Video;

namespace Framejet
{
    /// <summary>
    /// The main window. It connects the sessions to the backends, device notifications, settings, window placement,
    /// keep-awake and the stats log; the decisions themselves live in the sessions.
    /// </summary>
    sealed class MainForm : Form
    {
        const int WmDeviceChange = 0x0219;
        const int SwRestore = 9;
        static readonly TimeSpan DeviceChangeDelay = TimeSpan.FromMilliseconds(800);
        static readonly TimeSpan DefaultOutputChangeDelay = TimeSpan.FromMilliseconds(300);
        static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
        const int StatsIntervalMs = 60000;
        const int CursorHideDelayMs = 2000;
        const int ToolbarTickMs = 100;
        const int StatusIntervalMs = 500;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hwnd, int command);

        readonly bool _selfTesting;
        readonly SettingsStore _store;
        readonly FileLog _fileLog;
        readonly VideoSurface _surface;
        readonly Debouncer _deviceChange, _defaultOutputChange, _saveDebounce;
        readonly Timer _statsTimer, _cursorTimer, _toolbarTimer, _statusTimer;
        readonly ToolbarAutoHide _autoHide;
        readonly List<string> _warnings = new List<string>();
        readonly ContextMenuStrip _menu;
        readonly SelfTestRunner _selfTest;
        readonly ScreenInfo _startFullscreenOn;
        ControlDispatcher _dispatcher;
        AudioDeviceWatcher _watcher;
        string _runningKey;
        Rectangle _normalBounds;
        FormWindowState _normalState;
        FormWindowState _lastShownState; // the last state that was not minimised: a minimised window forgets it was maximised
        ToolbarWindow _toolbar;
        bool _menuOpen;
        bool _savePending;
        bool _audioDevicesDirty; // the next Audio.Changed may come from a device change: list the outputs again
        bool _cursorHidden;
        bool _firstFrameLogged;
        bool _closed;

        public MainForm(CommandLine cmd)
        {
            var constructorStart = DateTime.Now; // for the startup timings logged below
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            var carryOver = SettingsCarryOver.Run(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), cmd.SettingsPath == null, out var carryOverError);
            _selfTesting = cmd.SelfTestReport != null;

            var settingsPath = Path.GetFullPath(cmd.SettingsPath ?? SettingsStore.DefaultPath);
            _store = new SettingsStore(settingsPath);
            _fileLog = new FileLog(Path.Combine(Path.GetDirectoryName(settingsPath), "Framejet.log"));
            if (_selfTesting) _selfTest = new SelfTestRunner(this, cmd);
            Log = _selfTest != null ? _selfTest.RecordErrors(_fileLog) : _fileLog;
            UnhandledExceptions.Log = Log;
            Log.Info(UiText.NameAndVersion(Application.ProductVersion) + (_selfTesting ? " self-test started" : " started"));
            Log.Info("Settings " + settingsPath);
            Log.Info("Startup timing: the form's constructor began " + MsSinceProcessStart(constructorStart) + " ms after the process started");
            if (carryOver == CarryOverResult.Copied) Log.Info(@"Copied settings.ini from %AppData%\PlayThrough");
            else if (carryOver == CarryOverResult.Failed) Log.Info(@"Couldn't copy settings.ini from %AppData%\PlayThrough: " + carryOverError);
            Settings = _store.Load();
            Clock = new WinFormsScheduler();
            NoSignal = new NoSignalDetector();
            _autoHide = new ToolbarAutoHide(Clock.Now);

            Text = "Framejet";
            Icon = new Icon(typeof(MainForm).Assembly.GetManifestResourceStream("Framejet.ico"));
            BackColor = DarkTheme.Window;
            StartPosition = FormStartPosition.Manual;
            _surface = new VideoSurface { Dock = DockStyle.Fill };
            Controls.Add(_surface);
            _menu = VideoMenu.Build(this);
            _surface.SetMenu(_menu);
            _menu.Opening += (s, e) => ShowCursorNow(); // a hidden pointer would make the menu unusable
            _menu.Opened += (s, e) => { _menuOpen = true; UpdateHeld(); };
            _menu.Closed += (s, e) => { _menuOpen = false; UpdateHeld(); };
            _surface.VideoDoubleClick += (s, e) => ToggleFullscreen();
            _surface.VideoMouseMove += (s, e) => OnPictureMouseMove();

            var placement = PlacementRules.Restore(Settings.Window, Screens());
            Bounds = placement.Bounds;
            if (placement.Maximized) WindowState = FormWindowState.Maximized;
            _lastShownState = WindowState == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
            if (placement.Fullscreen) _startFullscreenOn = placement.FullscreenScreen;

            _deviceChange = new Debouncer(Clock, DeviceChangeDelay, OnDevicesChanged);
            _defaultOutputChange = new Debouncer(Clock, DefaultOutputChangeDelay, OnDefaultOutputChanged);
            _saveDebounce = new Debouncer(Clock, SaveDelay, WriteSettings);
            _statsTimer = new Timer { Interval = StatsIntervalMs };
            _statsTimer.Tick += (s, e) => LogStats();
            _cursorTimer = new Timer { Interval = CursorHideDelayMs };
            _cursorTimer.Tick += (s, e) => HideCursorNow();
            _toolbarTimer = new Timer { Interval = ToolbarTickMs };
            _toolbarTimer.Tick += (s, e) => UpdateToolbar();
            _statusTimer = new Timer { Interval = StatusIntervalMs };
            _statusTimer.Tick += (s, e) => UpdateStatus();
            LocationChanged += (s, e) => FitToScreen();
            SizeChanged += (s, e) => FitToScreen();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        public VideoSession Video { get; private set; }
        public AudioSession Audio { get; private set; }
        public DirectShowVideoBackend VideoBackend { get; private set; }
        public WasapiAudioBackend AudioBackend { get; private set; }
        public AppSettings Settings { get; }
        public IScheduler Clock { get; }
        public NoSignalDetector NoSignal { get; }
        public ILog Log { get; }
        public ToolbarWindow Toolbar => _toolbar;
        public bool IsFullscreen { get; private set; }

        /// <summary>The right-click menu; also shown by the toolbar.</summary>
        public ContextMenuStrip VideoContextMenu => _menu;

        public IReadOnlyList<ScreenInfo> Screens()
            => Screen.AllScreens.Select(s => new ScreenInfo(s.DeviceName, s.Bounds, s.WorkingArea, s.Primary)).ToList();

        /// <summary>Borderless over the whole screen the window is on, or back to the window it was.</summary>
        public void ToggleFullscreen()
        {
            if (IsFullscreen) LeaveFullscreen();
            else EnterFullscreen(PlacementRules.ScreenFor(WindowState == FormWindowState.Minimized ? RestoreBounds : Bounds, Screens()));
        }

        /// <summary>Handles the app's keys; true when it did. F11 and Alt+Enter toggle fullscreen, Esc leaves it, M mutes.</summary>
        public bool HandleShortcut(Keys keyData)
        {
            if (_closed) return false;
            switch (keyData)
            {
                case Keys.F11:
                case Keys.Alt | Keys.Enter:
                    ToggleFullscreen();
                    return true;
                case Keys.Escape:
                    if (!IsFullscreen) return false;
                    LeaveFullscreen();
                    return true;
                case Keys.M:
                    if (Audio == null) return false;
                    Audio.SetMuted(!Settings.Muted);
                    return true;
                default:
                    return false;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            => HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

        /// <summary>The V-Sync menu item: in the settings and the running presenter.</summary>
        public void ToggleVSync()
        {
            if (_closed) return;
            Settings.VSync = !Settings.VSync;
            if (VideoBackend != null) VideoBackend.VSync = Settings.VSync;
            SaveSettings();
            _toolbar?.RefreshToggles(); // from the menu as well as from the toolbar
        }

        /// <summary>User-initiated rescan: also takes the card back after another app took it.</summary>
        public void RescanDevices()
        {
            if (_closed || Video == null || Audio == null) return;
            Video.Rescan();
            _audioDevicesDirty = true;
            Audio.DevicesChanged();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTheme.DarkTitleBar(Handle);
        }

        /// <summary>While fullscreen the window follows its screen: moved, resized or a display change puts it back over the whole screen it is on.</summary>
        void FitToScreen()
        {
            if (_closed || !IsFullscreen || WindowState != FormWindowState.Normal) return;
            var target = PlacementRules.ScreenFor(Bounds, Screens());
            if (target != null && Bounds != target.Bounds) Bounds = target.Bounds;
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // Raised on another thread.
            if (_closed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(FitToScreen)); }
            catch (InvalidOperationException) { } // the window went away meanwhile
        }

        /// <summary>Alt+Tab away while the cursor is hidden: show it, and hide it again only after the next move over the picture.</summary>
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_closed) return;
            _cursorTimer.Stop();
            ShowCursorNow();
        }

        void OnPictureMouseMove()
        {
            if (_closed) return;
            _autoHide.MouseMoved(Clock.Now);
            ShowCursorNow();
            _cursorTimer.Stop();
            if (IsFullscreen) _cursorTimer.Start();
        }

        void HideCursorNow()
        {
            _cursorTimer.Stop();
            if (_closed || !IsFullscreen || _cursorHidden || _menu.Visible) return;
            _cursorHidden = true;
            Cursor.Hide();
        }

        /// <summary>Every Hide is matched by exactly one Show.</summary>
        void ShowCursorNow()
        {
            if (!_cursorHidden) return;
            _cursorHidden = false;
            Cursor.Show();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Log.Info("Startup timing: the form was shown " + MsSinceProcessStart(DateTime.Now) + " ms after the process started");
            if (_startFullscreenOn != null) EnterFullscreen(_startFullscreenOn);
            _selfTest?.Start(); // first: V-Sync on and muted, in memory, before anything starts
            StartDevices();
        }

        /// <summary>Whole milliseconds from the process start to <paramref name="at"/>, as text; "?" if the start time can't be read. A diagnostic.</summary>
        static string MsSinceProcessStart(DateTime at)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    return ((at - process.StartTime).TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        void StartDevices()
        {
            _dispatcher = new ControlDispatcher(this);
            VideoBackend = new DirectShowVideoBackend(_surface, Log) { VSync = Settings.VSync, Paused = WindowState == FormWindowState.Minimized };
            AudioBackend = new WasapiAudioBackend(Log);
            try
            {
                _watcher = new AudioDeviceWatcher(_dispatcher);
                _watcher.DevicesChanged += _deviceChange.Trigger;
                _watcher.DefaultOutputChanged += _defaultOutputChange.Trigger;
            }
            catch (Exception ex)
            {
                _watcher = null;
                Log.Error("Watching audio devices failed", ex);
            }

            Video = new VideoSession(VideoBackend, Settings, Clock, () => Screen.FromControl(this).Bounds.Size, SaveSettings, Log);
            Audio = new AudioSession(AudioBackend, Settings, _dispatcher, Clock, SaveSettings, Log);
            Video.Changed += OnVideoChanged;
            Audio.Changed += OnAudioChanged;
            _toolbar = new ToolbarWindow(this);
            _toolbar.HeldChanged += (s, e) => UpdateHeld();
            _toolbar.RefreshContent();
            _toolbarTimer.Start();
            _statusTimer.Start();
            VideoBackend.LumaSampled += OnLumaSampled;
            if (!_selfTesting) _statsTimer.Start(); // its audio stats reset would zero the self-test's figures
            Video.Refresh();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDeviceChange)
            {
                _deviceChange?.Trigger();
            }
            else if (m.Msg == SingleInstance.ActivateMessage && m.Msg != 0)
            {
                ComeForward();
                return;
            }
            base.WndProc(ref m);
        }

        void ComeForward()
        {
            if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, SwRestore);
            Activate();
        }

        void OnDevicesChanged()
        {
            if (_closed || Video == null) return;
            Video.Refresh();
            _audioDevicesDirty = true;
            Audio.DevicesChanged();
        }

        void OnDefaultOutputChanged()
        {
            if (_closed || Audio == null) return;
            _audioDevicesDirty = true;
            Audio.DefaultOutputChanged();
        }

        void OnAudioChanged()
        {
            if (_closed) return;
            var devices = _audioDevicesDirty;
            _audioDevicesDirty = false;
            _toolbar?.RefreshContent(devices); // a volume or mute change does not list the outputs again
        }

        void OnVideoChanged()
        {
            if (_closed) return;
            bool running = Video.State == VideoState.Running;
            var message = running ? null : Video.Message;
            if (message != null && message != _surface.Message) Log.Info("Video: " + message);
            _surface.Message = message;
            KeepAwake.Set(running);

            // A start (a new device or mode, or the same one coming back) begins a fresh no-signal check.
            var key = running ? Video.Device.Path + "|" + Video.Mode?.Key : null;
            if (key != null && key != _runningKey) NoSignal.Reset();
            _runningKey = key;

            var audioTimer = Stopwatch.StartNew();
            Audio.Follow(running ? Video.Device : null, running ? DeviceContainers.ForDirectShowDevicePath(Video.Device.Path) : Guid.Empty);
            if (running) Log.Info(Invariant($"Audio follow (devices listed, audio started) took {audioTimer.Elapsed.TotalMilliseconds:0} ms"));
            _toolbar?.RefreshContent();
        }

        /// <summary>Raised on the render thread: post, never block.</summary>
        void OnLumaSampled(LumaStats stats) => _dispatcher.Post(() =>
        {
            if (_closed) return;
            if (NoSignal.Update(stats, Clock.Now))
                Log.Info(NoSignal.NoSignal
                    ? Invariant($"No picture: the card sends black frames (mean {stats.Mean:0.0}, dark {stats.DarkFraction * 100:0}%)")
                    : Invariant($"The picture is back (mean {stats.Mean:0.0}, dark {stats.DarkFraction * 100:0}%)"));
        });

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (VideoBackend != null) VideoBackend.Paused = WindowState == FormWindowState.Minimized;
            if (WindowState != FormWindowState.Minimized && !IsFullscreen)
                _lastShownState = WindowState == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        void OnFullscreenChanged()
        {
            _autoHide.SetFullscreen(IsFullscreen);
            _autoHide.Tick(Clock.Now); // SetFullscreen recomputes with the last time it saw
            if (IsFullscreen) _toolbar?.HideNow();
        }

        void UpdateHeld()
        {
            if (_closed) return;
            _autoHide.SetHeld((_toolbar != null && _toolbar.Held) || _menuOpen, Clock.Now);
        }

        /// <summary>Every 100 ms: the toolbar follows the auto-hide state.</summary>
        void UpdateToolbar()
        {
            if (_closed || _toolbar == null) return;
            _autoHide.Tick(Clock.Now);
            if (_autoHide.Visible && WindowState != FormWindowState.Minimized && !IsFullscreen) _toolbar.ShowAnimated();
            else _toolbar.HideAnimated();
        }

        /// <summary>Every 500 ms: the toolbar's status text, and a new warning brings the toolbar up.</summary>
        void UpdateStatus()
        {
            if (_closed || _toolbar == null || Video == null || Audio == null) return;
            LogFirstFrame();
            var warnings = new List<string>();
            if (Video.Warning != null) warnings.Add(Video.Warning);
            if (Audio.Warning != null) warnings.Add(Audio.Warning);
            if (NoSignal.NoSignal) warnings.Add(UiText.NoPicture);
            if (warnings.Any(w => !_warnings.Contains(w))) _autoHide.WarningRaised(Clock.Now);
            _warnings.Clear();
            _warnings.AddRange(warnings);
            var fps = VideoBackend != null ? VideoBackend.MeasuredFps : 0; // not GetStats: it allocates ~150 KB, and the GCs pause the render thread
            _toolbar.SetStatus(StatusText.Format(Video.Mode, fps, warnings));
        }

        /// <summary>
        /// Once per launch: the time from the process start to the first frame on screen, against spec 2's "opens in
        /// about a second". The presenter stamps that frame, so the value does not depend on when this tick sees it.
        /// </summary>
        void LogFirstFrame()
        {
            if (_firstFrameLogged || VideoBackend == null) return;
            long presented = VideoBackend.FirstPresentTimestamp;
            if (presented == 0) return;
            _firstFrameLogged = true;
            try
            {
                var sincePresent = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - presented) / (double)Stopwatch.Frequency);
                DateTime processStart;
                using (var process = Process.GetCurrentProcess()) processStart = process.StartTime;
                var ms = (DateTime.Now - sincePresent - processStart).TotalMilliseconds;
                Log.Info(string.Format(CultureInfo.InvariantCulture, "First frame after {0:0} ms", ms));
            }
            catch (Exception ex)
            {
                Log.Info("Couldn't measure the time to the first frame: " + ex.Message);
            }
        }

        void LogStats()
        {
            if (_closed || VideoBackend == null || AudioBackend == null) return;
            var video = VideoBackend.GetStats();
            var audio = AudioBackend.GetStats(reset: true);
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "stats: video fps={0:0.0} drawn={1} dropped={2} mismatched={3}; audio buffer={4:0.0} ms max={5:0.0} ms trims={6}",
                video.MeasuredFps, video.Drawn, video.Dropped, video.SizeMismatches, audio.BufferedMs, audio.MaxBufferedMs, audio.Trims));
        }

        void EnterFullscreen(ScreenInfo screen)
        {
            if (IsFullscreen || screen == null) return;
            _normalState = WindowState == FormWindowState.Minimized ? _lastShownState : WindowState;
            _normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal; // a maximised window ignores new bounds
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen.Bounds;
            IsFullscreen = true; // last, so that the re-fit does not act on the half-way window
            OnFullscreenChanged();
            _cursorTimer.Stop();
            _cursorTimer.Start();
        }

        void LeaveFullscreen()
        {
            if (!IsFullscreen) return;
            IsFullscreen = false;
            OnFullscreenChanged();
            _cursorTimer.Stop();
            ShowCursorNow();
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _normalBounds;
            if (_normalState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
        }

        /// <summary>
        /// Only saves. WinForms also raises this for WM_QUERYENDSESSION, before Windows knows whether the session
        /// really ends: another app can veto the shutdown, and then the window must carry on as it was.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || _selfTesting) return;
            RememberWindow();
            SaveSettings();
            WriteSettings(); // flush a pending debounced save before closing
        }

        /// <summary>Raised on a real close, and when the Windows session ends (WM_ENDSESSION with TRUE): tears everything down.</summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_closed) return;
            _closed = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _cursorTimer.Stop();
            ShowCursorNow();
            _statsTimer.Stop();
            _statsTimer.Dispose();
            _toolbarTimer.Stop();
            _statusTimer.Stop();
            if (Audio != null) Audio.Changed -= OnAudioChanged;
            _toolbar?.Dispose();
            _toolbar = null;
            if (_watcher != null)
            {
                _watcher.DevicesChanged -= _deviceChange.Trigger;
                _watcher.DefaultOutputChanged -= _defaultOutputChange.Trigger;
            }
            if (VideoBackend != null) VideoBackend.LumaSampled -= OnLumaSampled;
            if (Video != null) Video.Changed -= OnVideoChanged;

            // 1. Stop video and audio (with any pending audio retry), and let the PC sleep again.
            Video?.Stop();
            Audio?.Dispose();
            KeepAwake.Set(false);

            // 2. Let go of the devices, then the log.
            _watcher?.Dispose();
            VideoBackend?.Dispose();
            AudioBackend?.Dispose();
            Log.Info("Framejet closed");
            UnhandledExceptions.Log = null; // from now on, unhandled exceptions go to a file next to the settings
            _fileLog.Dispose();
        }

        void RememberWindow()
        {
            Rectangle normal;
            bool maximized;
            if (IsFullscreen)
            {
                normal = _normalBounds;
                maximized = _normalState == FormWindowState.Maximized;
            }
            else if (WindowState == FormWindowState.Normal)
            {
                normal = Bounds;
                maximized = false;
            }
            else
            {
                normal = RestoreBounds;
                maximized = WindowState == FormWindowState.Maximized || (WindowState == FormWindowState.Minimized && _lastShownState == FormWindowState.Maximized);
            }
            var w = Settings.Window;
            w.X = normal.X;
            w.Y = normal.Y;
            w.Width = normal.Width;
            w.Height = normal.Height;
            w.HasBounds = true;
            w.Maximized = maximized;
            w.Fullscreen = IsFullscreen;
            w.ScreenName = Screen.FromControl(this).DeviceName;
        }

        /// <summary>Never during a self-test. A failed save is logged, never thrown.</summary>
        void SaveSettings()
        {
            if (_selfTesting) return;
            _savePending = true;
            if (_closed) WriteSettings(); // nothing is left to debounce with
            else _saveDebounce.Trigger();
        }

        /// <summary>Writes the settings if a save is pending: from the debounce, and flushed when the window closes.</summary>
        void WriteSettings()
        {
            if (!_savePending) return;
            _savePending = false;
            try
            {
                _store.Save(Settings);
            }
            catch (Exception ex)
            {
                Log.Error("Saving settings failed", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _cursorTimer.Dispose();
                ShowCursorNow(); // balance a Hide, if the window is disposed while the cursor is hidden
                _menu.Dispose();
                _statsTimer.Dispose();
                _toolbarTimer.Dispose();
                _statusTimer.Dispose();
                _toolbar?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
