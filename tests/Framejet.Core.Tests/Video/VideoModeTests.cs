using System.Globalization;
using System.Linq;
using Framejet.Core.Video;
using Xunit;
using static Framejet.Core.Video.PixelFormat;

namespace Framejet.Core.Tests.Video
{
    public class VideoModeTests
    {
        [Fact] public void Key_and_label_at_60fps()
        {
            var m = new VideoMode(2560, 1440, 166667, PixelFormat.NV12);
            Assert.Equal("2560x1440@60/NV12", m.Key);
            Assert.Equal("2560×1440 · 60 fps", m.Label);
        }
        [Fact] public void Key_and_label_at_59_94fps()
        {
            var m = new VideoMode(1920, 1080, 166833, PixelFormat.YUY2);
            Assert.Equal("1920x1080@59.94/YUY2", m.Key);
            Assert.Equal("1920×1080 · 59.94 fps", m.Label);
        }
        [Fact] public void Other_raw_formats_are_keyed_RAW() => Assert.Equal("640x480@30/RAW", TestModes.M(640, 480, 30, PixelFormat.OtherRaw).Key);
        [Fact] public void Equal_when_size_rounded_rate_and_format_match()
            => Assert.Equal(new VideoMode(1920, 1080, 166666, PixelFormat.NV12), new VideoMode(1920, 1080, 166667, PixelFormat.NV12));
        [Fact] public void Key_ignores_the_current_culture()
        {
            var old = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); Assert.Equal("1920x1080@59.94/YUY2", new VideoMode(1920, 1080, 166833, PixelFormat.YUY2).Key); }
            finally { CultureInfo.CurrentCulture = old; }
        }
        [Theory]
        [InlineData("NV12", PixelFormat.NV12)] [InlineData("YUY2", PixelFormat.YUY2)] [InlineData("YUYV", PixelFormat.YUY2)]
        [InlineData("UYVY", PixelFormat.UYVY)] [InlineData("MJPG", PixelFormat.MJPG)] [InlineData("H264", PixelFormat.H264)] [InlineData("I420", PixelFormat.OtherRaw)]
        public void FourCC_maps_to_format(string fourCC, PixelFormat f) => Assert.Equal(f, PixelFormats.FromFourCC(fourCC));
        [Fact] public void Ranking_is_nv12_yuy2_uyvy_raw_mjpg_h264()
            => Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[] { NV12, YUY2, UYVY, OtherRaw, MJPG, H264 }.Select(PixelFormats.Rank));
        [Theory]
        [InlineData(PixelFormat.NV12, GrabberFormat.Nv12)] [InlineData(PixelFormat.YUY2, GrabberFormat.Yuy2)] [InlineData(PixelFormat.UYVY, GrabberFormat.Uyvy)]
        [InlineData(PixelFormat.MJPG, GrabberFormat.Rgb32)] [InlineData(PixelFormat.H264, GrabberFormat.Rgb32)] [InlineData(PixelFormat.OtherRaw, GrabberFormat.Rgb32)]
        public void Grabber_format_follows_capture_format(PixelFormat f, GrabberFormat g) => Assert.Equal(g, PixelFormats.GrabberFormatFor(f));
    }
}
