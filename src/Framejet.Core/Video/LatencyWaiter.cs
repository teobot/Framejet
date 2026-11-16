using System;

namespace Framejet.Core.Video
{
    /// <summary>What a frame got from the display queue before its present.</summary>
    public enum QueueSlot
    {
        /// <summary>The frame holds a slot: present it.</summary>
        Taken,
        /// <summary>The wait timed out: skip the frame.</summary>
        Busy,
        /// <summary>No slot has come for the stuck time while frames kept arriving: skip the frame and recreate the swap chain.</summary>
        Stuck,
    }

    /// <summary>
    /// Takes a slot from the swap chain's frame-latency waitable object before each present. The object is a semaphore that
    /// gains one for every present that leaves the display queue, with no cap (measured), so a present without a slot would
    /// leave a surplus that lifts the latency limit for good. A frame is therefore never presented without one, and a slot
    /// taken by a frame whose Present failed goes to the next frame. Render thread only.
    /// </summary>
    public sealed class LatencyWaiter
    {
        public const uint Signalled = 0; // WAIT_OBJECT_0; any other result is no slot

        readonly Func<uint, uint> _wait;
        readonly Func<long> _clock;
        readonly long _stuckTicks;
        bool _held;                    // a slot taken by a frame whose Present failed
        long _busySince, _lastBusyEnd; // the current run of timed-out waits: when it began and when its last wait ended; 0 when none

        /// <param name="wait">Waits on the object for up to the given ms and returns the WaitForSingleObject result.</param>
        /// <param name="clock">The time, in the ticks <paramref name="stuckTicks"/> is in.</param>
        /// <param name="stuckTicks">How long no slot may come while frames keep arriving before the queue counts as stuck.</param>
        public LatencyWaiter(Func<uint, uint> wait, Func<long> clock, long stuckTicks)
        {
            _wait = wait;
            _clock = clock;
            _stuckTicks = stuckTicks;
        }

        /// <summary>Waits up to <paramref name="waitMs"/> for a slot. After a failed Present, the frame takes its slot without waiting.</summary>
        public QueueSlot BeforePresent(uint waitMs)
        {
            if (_held)
            {
                _held = false;
                return QueueSlot.Taken;
            }
            long start = _clock();
            if (_wait(waitMs) == Signalled)
            {
                _busySince = 0;
                return QueueSlot.Taken;
            }
            long end = _clock();
            // Timeouts count back to back while frames keep arriving. A gap with no frames waiting (paused, stopped, no signal)
            // starts a new run.
            if (_busySince == 0 || start - _lastBusyEnd > _stuckTicks / 4) _busySince = start;
            _lastBusyEnd = end;
            if (end - _busySince < _stuckTicks) return QueueSlot.Busy;
            _busySince = 0;
            return QueueSlot.Stuck;
        }

        /// <summary>The Present failed, so nothing was queued and the slot the frame took goes to the next frame.</summary>
        public void PresentFailed() => _held = true;

        /// <summary>Forgets a held slot and the timeouts with the swap chain they belong to.</summary>
        public void Reset()
        {
            _held = false;
            _busySince = 0;
        }
    }
}
