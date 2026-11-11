using System;
using System.Windows.Forms;
using DirectShowLib;
using Framejet.Core;
using Framejet.Core.Video;

namespace Framejet.Platform.Video
{
    /// <summary>
    /// A message-only window, created on the UI thread, that a capture graph posts its event notifications to. It
    /// drains the graph's events, frees each one, and raises <see cref="DeviceTaken"/> once if another app took the
    /// device, otherwise <see cref="DeviceLost"/> once if any of them means the device is gone.
    /// </summary>
    internal sealed class GraphEventWindow : NativeWindow, IDisposable
    {
        /// <summary>WM_APP + 1: the message the graph posts for each event it queues.</summary>
        public const int NotifyMessage = 0x8000 + 1;
        static readonly IntPtr HwndMessage = new IntPtr(-3);

        readonly ILog _log;
        IMediaEventEx _events;
        bool _raised;

        public GraphEventWindow(IMediaEventEx events, ILog log)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            CreateHandle(new CreateParams { Parent = HwndMessage });
        }

        /// <summary>Raised on the UI thread, at most once, and never together with <see cref="DeviceTaken"/>. A handler may stop the graph and dispose this window.</summary>
        public event Action DeviceLost;

        /// <summary>Raised on the UI thread, at most once, instead of <see cref="DeviceLost"/>, when another app took the device.</summary>
        public event Action DeviceTaken;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg != NotifyMessage)
            {
                base.WndProc(ref m);
                return;
            }
            try
            {
                var kind = DrainEvents(out string reason);
                if (kind != GraphEventKind.None && !_raised)
                {
                    _raised = true;
                    if (kind == GraphEventKind.TakenByAnotherApp)
                    {
                        _log.Info("Capture graph " + reason + ": another app took the video device");
                        DeviceTaken?.Invoke(); // last: the handler may stop the graph and destroy this window
                    }
                    else
                    {
                        _log.Info("Capture graph " + reason + ": the video device is lost");
                        DeviceLost?.Invoke(); // last: the handler may stop the graph and destroy this window
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Handling a capture graph event failed", ex);
            }
        }

        /// <summary>
        /// Takes every queued event. Returns TakenByAnotherApp if any event says so, else DeviceLost if any means the
        /// device is lost, else None; <paramref name="reason"/> describes the first event of the returned kind.
        /// </summary>
        GraphEventKind DrainEvents(out string reason)
        {
            var events = _events;
            var result = GraphEventKind.None;
            reason = null;
            while (events != null && events.GetEvent(out EventCode code, out IntPtr param1, out IntPtr param2, 0) == 0)
            {
                var kind = GraphEventPolicy.Classify((int)code, param1.ToInt64(), param2.ToInt64());
                if (kind > result) // TakenByAnotherApp outranks DeviceLost
                {
                    result = kind;
                    reason = $"event 0x{(int)code:X2} (0x{unchecked((int)param1.ToInt64()):X8})";
                }
                events.FreeEventParams(code, param1, param2);
            }
            return result;
        }

        /// <summary>
        /// Frees any events still queued, without raising anything, then destroys the window. Call it before the
        /// graph is released. Safe to call from a <see cref="DeviceLost"/> handler.
        /// </summary>
        public void Dispose()
        {
            try
            {
                DrainEvents(out _);
            }
            finally
            {
                _events = null;
                DestroyHandle();
            }
        }
    }
}
