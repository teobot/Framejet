using System;

namespace Framejet.Platform.Video
{
    /// <summary>
    /// Waits for the swap chain's frame-latency waitable object before each present, and keeps its count in step with the
    /// presents. The object is a semaphore that gains one for every present that leaves the display queue, with no cap
    /// (measured). So a present that took no count leaves a surplus, and later waits return while a frame is still queued.
    /// A count taken by a frame that never presented is never given back, and every later wait times out. The debt tracks both.
    /// Render thread only.
    /// </summary>
    internal sealed class LatencyWaiter
    {
        public const uint Signalled = 0;     // WAIT_OBJECT_0
        public const uint TimedOut = 0x102; // WAIT_TIMEOUT

        readonly Func<uint, uint> _wait;
        readonly Func<bool> _queueEmpty;
        readonly long _guardTicks;
        int _debt;           // > 0: presents that took no count; < 0: a count taken by a frame that did not present
        long _timeoutsSince; // when the current run of skipping timeouts began; 0 when there is none

        /// <param name="wait">Waits on the object for up to the given ms; returns <see cref="Signalled"/> or <see cref="TimedOut"/>.</param>
        /// <param name="queueEmpty">True when the last frame presented is known to have reached the screen.</param>
        /// <param name="guardTicks">How long the skipping timeouts may run back to back before a frame is presented anyway.</param>
        public LatencyWaiter(Func<uint, uint> wait, Func<bool> queueEmpty, long guardTicks)
        {
            _wait = wait;
            _queueEmpty = queueEmpty;
            _guardTicks = guardTicks;
        }

        /// <summary>
        /// Waits before one present. False: skip the frame. True: present it, then call <see cref="Presented"/> or
        /// <see cref="PresentFailed"/> with <paramref name="took"/>, which says whether the frame holds a count.
        /// </summary>
        public bool BeforePresent(uint waitMs, bool skipOnTimeout, long now, out bool took)
        {
            if (_debt < 0)
            {
                _debt++; // the count a frame took before its present failed
                took = true;
            }
            else
            {
                while (_debt > 0 && _wait(0) == Signalled) _debt--; // the surplus left by presents that took no count
                took = _wait(waitMs) != TimedOut;
            }
            if (!took && skipOnTimeout)
            {
                if (_timeoutsSince == 0) _timeoutsSince = now;
                if (now - _timeoutsSince < _guardTicks) return false;
                // A second of back-to-back timeouts. If the last frame presented is on screen, the queue is empty and the
                // count has fallen short through an error not foreseen here: this frame takes the lost count (or the one
                // that has just come). Otherwise the display has stalled: present anyway, as a present that took no count.
                if (_queueEmpty())
                {
                    _wait(0);
                    took = true;
                }
            }
            _timeoutsSince = 0;
            return true;
        }

        /// <summary>The present succeeded.</summary>
        public void Presented(bool took)
        {
            if (!took) _debt++;
        }

        /// <summary>The present failed, so nothing was queued and a count the frame took is still free.</summary>
        public void PresentFailed(bool took)
        {
            if (took) _debt--;
        }

        /// <summary>Forgets the debt and the timeouts with the swap chain they belong to.</summary>
        public void Reset()
        {
            _debt = 0;
            _timeoutsSince = 0;
        }
    }
}
