using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Framejet.Core;
using Framejet.Core.Video;
using Framejet.Platform.Tests.TestSupport;
using Framejet.Platform.Video;
using Xunit;

namespace Framejet.Platform.Tests.Video
{
    public class D3D11PresenterTests
    {
        static void WithPresenter(Action<D3D11Presenter, LatestFrameSlot, CaptureFormat> body) => WithPresenter((p, slot, f, _) => body(p, slot, f));
        static void WithPresenter(Action<D3D11Presenter, LatestFrameSlot, CaptureFormat, Form> body) => Sta.Run(() =>
        {
            using var form = new Form { Width = 1280, Height = 720, StartPosition = FormStartPosition.CenterScreen }; form.Show();
            var f = new CaptureFormat(1280, 720, GrabberFormat.Nv12, false); var slot = new LatestFrameSlot(f.FrameBytes);
            using var p = new D3D11Presenter(form.Handle, NullLog.Instance); p.Start(f, slot); body(p, slot, f, form); p.Stop();
        });
        static Thread Feed(LatestFrameSlot slot, CaptureFormat f, int frames, int[] fed = null) { var t = new Thread(() => { var d = SyntheticFrames.Solid(f, 120, 128, 128); var h = GCHandle.Alloc(d, GCHandleType.Pinned);
            for (int i = 0; i < frames; i++) { slot.Publish(h.AddrOfPinnedObject(), d.Length, Stopwatch.GetTimestamp()); if (fed != null) Interlocked.Increment(ref fed[0]); Thread.Sleep(16); } h.Free(); }) { IsBackground = true }; t.Start(); return t; }

        [SkippableFact, Trait("Category", "Hardware")] public void Presents_synthetic_frames_on_a_window()
            => WithPresenter((p, slot, f) => { var fed = new int[1]; var feeder = Feed(slot, f, 120, fed); Sta.Pump(TimeSpan.FromSeconds(2.5));
                var wait = Stopwatch.StartNew(); while (feeder.IsAlive && wait.Elapsed < TimeSpan.FromSeconds(5)) Sta.Pump(TimeSpan.FromMilliseconds(50));
                Sta.Pump(TimeSpan.FromSeconds(0.3));
                int sent = Volatile.Read(ref fed[0]); var s = p.GetStats(); string msg = $"drawn {s.Drawn}, dropped {s.Dropped} of {sent} fed";
                // The bar is relative to what the Sleep(16) feeder managed. Every fed frame must be drawn, or counted as dropped (overwritten in the newest-frame slot or skipped);
                // the newest frame may still sit in the slot, hence the - 1. Dropped itself must stay small: the presenter keeps up.
                Assert.True(sent >= 30, $"feeder only managed {sent} frames"); Assert.True(s.Drawn + s.Dropped >= sent - 1, msg); Assert.True(s.Dropped <= 5, msg); });
        [SkippableFact, Trait("Category", "Hardware")] public void Recovers_after_device_loss()
            => WithPresenter((p, slot, f) => { Feed(slot, f, 200); Sta.Pump(TimeSpan.FromSeconds(1)); long before = p.GetStats().Drawn;
                p.SimulateDeviceLost(); Sta.Pump(TimeSpan.FromSeconds(1.5)); var s = p.GetStats(); Assert.Equal(1, s.DeviceResets); Assert.True(s.Drawn - before >= 30); });
        [SkippableFact, Trait("Category", "Hardware")] public void VSync_off_presents_with_the_tearing_flag_accepted()
            => WithPresenter((p, slot, f) => { Assert.True(p.TearingSupported); p.VSync = false; Feed(slot, f, 60); Sta.Pump(TimeSpan.FromSeconds(1.5)); Assert.True(p.GetStats().Drawn >= 50); });

        [Theory]
        [InlineData(true, false, true, 1, 0, 4, true)]
        [InlineData(true, false, false, 1, 0, 4, true)]
        [InlineData(true, true, true, 1, 0, 1000, true)]
        [InlineData(true, true, false, 1, 0, 1000, true)]
        [InlineData(false, false, true, 0, 0x200, 1000, true)]
        [InlineData(false, true, true, 0, 0x200, 1000, true)]
        [InlineData(false, false, false, 0, 0, 1000, true)]
        public void Plans_the_present(bool vsync, bool neverSkip, bool tearing, int interval, int flags, int waitMs, bool skip)
        {
            var plan = D3D11Presenter.Plan(vsync, neverSkip, tearing);
            Assert.Equal(interval, plan.SyncInterval); Assert.Equal(flags, (int)plan.Flags);
            Assert.Equal(waitMs, (int)plan.WaitMs); Assert.Equal(skip, plan.SkipOnTimeout);
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Newest_wins_keeps_up_and_survives_toggles_and_device_loss()
            => WithPresenter((p, slot, f) => { p.MeasureLatency = true; var feeder = Feed(slot, f, 200); Sta.Pump(TimeSpan.FromSeconds(1));
                p.NeverSkipFrames = true; Sta.Pump(TimeSpan.FromSeconds(0.5)); p.NeverSkipFrames = false; Sta.Pump(TimeSpan.FromSeconds(0.5));
                p.SimulateDeviceLost(); Sta.Pump(TimeSpan.FromSeconds(1));
                // Sleep(16) can run at 31 ms on a coarse timer, so let the feeder finish its 200 frames before counting.
                var wait = Stopwatch.StartNew(); while (feeder.IsAlive && wait.Elapsed < TimeSpan.FromSeconds(6)) Sta.Pump(TimeSpan.FromMilliseconds(50));
                Sta.Pump(TimeSpan.FromSeconds(0.3)); var s = p.GetStats();
                Assert.Equal(1, s.DeviceResets); Assert.True(s.Drawn >= 120, $"drawn {s.Drawn}"); Assert.True(s.PresentToScreen.Count > 0, "no Present to screen samples");
                Assert.True(s.QueueWait.Count > 0, "no queue wait samples"); });

        // A Present that fails after its wait (not a device loss) queues nothing. The slot it took must go to the next frame,
        // or every later 4 ms wait in the default mode times out until the swap chain is recreated.
        [SkippableFact, Trait("Category", "Hardware")] public void A_failed_present_does_not_stall_the_default_mode()
            => WithPresenter((p, slot, f) => { var feeder = Feed(slot, f, 150); Sta.Pump(TimeSpan.FromSeconds(0.5)); p.SimulatePresentFailure();
                var wait = Stopwatch.StartNew(); while (feeder.IsAlive && wait.Elapsed < TimeSpan.FromSeconds(8)) Sta.Pump(TimeSpan.FromMilliseconds(50));
                Sta.Pump(TimeSpan.FromSeconds(0.3)); var s = p.GetStats(); string msg = $"drawn {s.Drawn}, dropped {s.Dropped}, recreated {p.SwapChainRecreates}";
                Assert.True(s.Drawn >= 100, msg); Assert.True(s.Dropped <= 6, msg); Assert.Equal(0, p.SwapChainRecreates); });

        // A slot lost to an error not foreseen makes every 4 ms wait time out. After 1 s with no slot, the presenter recreates
        // the swap chain, which resets the waitable object, and drawing resumes. That is not a device loss.
        [SkippableFact, Trait("Category", "Hardware")] public void Recreates_the_swap_chain_when_no_slot_comes_for_a_second()
            => WithPresenter((p, slot, f) => { var feeder = Feed(slot, f, 150); Sta.Pump(TimeSpan.FromSeconds(0.5)); p.SimulateLostQueueCount();
                var since = Stopwatch.StartNew(); Sta.Pump(TimeSpan.FromMilliseconds(100)); long frozenAt = p.GetStats().Drawn; double resumedMs = -1;
                while (feeder.IsAlive && since.Elapsed < TimeSpan.FromSeconds(8))
                {
                    Sta.Pump(TimeSpan.FromMilliseconds(20));
                    if (resumedMs < 0 && p.GetStats().Drawn > frozenAt + 2) resumedMs = since.Elapsed.TotalMilliseconds;
                }
                Sta.Pump(TimeSpan.FromSeconds(0.3)); var s = p.GetStats();
                string msg = $"resumed after {resumedMs:0} ms, drawn {s.Drawn}, dropped {s.Dropped}, recreated {p.SwapChainRecreates}, device resets {s.DeviceResets}";
                Assert.True(resumedMs >= 900 && resumedMs <= 1500, msg); Assert.Equal(1, p.SwapChainRecreates); Assert.Equal(0, s.DeviceResets); Assert.True(s.Drawn >= 70, msg); });

        // ResizeBuffers must pass the flags the swap chain was created with, the waitable-object flag included, or it fails.
        [SkippableFact, Trait("Category", "Hardware")] public void Waitable_swap_chain_survives_resize()
            => WithPresenter((p, slot, f, form) => { var feeder = Feed(slot, f, 150); Sta.Pump(TimeSpan.FromSeconds(0.5));
                form.Size = new System.Drawing.Size(960, 540); Sta.Pump(TimeSpan.FromSeconds(0.5)); form.Size = new System.Drawing.Size(1280, 720);
                var wait = Stopwatch.StartNew(); while (feeder.IsAlive && wait.Elapsed < TimeSpan.FromSeconds(8)) Sta.Pump(TimeSpan.FromMilliseconds(50));
                Sta.Pump(TimeSpan.FromSeconds(0.3)); var s = p.GetStats();
                Assert.True(s.Drawn >= 100, $"drawn {s.Drawn}, dropped {s.Dropped}"); Assert.Equal(0, s.DeviceResets); });

        // DelayCheck passes vacuously when RefreshHz is 0, so a broken frame-statistics path must fail here.
        [SkippableFact, Trait("Category", "Hardware")] public void Measures_latency_from_frame_statistics()
            => WithPresenter((p, slot, f) => { p.MeasureLatency = true; Feed(slot, f, 150); Sta.Pump(TimeSpan.FromSeconds(2)); var s = p.GetStats();
                Assert.True(s.PresentToScreen.Count > 0, "no exact Present to screen samples"); Assert.InRange(s.RefreshHz, 23, 500); Assert.True(s.Overlay + s.Composed > 0, "no presentation mode samples"); });

        // A render thread stuck past Stop's 2 s join (here in a blocking LumaSampled handler) must still free the
        // window once it exits: DXGI allows one flip-model swap chain per window, and the next session reuses it.
        [SkippableFact, Trait("Category", "Hardware")] public void A_render_thread_that_outlives_dispose_still_frees_the_window()
            => Sta.Run(() =>
            {
                using var form = new Form { Width = 1280, Height = 720, StartPosition = FormStartPosition.CenterScreen }; form.Show();
                var f = new CaptureFormat(1280, 720, GrabberFormat.Nv12, false);
                using var blocked = new ManualResetEventSlim(); using var unblock = new ManualResetEventSlim();
                var slot = new LatestFrameSlot(f.FrameBytes);
                var stuck = new D3D11Presenter(form.Handle, NullLog.Instance);
                stuck.LumaSampled += _ => { blocked.Set(); unblock.Wait(10000); };
                stuck.Start(f, slot); Feed(slot, f, 60); Sta.Pump(TimeSpan.FromSeconds(1));
                Assert.True(blocked.IsSet, "the render thread never raised LumaSampled");
                stuck.Dispose(); // the join gives up after 2 s while the handler still blocks
                unblock.Set();   // now the render thread finishes its frame and exits
                using var next = CreateWithin(form.Handle, TimeSpan.FromSeconds(3));
                var nextSlot = new LatestFrameSlot(f.FrameBytes); next.Start(f, nextSlot); Feed(nextSlot, f, 60); Sta.Pump(TimeSpan.FromSeconds(1));
                Assert.True(next.GetStats().Drawn >= 30, $"drawn {next.GetStats().Drawn}"); next.Stop();
            });

        static D3D11Presenter CreateWithin(IntPtr hwnd, TimeSpan limit)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                try { return new D3D11Presenter(hwnd, NullLog.Instance); }
                catch (SharpDX.SharpDXException) when (sw.Elapsed < limit) { Sta.Pump(TimeSpan.FromMilliseconds(50)); }
            }
        }
    }
}
