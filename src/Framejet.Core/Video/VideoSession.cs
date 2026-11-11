using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Framejet.Core.Devices;
using Framejet.Core.Settings;
using Framejet.Core.Timing;
using Framejet.Core.Ui;

namespace Framejet.Core.Video
{
    public enum VideoState { NoCard, Waiting, Busy, Running }

    /// <summary>Decides which capture device and mode to run, and rides out unplugs, busy cards and lost devices.</summary>
    public sealed class VideoSession
    {
        public static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(3);
        /// <summary>A device lost this soon after a successful start counts as a failed start.</summary>
        public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Running, but no new frame for this long after frames had been arriving: the graph has stalled without an
        /// error (resume from sleep, a card hiccup), so it is restarted as if the device were lost. The Ripsaw streams
        /// about 60 fps even with no HDMI signal, so this much silence is unambiguous.
        /// </summary>
        public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3);
        static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(1);

        static readonly VideoDeviceInfo[] NoDevices = new VideoDeviceInfo[0];
        static readonly VideoMode[] NoModes = new VideoMode[0];

        readonly IVideoBackend _backend;
        readonly AppSettings _settings;
        readonly IScheduler _scheduler;
        readonly Func<Size> _screenSize;
        readonly Action _settingsChanged;
        readonly ILog _log;

        IDisposable _retry;
        VideoMode _lastWorking;
        bool _yielded;
        TimeSpan _lastStart;

        // The stall watchdog, armed by every successful start.
        IDisposable _stallCheck;
        long _framesAtCheck;
        bool _framesSeen;
        TimeSpan _lastNewFrames;

        public VideoSession(IVideoBackend backend, AppSettings settings, IScheduler scheduler, Func<Size> screenSize, Action settingsChanged, ILog log)
        {
            _backend = backend;
            _settings = settings;
            _scheduler = scheduler;
            _screenSize = screenSize;
            _settingsChanged = settingsChanged;
            _log = log ?? NullLog.Instance;
            Devices = NoDevices;
            Modes = NoModes;
            _backend.DeviceLost += OnDeviceLost;
            _backend.DeviceTaken += OnDeviceTaken;
        }

        public VideoState State { get; private set; } = VideoState.NoCard;
        public VideoDeviceInfo Device { get; private set; }
        public VideoPickReason PickReason { get; private set; }
        public VideoMode Mode { get; private set; }
        public IReadOnlyList<VideoMode> Modes { get; private set; }
        public IReadOnlyList<VideoDeviceInfo> Devices { get; private set; }
        public string Warning { get; private set; }

        public string Message
        {
            get
            {
                switch (State)
                {
                    case VideoState.NoCard: return UiText.NoCard;
                    case VideoState.Waiting: return UiText.Waiting(WaitingName);
                    case VideoState.Busy:
                        var name = Device != null ? Device.Name : _settings.VideoDeviceName;
                        return _yielded ? UiText.Taken(name) : UiText.Busy(name);
                    default: return null;
                }
            }
        }

        string WaitingName => !string.IsNullOrEmpty(_settings.VideoDeviceName) ? _settings.VideoDeviceName : (Device != null ? Device.Name : "");

        public event Action Changed;

        public void Refresh()
        {
            Devices = _backend.ListDevices();

            if (_yielded)
            {
                if (Device != null && Devices.Any(d => d.Path == Device.Path)) return; // another app has it: only Rescan or a pick takes it back
                _yielded = false;
            }

            if (State == VideoState.Running && Device != null && Devices.Any(d => d.Path == Device.Path)) return;

            if (State == VideoState.Running)
            {
                _log.Info("Video device gone: " + Device?.Name);
                _backend.Stop();
                Warning = null; // it was about the device that has gone
            }

            var pick = DeviceRules.PickVideo(Devices, _settings.VideoDevicePath, _settings.VideoDeviceName);
            if (pick.Device == null)
            {
                CancelRetry();
                Device = null;
                PickReason = VideoPickReason.None;
                Mode = null;
                Modes = NoModes;
                Warning = null;
                State = string.IsNullOrEmpty(_settings.VideoDeviceName) ? VideoState.NoCard : VideoState.Waiting;
                RaiseChanged();
                return;
            }

            PickReason = pick.Reason;
            Open(pick.Device);
        }

        /// <summary>Explicit intent to use the card: takes it back from another app, then does a normal refresh.</summary>
        public void Rescan()
        {
            _yielded = false;
            Refresh();
        }

        public void SelectDevice(VideoDeviceInfo device)
        {
            if (device == null) return;
            _yielded = false;
            _settings.VideoDevicePath = device.Path;
            _settings.VideoDeviceName = device.Name;
            _settingsChanged();
            CancelRetry();
            _backend.Stop();
            PickReason = VideoPickReason.SavedPath;
            Open(device); // a successful start clears the warning; a failed one leaves it
        }

        public void SelectMode(VideoMode mode)
        {
            if (mode == null || Device == null) return;
            _yielded = false;
            CancelRetry();
            _backend.Stop();
            try
            {
                _backend.Start(Device, mode);
            }
            catch (Exception ex)
            {
                _log.Error("Couldn't start " + mode.Key, ex);
                if (_lastWorking != null && !_lastWorking.Equals(mode))
                {
                    var back = _lastWorking;
                    Warning = UiText.ModeReverted(mode, back);
                    try
                    {
                        _backend.Start(Device, back);
                        // Not Succeeded(): the warning must stay until the next change (a later start or a device loss).
                        Mode = back;
                        State = VideoState.Running;
                        _lastStart = _scheduler.Now;
                        WatchForStall();
                        RaiseChanged();
                        return;
                    }
                    catch (Exception ex2)
                    {
                        _log.Error("Couldn't go back to " + back.Key, ex2);
                    }
                }
                GoBusy();
                return;
            }
            Mode = mode;
            _settings.ModeKey = mode.Key;
            Succeeded();
        }

        public void Stop()
        {
            CancelRetry();
            CancelStallCheck();
            _backend.Stop();
            Device = null;
            Mode = null;
            State = VideoState.NoCard;
            RaiseChanged();
        }

        void OnDeviceTaken()
        {
            _log.Info("Video device taken by another app: " + Device?.Name);
            CancelRetry();
            _backend.Stop();
            _yielded = true;
            State = VideoState.Busy;
            RaiseChanged();
        }

        void OnDeviceLost()
        {
            _log.Info("Video device lost");
            Lost();
        }

        /// <summary>The running device stopped working: reopen it, or back off when it had only just started.</summary>
        void Lost()
        {
            CancelRetry();
            CancelStallCheck();
            _backend.Stop();
            Warning = null;
            if (_scheduler.Now - _lastStart < StartupGrace)
            {
                GoBusy(); // died right after starting: a failed start, so back off instead of rebuilding in a loop
                return;
            }
            State = VideoState.Waiting;
            Refresh();
        }

        /// <summary>Arms the watchdog afresh for a new start: it never fires before that start's first frame.</summary>
        void WatchForStall()
        {
            CancelStallCheck();
            _framesAtCheck = _backend.FramesArrived;
            _framesSeen = false;
            _lastNewFrames = _scheduler.Now;
            _stallCheck = _scheduler.After(StallCheckInterval, CheckForStall);
        }

        void CheckForStall()
        {
            _stallCheck = null;
            if (State != VideoState.Running) return; // yielded, waiting, busy or stopped: the next start arms it again
            long frames = _backend.FramesArrived;
            var now = _scheduler.Now;
            if (frames != _framesAtCheck)
            {
                _framesAtCheck = frames;
                _framesSeen = true;
                _lastNewFrames = now;
            }
            else if (_framesSeen && now - _lastNewFrames >= StallTimeout)
            {
                _log.Info("Video stalled: no frames for 3 s — restarting");
                Lost();
                return;
            }
            _stallCheck = _scheduler.After(StallCheckInterval, CheckForStall);
        }

        void CancelStallCheck()
        {
            _stallCheck?.Dispose();
            _stallCheck = null;
        }

        void Open(VideoDeviceInfo device)
        {
            CancelRetry();
            _lastWorking = null;
            Device = device;
            try
            {
                Modes = ModeCatalog.Build(_backend.ListModes(device));
                var size = _screenSize();
                Mode = ModeCatalog.Resolve(Modes, _settings.ModeKey, size.Width, size.Height);
                if (Mode == null) throw new VideoStartException("No usable modes");
                _backend.Start(device, Mode);
            }
            catch (Exception ex)
            {
                _log.Error("Couldn't open " + device.Name, ex);
                GoBusy();
                return;
            }
            // The device is remembered; the mode is not: an automatic choice (the screen's default, or the fallback while the
            // saved pick is unavailable) must not replace the user's pick. Only SelectMode saves a mode.
            _settings.VideoDevicePath = device.Path;
            _settings.VideoDeviceName = device.Name;
            Succeeded();
        }

        void Succeeded()
        {
            State = VideoState.Running;
            _lastStart = _scheduler.Now;
            _lastWorking = Mode;
            Warning = null; // a mode-revert warning lasts until the next start
            CancelRetry();
            WatchForStall();
            _settingsChanged();
            RaiseChanged();
        }

        void GoBusy()
        {
            State = VideoState.Busy;
            CancelRetry();
            _retry = _scheduler.After(BusyRetry, Refresh);
            RaiseChanged();
        }

        void CancelRetry()
        {
            _retry?.Dispose();
            _retry = null;
        }

        void RaiseChanged() => Changed?.Invoke();
    }
}
