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
        static void WithPresenter(Action<D3D11Presenter, LatestFrameSlot, CaptureFormat> body) => Sta.Run(() =>
        {
            using var form = new Form { Width = 1280, Height = 720, StartPosition = FormStartPosition.CenterScreen }; form.Show();
            var f = new CaptureFormat(1280, 720, GrabberFormat.Nv12, false); var slot = new LatestFrameSlot(f.FrameBytes);
            using var p = new D3D11Presenter(form.Handle, NullLog.Instance); p.Start(f, slot); body(p, slot, f); p.Stop();
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
