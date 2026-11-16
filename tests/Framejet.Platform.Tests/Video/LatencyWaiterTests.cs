using Framejet.Platform.Video;
using Xunit;

namespace Framejet.Platform.Tests.Video
{
    public class LatencyWaiterTests
    {
        /// <summary>
        /// The frame-latency waitable object as measured on a flip-model swap chain: a semaphore that starts at 1 and gains
        /// one for every present that leaves the display queue, with no cap. Waits never block here; time is the caller's.
        /// </summary>
        sealed class FakeQueue
        {
            public int Count = 1, Queued;
            public uint Wait(uint ms) { if (Count == 0) return LatencyWaiter.TimedOut; Count--; return LatencyWaiter.Signalled; }
            public void Retire() { Count += Queued; Queued = 0; }
        }

        FakeQueue _q = new FakeQueue();
        readonly LatencyWaiter _w;

        public LatencyWaiterTests() { _w = new LatencyWaiter(ms => _q.Wait(ms), () => _q.Queued == 0, guardTicks: 1000); }

        /// <summary>One frame through the waiter, at time <paramref name="now"/> (1 tick = 1 ms). True when it was presented.</summary>
        bool Frame(uint waitMs, bool skipOnTimeout, long now, bool presentFails = false)
        {
            if (!_w.BeforePresent(waitMs, skipOnTimeout, now, out bool took)) return false;
            if (presentFails) { _w.PresentFailed(took); return false; }
            _q.Queued++;
            _w.Presented(took);
            return true;
        }

        int SkipsUntilPresented(ref long now)
        {
            int skipped = 0;
            while (skipped < 200 && !Frame(4, true, now += 16)) skipped++;
            return skipped;
        }

        [Fact]
        public void Default_mode_presents_when_the_queue_is_free_and_skips_while_it_is_busy()
        {
            Assert.True(Frame(4, true, 16));
            Assert.False(Frame(4, true, 32));
            _q.Retire();
            Assert.True(Frame(4, true, 48));
            _q.Retire(); Assert.Equal(1, _q.Count);
        }

        [Fact]
        public void A_present_after_a_long_timeout_leaves_no_surplus()
        {
            Assert.True(Frame(1000, false, 16));   // never skip: A takes the count
            Assert.True(Frame(1000, false, 1032)); // the display stalled past 1 s: B is presented without a count
            _q.Retire();                           // A and B leave the queue: the object now holds 2
            Assert.True(Frame(4, true, 1048));     // C
            Assert.False(Frame(4, true, 1064));    // C is still queued, so the default mode's skip still works
            _q.Retire(); Assert.Equal(1, _q.Count);
        }

        [Fact]
        public void A_failed_present_hands_its_count_to_the_next_frame()
        {
            Assert.False(Frame(4, true, 16, presentFails: true)); // took the count, then Present failed: nothing queued
            Assert.True(Frame(4, true, 32));                       // uses that count without waiting
            _q.Retire(); Assert.Equal(1, _q.Count);
            Assert.True(Frame(4, true, 48));
        }

        [Fact]
        public void After_a_second_of_timeouts_with_the_queue_empty_it_takes_the_lost_count()
        {
            _q.Count = 0; // a count lost to an error the waiter can't see: every wait times out
            long now = 0;
            Assert.InRange(SkipsUntilPresented(ref now), 60, 64); // about 1 s of 16 ms frames
            _q.Retire(); Assert.Equal(1, _q.Count);               // balanced again, with no surplus
            Assert.True(Frame(4, true, now += 16));
            Assert.False(Frame(4, true, now += 16));
        }

        [Fact]
        public void After_a_second_of_timeouts_with_the_display_stalled_it_presents_and_keeps_the_count_balanced()
        {
            long now = 16;
            Assert.True(Frame(4, true, now));                     // A is queued, then the display stalls
            Assert.InRange(SkipsUntilPresented(ref now), 60, 64); // G is presented without a count after about 1 s
            _q.Retire();                                          // the display is back: A and G leave, the object holds 2
            Assert.True(Frame(4, true, now += 16));
            Assert.False(Frame(4, true, now += 16));              // no surplus: a busy queue still skips
            _q.Retire(); Assert.Equal(1, _q.Count);
        }

        [Fact]
        public void Reset_forgets_the_debt_with_the_old_swap_chain()
        {
            Assert.False(Frame(4, true, 16, presentFails: true)); // holds a count of the old swap chain
            _w.Reset(); _q = new FakeQueue();                      // a new swap chain with its own object
            Assert.True(Frame(4, true, 32));
            Assert.False(Frame(4, true, 48));
        }
    }
}
