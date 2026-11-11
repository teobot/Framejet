using System;

namespace Framejet.Core.Timing
{
    public sealed class Debouncer
    {
        readonly IScheduler _scheduler;
        readonly TimeSpan _delay;
        readonly Action _action;
        IDisposable _pending;

        public Debouncer(IScheduler scheduler, TimeSpan delay, Action action)
        {
            _scheduler = scheduler;
            _delay = delay;
            _action = action;
        }

        public void Trigger()
        {
            _pending?.Dispose();
            _pending = _scheduler.After(_delay, _action);
        }
    }
}
