using System;
using System.Collections.Generic;
using Framejet.Core.Timing;

namespace Framejet.Core.Tests.Fakes
{
    public sealed class FakeScheduler : IScheduler
    {
        sealed class Entry
        {
            public TimeSpan Due;
            public long Seq;
            public Action Action;
            public bool Cancelled;
        }

        readonly List<Entry> _pending = new List<Entry>();
        long _seq;

        public TimeSpan Now { get; private set; } = TimeSpan.Zero;

        public IDisposable After(TimeSpan delay, Action action)
        {
            var e = new Entry { Due = Now + delay, Seq = _seq++, Action = action };
            _pending.Add(e);
            return new Cancel(e);
        }

        public void Advance(TimeSpan by)
        {
            var target = Now + by;
            while (true)
            {
                Entry next = null;
                foreach (var e in _pending)
                {
                    if (e.Cancelled || e.Due > target) continue;
                    if (next == null || e.Due < next.Due || (e.Due == next.Due && e.Seq < next.Seq)) next = e;
                }
                if (next == null) break;
                _pending.Remove(next);
                if (next.Due > Now) Now = next.Due;
                next.Action();
            }
            Now = target;
            _pending.RemoveAll(e => e.Cancelled);
        }

        sealed class Cancel : IDisposable
        {
            readonly Entry _e;
            public Cancel(Entry e) { _e = e; }
            public void Dispose() { _e.Cancelled = true; }
        }
    }
}
