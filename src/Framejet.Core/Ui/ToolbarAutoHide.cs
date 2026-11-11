using System;

namespace Framejet.Core.Ui
{
    public sealed class ToolbarAutoHide
    {
        public static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan WarningShow = TimeSpan.FromSeconds(5);

        TimeSpan _lastActivity;
        TimeSpan _warningUntil = TimeSpan.MinValue;
        bool _held;
        bool _fullscreen;

        public ToolbarAutoHide(TimeSpan now)
        {
            _lastActivity = now;
            Recompute(now);
        }

        public bool Visible { get; private set; }

        public void MouseMoved(TimeSpan now) { _lastActivity = now; Recompute(now); }

        public void SetHeld(bool held, TimeSpan now)
        {
            _held = held;
            if (!held) _lastActivity = now;
            Recompute(now);
        }

        public void WarningRaised(TimeSpan now) { _warningUntil = now + WarningShow; Recompute(now); }

        public void SetFullscreen(bool fullscreen) { _fullscreen = fullscreen; Recompute(_lastNow); }

        public void Tick(TimeSpan now) => Recompute(now);

        TimeSpan _lastNow;

        void Recompute(TimeSpan now)
        {
            _lastNow = now;
            Visible = !_fullscreen && (_held || now < _lastActivity + HideDelay || now < _warningUntil);
        }
    }
}
