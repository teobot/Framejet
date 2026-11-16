using System.Collections.Generic;
using Framejet.Platform.Video;
using Xunit;

namespace Framejet.Platform.Tests.Video
{
    public class LatencyWaiterTests
    {
        const uint WaitTimeout = 0x102, WaitFailed = 0xFFFFFFFF;

        /// <summary>
        /// The frame-latency waitable object as measured on a flip-model swap chain: a semaphore that starts at 1 and gains
        /// one for every present that leaves the display queue, with no cap. Time is in ms. A wait takes a count already
        /// there at once; otherwise it returns when a release lands within it, or times out at its end.
        /// </summary>
        sealed class FakeQueue
        {
            public long Now;
            public int Count = 1, MaxCount = 1, Waits;
            public uint Result = 0; // what a wait that gets nothing returns, when not WaitTimeout
            readonly List<long> _releases = new List<long>();

            public void ReleaseAt(long time) { _releases.Add(time); _releases.Sort(); }

            public uint Wait(uint ms)
            {
                Waits++;
                long deadline = Now + ms;
                while (_releases.Count > 0 && _releases[0] <= deadline)
                {
                    if (Count > 0) break;
                    Now = System.Math.Max(Now, _releases[0]);
                    Release();
                }
                if (Count > 0) { Count--; return 0; }
                Now = deadline;
                return Result != 0 ? Result : WaitTimeout;
            }

            /// <summary>Applies the releases due by now.</summary>
            public void Advance(long now)
            {
                Now = now;
                while (_releases.Count > 0 && _releases[0] <= Now) Release();
            }

            void Release()
            {
                _releases.RemoveAt(0);
                Count++;
                if (Count > MaxCount) MaxCount = Count;
            }
        }

        FakeQueue _q = new FakeQueue();
        readonly LatencyWaiter _w;

        public LatencyWaiterTests() { _w = new LatencyWaiter(ms => _q.Wait(ms), () => _q.Now, stuckTicks: 1000); }

        QueueSlot At(long now, uint waitMs = 4) { _q.Advance(now); return _w.BeforePresent(waitMs); }

        [Fact]
        public void Takes_a_free_slot_and_reports_busy_when_the_wait_times_out()
        {
            Assert.Equal(QueueSlot.Taken, At(16));
            Assert.Equal(QueueSlot.Busy, At(32));
            Assert.Equal(36, _q.Now); // waited the whole 4 ms
        }

        [Fact]
        public void A_slot_released_during_the_wait_goes_to_that_frame()
        {
            Assert.Equal(QueueSlot.Taken, At(16));
            _q.ReleaseAt(34);                       // the frame before leaves the queue 2 ms into the next wait
            Assert.Equal(QueueSlot.Taken, At(32));
            Assert.Equal(34, _q.Now);               // the wait returned when the release landed
            Assert.Equal(QueueSlot.Busy, At(48));   // and took that count: there is no surplus
        }

        [Fact]
        public void A_failed_present_keeps_its_slot_and_the_next_frame_presents_without_waiting()
        {
            Assert.Equal(QueueSlot.Taken, At(16));
            _w.PresentFailed();
            int waits = _q.Waits;
            Assert.Equal(QueueSlot.Taken, At(32));
            Assert.Equal(waits, _q.Waits);
            Assert.Equal(QueueSlot.Busy, At(48));   // that slot was used once, not kept
        }

        [Fact]
        public void Only_WAIT_OBJECT_0_counts_as_a_slot()
        {
            _q.Count = 0; _q.Result = WaitFailed;
            Assert.Equal(QueueSlot.Busy, At(16));
        }

        [Fact]
        public void No_slot_for_a_second_of_back_to_back_timeouts_is_stuck()
        {
            _q.Count = 0; // a count lost: every wait times out
            long now = 0; int busy = 0; QueueSlot slot;
            while ((slot = At(now += 16)) == QueueSlot.Busy && busy < 200) busy++;
            Assert.Equal(QueueSlot.Stuck, slot);
            Assert.InRange(busy, 60, 64);          // about 1 s of 16 ms frames
            Assert.Equal(QueueSlot.Busy, At(now += 16)); // a new run starts after Stuck
        }

        [Fact]
        public void A_one_second_wait_that_times_out_is_stuck_at_once()
        {
            _q.Count = 0;
            Assert.Equal(QueueSlot.Stuck, At(16, waitMs: 1000));
        }

        [Fact]
        public void A_gap_with_no_frames_starts_a_new_run()
        {
            _q.Count = 0;
            long now = 0;
            for (int i = 0; i < 40; i++) Assert.Equal(QueueSlot.Busy, At(now += 16)); // 0.64 s
            now += 2000;                                                                // paused: no frames reach the wait
            for (int i = 0; i < 40; i++) Assert.Equal(QueueSlot.Busy, At(now += 16)); // not stuck: the run began again
        }

        [Fact]
        public void Reset_forgets_a_held_slot_with_the_old_swap_chain()
        {
            Assert.Equal(QueueSlot.Taken, At(16));
            _w.PresentFailed();
            _w.Reset(); _q = new FakeQueue { Now = 32 }; // a new swap chain with its own object
            Assert.Equal(QueueSlot.Taken, At(32));
            Assert.Equal(1, _q.Waits);                  // it waited for its slot
            Assert.Equal(QueueSlot.Busy, At(48));
        }

        // A 60.00 fps source on a 59.94 Hz display, which made the old presenter queue two deep. Each presented frame leaves
        // the queue at the first refresh at least 3.5 ms after its present. Every present holds a slot, so it stays one deep.
        [Theory]
        [InlineData(4u)]
        [InlineData(1000u)]
        public void Stays_one_deep_with_a_60_fps_source_on_a_59_94_Hz_display(uint waitMs)
        {
            const double frame = 1000 / 60.0, refresh = 1000 / 59.94, latch = 3.5;
            var q = new FakeQueue();
            var w = new LatencyWaiter(ms => q.Wait(ms), () => q.Now, stuckTicks: 1000 * 1000);
            int presented = 0, frames = 0;
            for (double arrival = 0; arrival < 60000; arrival += frame)
            {
                // Time is in µs here, for the 0.06 ms beat between the two rates.
                q.Advance(System.Math.Max(q.Now, (long)(arrival * 1000)));
                frames++;
                if (w.BeforePresent(waitMs * 1000) != QueueSlot.Taken) continue;
                presented++;
                double shown = System.Math.Ceiling((q.Now / 1000.0 + latch) / refresh) * refresh;
                q.ReleaseAt((long)(shown * 1000));
            }
            Assert.Equal(1, q.MaxCount);
            Assert.True(presented >= frames * 0.95, $"presented {presented} of {frames}");
        }
    }
}
