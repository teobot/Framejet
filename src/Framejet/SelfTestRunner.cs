using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Framejet.Core;
using Framejet.Core.Devices;
using Framejet.Core.Settings;
using Framejet.Core.Ui;
using Framejet.Core.Video;
using Framejet.Platform.Audio;
using Framejet.Platform.Os;
using Framejet.Platform.Video;
using static System.FormattableString;

namespace Framejet
{
    /// <summary>
    /// <c>--selftest</c>: drives the real window, windowed and then fullscreen, measures how frames reach the screen,
    /// writes a UTF-8 report, and closes the app with exit code 0 for a pass and 1 for a fail.
    /// </summary>
    sealed class SelfTestRunner
    {
        static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
        static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
        static readonly TimeSpan ToolbarHideTimeout = TimeSpan.FromSeconds(5);
        static readonly TimeSpan WindowedWarmup = NoSignalDetector.HoldTime + TimeSpan.FromSeconds(1); // at least the old 2.5 s
        static readonly TimeSpan FullscreenSettle = TimeSpan.FromSeconds(1);
        const int MaxErrors = 20;

        readonly MainForm _form;
        readonly CommandLine _cmd;
        readonly ControlDispatcher _ui;
        readonly object _errorsLock = new object();
        readonly List<string> _errors = new List<string>();
        double _lumaMeanSum, _lumaSpreadSum, _lumaDarkSum; // UI thread only
        int _lumaCount;
        int _windowedDisplayHz, _fullscreenDisplayHz; // the display mode's refresh for the screen each phase ran on; 0 = unknown
        DateTime _startedAt;
        bool _started;
        string _toolbarState; // null: the windowed phase was never reached

        public SelfTestRunner(MainForm form, CommandLine cmd)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
            _cmd = cmd ?? throw new ArgumentNullException(nameof(cmd));
            _ui = new ControlDispatcher(form);
        }

        /// <summary>Wraps the app's log so that errors logged during the run also go into the report.</summary>
        public ILog RecordErrors(ILog log) => new ErrorRecorder(log, this);

        /// <summary>
        /// Call from OnShown before the devices start. Turns V-Sync on and mutes, in memory only, so audio plays at
        /// gain 0 and nothing is saved; the measuring begins once OnShown has returned.
        /// </summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            _startedAt = DateTime.Now;
            _form.Settings.VSync = true;
            _form.Settings.Muted = true;
            Environment.ExitCode = 1; // until it passes: a run that never finishes must not look like a pass
            _form.TopMost = true;     // measure the picture as it is seen: in front, not covered by another window
            _form.Activate();
            _form.BeginInvoke(new Action(() => _ = RunAsync()));
        }

        async Task RunAsync()
        {
            var windowed = new PresenterStats();
            var fullscreen = new PresenterStats();
            var audio = new AudioStats();
            ScreenInfo screen = null;
            string failure = null;
            DirectShowVideoBackend backend = null;
            try
            {
                // Wait up to 15 s for the video to run.
                var waited = Stopwatch.StartNew();
                while (!VideoRunning && waited.Elapsed < StartTimeout)
                    if (!await Wait(PollInterval)) return;

                if (!VideoRunning)
                {
                    failure = "video didn't start: " + (_form.Video?.Message ?? "no video session");
                }
                else
                {
                    backend = _form.VideoBackend;
                    var running = Stopwatch.StartNew(); // since the video was seen running
                    var seconds = TimeSpan.FromSeconds(_cmd.Seconds);

                    // Windowed.
                    if (_form.IsFullscreen) _form.ToggleFullscreen();
                    // Warm up first, long enough for a black picture to raise its no-signal warning (which brings the toolbar up).
                    var warmup = WindowedWarmup - running.Elapsed;
                    if (warmup > TimeSpan.Zero && !await Wait(warmup)) return;
                    // The toolbar is a window of its own over the picture: measure once it is gone.
                    var toolbarWait = Stopwatch.StartNew();
                    do
                    {
                        if (!await Wait(PollInterval)) return;
                    }
                    while (!(_form.Toolbar?.FullyHidden ?? true) && toolbarWait.Elapsed < ToolbarHideTimeout);
                    var hiddenAtStart = _form.Toolbar?.FullyHidden ?? true;
                    var shownBefore = _form.Toolbar?.ShownCount ?? 0;
                    _windowedDisplayHz = DisplayMode.CurrentRefreshHz(PlacementRules.ScreenFor(_form.Bounds, _form.Screens())?.Name);
                    backend.MeasureLatency = true;
                    backend.ResetStats();
                    _form.AudioBackend.GetStats(reset: true);
                    backend.LumaSampled += OnLumaSampled;
                    if (!await Wait(seconds)) return;
                    windowed = backend.GetStats();
                    var stayedHidden = (_form.Toolbar?.FullyHidden ?? true) && (_form.Toolbar?.ShownCount ?? 0) == shownBefore;
                    _toolbarState = !hiddenAtStart ? "still visible" : stayedHidden ? "hidden during windowed measurement" : "shown during windowed measurement";

                    // Fullscreen.
                    if (!_form.IsFullscreen) _form.ToggleFullscreen();
                    screen = PlacementRules.ScreenFor(_form.Bounds, _form.Screens());
                    _fullscreenDisplayHz = DisplayMode.CurrentRefreshHz(screen?.Name);
                    if (!await Wait(FullscreenSettle)) return;
                    backend.ResetStats();
                    if (!await Wait(seconds)) return;
                    fullscreen = backend.GetStats();
                    audio = _form.AudioBackend.GetStats(reset: false);
                    _form.ToggleFullscreen();
                }
            }
            catch (Exception ex)
            {
                failure = "self-test error: " + ex.Message;
                _form.Log.Error("The self-test failed", ex);
            }
            finally
            {
                if (backend != null) backend.LumaSampled -= OnLumaSampled;
            }
            Finish(failure, windowed, fullscreen, screen, audio);
        }

        bool VideoRunning => _form.Video != null && _form.Video.State == VideoState.Running;

        /// <summary>False once the window is gone, which abandons the run (the exit code stays 1).</summary>
        async Task<bool> Wait(TimeSpan delay)
        {
            await Task.Delay(delay);
            return !_form.IsDisposed;
        }

        void Finish(string failure, PresenterStats windowed, PresenterStats fullscreen, ScreenInfo screen, AudioStats audio)
        {
            var verdict = Check(fullscreen, _fullscreenDisplayHz);
            var mismatch = SizeMismatch(windowed, fullscreen);
            if (mismatch != null) AddError(mismatch);
            var reason = failure ?? (verdict.Pass ? null : verdict.Reason) ?? mismatch;
            var result = reason == null ? "PASS" : "FAIL (" + reason + ")";
            bool written = false;
            try
            {
                Save(_cmd.SelfTestReport, Report(result, verdict, windowed, fullscreen, screen, audio));
                written = true;
            }
            catch (Exception ex)
            {
                _form.Log.Error("Writing the self-test report failed", ex);
            }
            _form.Log.Info("Self-test result: " + result);
            Environment.ExitCode = reason == null && written ? 0 : 1;
            _form.Close();
        }

        /// <summary>
        /// A report that only says the self-test failed, and why: for when it can't run at all. Never throws; the exit
        /// code still says FAIL.
        /// </summary>
        public static void WriteFailure(string path, string reason)
        {
            try
            {
                Save(path, Header(DateTime.Now) + "\r\nresult: FAIL (" + reason + ")\r\n");
            }
            catch (Exception)
            {
                // Nowhere to write it.
            }
        }

        static string Header(DateTime startedAt)
            => "Framejet self-test " + startedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>Writes the report in UTF-8, creating its folder.</summary>
        static void Save(string path, string text)
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, text, Encoding.UTF8);
        }

        /// <summary>
        /// A frame of the wrong size draws a shifted picture that the timing checks can't see, so any mismatch fails the
        /// run. The counts run from the start of the video, warm-up included. Null when every frame had the right size.
        /// </summary>
        static string SizeMismatch(PresenterStats windowed, PresenterStats fullscreen)
        {
            var s = fullscreen.SizeMismatches > 0 ? fullscreen : windowed;
            if (s.SizeMismatches == 0) return null;
            return Invariant($"frame size mismatch: expected {s.ExpectedFrameBytes} bytes, got {s.MismatchedFrameBytes} ({s.SizeMismatches} frames)");
        }

        /// <summary>
        /// The delay check, judged against the display mode's refresh when Windows reports one (the DXGI-measured rate can read low
        /// in fullscreen with no signal, which would loosen the bars), else the measured rate. It still fails when no refresh rate was
        /// measured: that proves the frame statistics work, and the check passes vacuously at 0 Hz.
        /// </summary>
        static DelayVerdict Check(PresenterStats s, int displayHz)
        {
            var verdict = DelayCheck.Evaluate(s.ArrivalToPresent, s.PresentToScreen, displayHz > 0 ? displayHz : s.RefreshHz);
            if (verdict.Pass && !(s.RefreshHz > 0)) return new DelayVerdict(false, "no refresh measured");
            return verdict;
        }

        string Report(string result, DelayVerdict verdict, PresenterStats windowed, PresenterStats fullscreen, ScreenInfo screen, AudioStats audio)
        {
            var video = _form.Video;
            var format = _form.VideoBackend?.Format;
            var sound = _form.Audio;
            var report = new StringBuilder();
            void Line(string text) => report.Append(text).Append("\r\n");

            Line(Header(_startedAt));
            Line("result: " + result);
            Line("video devices: " + DeviceNames(video?.Devices));
            Line("video device: " + (video?.Device == null ? "none (none)" : video.Device.Name + " (" + PickText(video.PickReason) + ")"));
            Line("mode: " + (video?.Mode == null ? "none" : video.Mode.Key + " (" + video.Mode.Label + ")"));
            Line("capture format: " + (format == null ? "none" : Invariant($"{format.Width}x{format.Height} {format.Format}")));
            Line("audio input: " + (sound?.Input?.Name ?? "none") + " (" + SourceText(sound?.InputSource ?? AudioInputSource.None) + ")");
            Line("audio output: " + (sound?.Output?.Name ?? "none") + " (" + (sound == null || sound.OutputIsDefault ? UiText.DefaultOutput : "chosen") + ")");
            Line(Frames("windowed", windowed));
            Line(Delays("windowed", windowed, _windowedDisplayHz));
            Line("fullscreen screen: " + (screen == null ? "none" : Invariant($"{screen.Name} {screen.Bounds.Width}x{screen.Bounds.Height}")));
            Line(Frames("fullscreen", fullscreen));
            Line(Delays("fullscreen", fullscreen, _fullscreenDisplayHz));
            Line("delay check: " + (verdict.Pass ? "ok" : verdict.Reason));
            Line("picture brightness: " + Brightness());
            Line("no signal: " + (_form.NoSignal.NoSignal ? "yes" : "no"));
            Line(Invariant($"audio: bytes={audio.BytesCaptured} peak={audio.Peak:0.00} buffer={audio.BufferedMs:0.0} ms max={audio.MaxBufferedMs:0.0} ms trims={audio.Trims}"));
            Line("toolbar: " + (_toolbarState ?? "not measured"));
            Line("menu: " + string.Join(" | ", VideoMenu.TopLevelTexts(_form.VideoContextMenu)));
            Line("errors: " + Errors());
            return report.ToString();
        }

        static string Frames(string phase, PresenterStats s)
            => Invariant($"{phase}: frames arrived={s.Arrived} drawn={s.Drawn} dropped={s.Dropped} fps={s.MeasuredFps:0.0}");

        static string Delays(string phase, PresenterStats s, int displayHz)
            => Invariant($"{phase} delay: arrival→Present mean={s.ArrivalToPresent.MeanMs:0.00} p95={s.ArrivalToPresent.P95Ms:0.00} ms; Present→screen mean={s.PresentToScreen.MeanMs:0.00} p95={s.PresentToScreen.P95Ms:0.00} ms; refresh={s.RefreshHz:0.0} Hz ({(displayHz > 0 ? "display " + displayHz + " Hz" : "display unknown")}); overlay={s.Overlay} composed={s.Composed}");

        static string DeviceNames(IReadOnlyList<VideoDeviceInfo> devices)
        {
            if (devices == null || devices.Count == 0) return "none";
            var names = new string[devices.Count];
            for (int i = 0; i < names.Length; i++) names[i] = devices[i].Name;
            return string.Join(" | ", names);
        }

        static string PickText(VideoPickReason reason)
        {
            switch (reason)
            {
                case VideoPickReason.SavedPath: return "saved pick";
                case VideoPickReason.SavedName: return "saved pick, new port";
                case VideoPickReason.LooksLikeCard: return "looks like a capture card";
                default: return "none";
            }
        }

        static string SourceText(AudioInputSource source)
        {
            switch (source)
            {
                case AudioInputSource.SameDevice: return "same device";
                case AudioInputSource.NameMatch: return "name match";
                case AudioInputSource.Override: return "override";
                default: return "none";
            }
        }

        /// <summary>Raised on the render thread: post, never block.</summary>
        void OnLumaSampled(LumaStats stats) => _ui.Post(() =>
        {
            _lumaMeanSum += stats.Mean;
            _lumaSpreadSum += stats.StdDev;
            _lumaDarkSum += stats.DarkFraction;
            _lumaCount++;
        });

        string Brightness()
            => _lumaCount == 0 ? "none" : Invariant($"mean={_lumaMeanSum / _lumaCount:0.0} spread={_lumaSpreadSum / _lumaCount:0.0} dark={_lumaDarkSum / _lumaCount * 100:0}%");

        /// <summary>From any thread.</summary>
        void AddError(string error)
        {
            var text = (error ?? "").Replace("\r", " ").Replace("\n", " ");
            lock (_errorsLock)
            {
                if (_errors.Count < MaxErrors && !_errors.Contains(text)) _errors.Add(text);
            }
        }

        string Errors()
        {
            lock (_errorsLock) return _errors.Count == 0 ? "none" : string.Join(" | ", _errors);
        }

        /// <summary>Passes everything to the app's log, keeping each error (from any thread) for the report.</summary>
        sealed class ErrorRecorder : ILog
        {
            readonly ILog _log;
            readonly SelfTestRunner _runner;

            public ErrorRecorder(ILog log, SelfTestRunner runner)
            {
                _log = log;
                _runner = runner;
            }

            public void Info(string message) => _log.Info(message);

            public void Error(string message, Exception error = null)
            {
                _log.Error(message, error);
                _runner.AddError(error == null ? message : message + ": " + error.Message);
            }
        }
    }
}
