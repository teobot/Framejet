using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Framejet.Core.Video;
using Xunit;

namespace Framejet.Core.Tests.Video
{
    public class FrameHelpersTests
    {
        static void Publish(LatestFrameSlot s, byte[] d, long t) { var h = GCHandle.Alloc(d, GCHandleType.Pinned); try { s.Publish(h.AddrOfPinnedObject(), d.Length, t); } finally { h.Free(); } }

        [Theory]
        [InlineData(GrabberFormat.Nv12, 2560, 1440, 5_529_600)] [InlineData(GrabberFormat.Yuy2, 1920, 1080, 4_147_200)]
        [InlineData(GrabberFormat.Uyvy, 1920, 1080, 4_147_200)] [InlineData(GrabberFormat.Rgb32, 1920, 1080, 8_294_400)]
        public void Frame_sizes(GrabberFormat f, int w, int h, int bytes) => Assert.Equal(bytes, new CaptureFormat(w, h, f, false).FrameBytes);

        [Fact] public void Slot_hands_over_the_newest_frame()
        {
            var s = new LatestFrameSlot(4); var buf = new byte[4];
            Assert.False(s.TryTake(ref buf, out _));
            Publish(s, new byte[] { 1, 1, 1, 1 }, 100); Publish(s, new byte[] { 2, 2, 2, 2 }, 200);
            Assert.True(s.FrameReady.WaitOne(0));
            Assert.True(s.TryTake(ref buf, out long t));
            Assert.Equal(new byte[] { 2, 2, 2, 2 }, buf); Assert.Equal(200, t); Assert.Equal(1, s.Replaced); Assert.Equal(2, s.Published);
        }
        [Fact] public void Slot_counts_size_mismatches()
        { var s = new LatestFrameSlot(4); Publish(s, new byte[6], 1); Assert.Equal(1, s.SizeMismatches); }
        [Fact] public void Slot_remembers_the_first_mismatched_size_and_still_hands_the_frame_over()
        {
            var s = new LatestFrameSlot(4); var buf = new byte[4];
            Assert.Equal(0, s.FirstMismatchBytes);
            Publish(s, new byte[] { 1, 1, 1, 1 }, 1); Assert.Equal(0, s.SizeMismatches);
            Publish(s, new byte[] { 2, 2, 2, 2, 2, 2 }, 2); Publish(s, new byte[] { 3, 3 }, 3);
            Assert.Equal(2, s.SizeMismatches); Assert.Equal(6, s.FirstMismatchBytes);
            Assert.True(s.TryTake(ref buf, out long t)); Assert.Equal(3, t);   // the short frame is drawn over older bytes
            Assert.Equal(new byte[] { 3, 3, 2, 2 }, buf);
        }

        [Fact] public void Luma_of_flat_black_nv12() { var f = new CaptureFormat(64, 64, GrabberFormat.Nv12, false); var d = Enumerable.Repeat((byte)16, f.FrameBytes).ToArray();
            var l = LumaSampler.Sample(d, f); Assert.Equal(16, l.Mean, 3); Assert.Equal(0, l.StdDev, 3); }
        [Fact] public void Luma_reads_only_y_bytes_of_yuy2_and_uyvy()
        {
            var f = new CaptureFormat(64, 64, GrabberFormat.Yuy2, false); var d = new byte[f.FrameBytes];
            for (int i = 0; i < d.Length; i++) d[i] = (byte)(i % 2 == 0 ? 235 : 128);
            Assert.Equal(235, LumaSampler.Sample(d, f).Mean, 3);
            var u = new CaptureFormat(64, 64, GrabberFormat.Uyvy, false); var e = new byte[u.FrameBytes];
            for (int i = 0; i < e.Length; i++) e[i] = (byte)(i % 2 == 1 ? 235 : 128);
            Assert.Equal(235, LumaSampler.Sample(e, u).Mean, 3);
        }
        [Fact] public void Luma_of_rgb32_uses_green()
        { var f = new CaptureFormat(64, 64, GrabberFormat.Rgb32, true); var d = new byte[f.FrameBytes]; for (int i = 1; i < d.Length; i += 4) d[i] = 200;
          Assert.Equal(200, LumaSampler.Sample(d, f).Mean, 3); }

        [Fact] public void Dark_fraction_of_the_ripsaw_no_signal_frame_is_about_99_percent()
        {
            var f = new CaptureFormat(1920, 1080, GrabberFormat.Nv12, false); var d = new byte[f.FrameBytes];
            int pixels = f.Width * f.Height;
            for (int p = 0; p < pixels * 7 / 1000; p++) d[p] = 217;   // a small white logo, the rest Y=0
            var l = LumaSampler.Sample(d, f);
            Assert.InRange(l.DarkFraction, 0.985, 0.998);
        }
        [Fact] public void Dark_fraction_of_video_black_is_zero()
        { var f = new CaptureFormat(64, 64, GrabberFormat.Nv12, false); var d = Enumerable.Repeat((byte)16, f.FrameBytes).ToArray();
          Assert.Equal(0, LumaSampler.Sample(d, f).DarkFraction, 3); }

        static readonly LumaStats RipsawNoSignal = new(1.5, 18, 0.993), VideoBlack = new(16, 0, 0), FullBlack = new(0, 0, 1.0),
            Picture = new(52.4, 19.8, 0.03), Normal = new(90, 40, 0);
        static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
        static NoSignalDetector Run(LumaStats stats)
        { var n = new NoSignalDetector(); for (int i = 0; i <= 40; i++) n.Update(stats, S(i * 0.1)); return n; }
        [Fact] public void No_signal_after_3s_of_the_ripsaw_screen_and_clears_at_once()
        {
            var n = new NoSignalDetector();
            Assert.False(n.Update(RipsawNoSignal, S(0))); Assert.False(n.Update(RipsawNoSignal, S(2.9))); Assert.False(n.NoSignal);
            Assert.True(n.Update(RipsawNoSignal, S(3.0))); Assert.True(n.NoSignal);
            Assert.True(n.Update(Normal, S(3.1))); Assert.False(n.NoSignal);
        }
        [Fact] public void Full_range_black_is_no_signal() => Assert.True(Run(FullBlack).NoSignal);
        [Fact] public void Limited_range_black_at_Y16_is_never_no_signal() => Assert.False(Run(VideoBlack).NoSignal);
        [Fact] public void A_real_picture_is_never_no_signal() => Assert.False(Run(Picture).NoSignal);
        [Fact] public void One_non_dark_sample_resets_the_hold()
        {
            var n = new NoSignalDetector();
            n.Update(RipsawNoSignal, S(0)); n.Update(RipsawNoSignal, S(2)); n.Update(Picture, S(2.5));
            Assert.False(n.Update(RipsawNoSignal, S(3.0))); Assert.False(n.Update(RipsawNoSignal, S(5.9))); Assert.False(n.NoSignal);
            Assert.True(n.Update(RipsawNoSignal, S(6.0)));
        }
        [Fact] public void Dark_fraction_limit_is_inclusive()
        { Assert.True(Run(new LumaStats(5, 10, NoSignalDetector.DarkFractionMin)).NoSignal); Assert.False(Run(new LumaStats(5, 10, 0.949)).NoSignal); }

        [Theory]
        [InlineData(1920, 1080, 2560, 1440, 0, 0, 2560, 1440)] [InlineData(640, 480, 2560, 1440, 320, 0, 1920, 1440)]
        [InlineData(1920, 1080, 2560, 1600, 0, 80, 2560, 1440)] [InlineData(720, 480, 1920, 1080, 150, 0, 1620, 1080)]
        public void Letterbox_keeps_aspect_and_centres(int sw, int sh, int tw, int th, int x, int y, int w, int h)
            => Assert.Equal(new Rectangle(x, y, w, h), Letterbox.Fit(sw, sh, tw, th));

        [Theory]   // height, Y, U, V, R, G, B
        [InlineData(1080, 235, 128, 128, 1, 1, 1)] [InlineData(1080, 16, 128, 128, 0, 0, 0)] [InlineData(1080, 63, 102, 240, 1, 0, 0)]
        [InlineData(480, 235, 128, 128, 1, 1, 1)] [InlineData(480, 81, 90, 240, 1, 0, 0)]
        public void Colour_matrix_is_bt709_from_720_lines_else_bt601(int height, byte y, byte u, byte v, float r, float g, float b)
        { var c = ColorMatrix.Apply(ColorMatrix.YuvToRgb(height), y, u, v); Assert.InRange(c.R, r - 0.01, r + 0.01); Assert.InRange(c.G, g - 0.01, g + 0.01); Assert.InRange(c.B, b - 0.01, b + 0.01); }
    }
}
