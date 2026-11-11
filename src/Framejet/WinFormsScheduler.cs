using System;
using System.Diagnostics;
using System.Windows.Forms;
using Framejet.Core.Timing;

namespace Framejet
{
    /// <summary>
    /// Runs delayed actions on the UI thread with one-shot WinForms timers. Create it, and call <see cref="After"/>,
    /// on the UI thread.
    /// </summary>
    sealed class WinFormsScheduler : IScheduler
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();

        public TimeSpan Now => _clock.Elapsed;

        public IDisposable After(TimeSpan delay, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var interval = (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(delay.TotalMilliseconds)));
            return new Pending(interval, action);
        }

        /// <summary>
        /// One scheduled action. Disposing it is safe at any time: before it fires (it never runs), after it fired,
        /// and from inside the action itself.
        /// </summary>
        sealed class Pending : IDisposable
        {
            Timer _timer;
            Action _action;

            public Pending(int interval, Action action)
            {
                _action = action;
                _timer = new Timer { Interval = interval };
                _timer.Tick += OnTick;
                _timer.Start();
            }

            void OnTick(object sender, EventArgs e)
            {
                var action = _action;
                Dispose(); // one shot: done before the action runs, so the action may schedule again or dispose this
                action?.Invoke();
            }

            public void Dispose()
            {
                var timer = _timer;
                if (timer == null) return;
                _timer = null;
                _action = null;
                timer.Stop();
                timer.Tick -= OnTick;
                timer.Dispose();
            }
        }
    }
}
