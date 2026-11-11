using Framejet.Core.Video;
using Xunit;
using static Framejet.Core.Video.PixelFormat;
using static Framejet.Core.Tests.Video.TestModes;

namespace Framejet.Core.Tests.Video
{
    public class ModeCatalogTests
    {
        [Fact] public void Ripsaw_list_has_one_entry_per_size_and_rate_with_nv12_preferred()
            => Assert.Equal(new[] { "3840x2160@30/NV12", "2560x1440@60/NV12", "2560x1440@50/YUY2", "2560x1440@30/NV12",
                                    "1920x1080@120/NV12", "1920x1080@60/NV12", "1920x1080@30/NV12", "1280x720@60/NV12",
                                    "720x576@50/NV12", "720x480@60/NV12", "640x480@60/NV12" }, Keys(ModeCatalog.Build(RipsawRaw)));
        [Fact] public void Webcam_list_hides_tiny_and_slow_modes()
            => Assert.Equal(new[] { "1920x1080@30/MJPG", "1280x720@30/MJPG", "800x600@24/YUY2", "640x480@30/YUY2" }, Keys(ModeCatalog.Build(WebcamRaw)));
        [Fact] public void Keeps_everything_when_nothing_passes_the_filter()
            => Assert.Equal(new[] { "320x240@15/YUY2" }, Keys(ModeCatalog.Build(new[] { M(320, 240, 15, PixelFormat.YUY2) })));
        [Theory]
        [InlineData(2560, 1440, "2560x1440@60/NV12")] [InlineData(1920, 1080, "1920x1080@60/NV12")] [InlineData(1280, 720, "1280x720@60/NV12")]
        public void Default_is_the_biggest_60fps_mode_that_fits(int sw, int sh, string key)
            => Assert.Equal(key, ModeCatalog.PickDefault(ModeCatalog.Build(RipsawRaw), sw, sh).Key);
        [Fact] public void Webcam_default_is_its_fastest_biggest_mode()
            => Assert.Equal("1920x1080@30/MJPG", ModeCatalog.PickDefault(ModeCatalog.Build(WebcamRaw), 2560, 1440).Key);
        [Fact] public void Falls_back_to_50fps()
            => Assert.Equal("1920x1080@50/NV12", ModeCatalog.PickDefault(ModeCatalog.Build(new[] { M(1920, 1080, 50, NV12), M(1280, 720, 50, NV12) }), 2560, 1440).Key);
        [Fact] public void Uses_all_modes_when_none_fit()
            => Assert.Equal("1920x1080@60/NV12", ModeCatalog.PickDefault(ModeCatalog.Build(new[] { M(1920, 1080, 60, NV12) }), 1280, 720).Key);
        [Theory]
        [InlineData("1920x1080@120/NV12", "1920x1080@120/NV12")] [InlineData("1920x1080@60/YUY2", "1920x1080@60/NV12")]
        [InlineData("garbage", "2560x1440@60/NV12")] [InlineData("", "2560x1440@60/NV12")]
        public void Resolve_prefers_the_saved_key(string saved, string expected)
            => Assert.Equal(expected, ModeCatalog.Resolve(ModeCatalog.Build(RipsawRaw), saved, 2560, 1440).Key);
    }
}
