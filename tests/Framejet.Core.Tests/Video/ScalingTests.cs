using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Framejet.Core.Video;
using Xunit;

namespace Framejet.Core.Tests.Video
{
    public class ScalingTests
    {
        public static IEnumerable<object[]> AllModes => Scaling.All.Select(m => new object[] { m });

        [Theory] // sw, sh, tw, th, mode, x, y, w, h
        [InlineData(2560, 1440, 2560, 1360, ScalingMode.Fill,             71,   0, 2418, 1360)]
        [InlineData(2560, 1440, 2560, 1360, ScalingMode.WholeStepsCrisp,  71,   0, 2418, 1360)] // 1x doesn't fit: falls back to Fill
        [InlineData(2560, 1440, 2560, 1360, ScalingMode.WholeStepsSmooth, 71,   0, 2418, 1360)]
        [InlineData(2560, 1440, 2560, 1360, ScalingMode.OneToOne,          0, -40, 2560, 1440)] // crop, centred
        [InlineData(1920, 1080, 2560, 1440, ScalingMode.Fill,              0,   0, 2560, 1440)]
        [InlineData(1920, 1080, 2560, 1440, ScalingMode.WholeStepsCrisp, 320, 180, 1920, 1080)]
        [InlineData(1920, 1080, 2560, 1440, ScalingMode.OneToOne,        320, 180, 1920, 1080)]
        [InlineData(1280,  720, 2560, 1440, ScalingMode.WholeStepsSmooth,  0,   0, 2560, 1440)] // exactly 2x
        [InlineData(1280,  720, 2560, 1360, ScalingMode.WholeStepsCrisp, 640, 320, 1280,  720)] // 2x doesn't fit: 1x
        [InlineData(1920, 1080, 1280,  720, ScalingMode.OneToOne,       -320, -180, 1920, 1080)]
        public void Place_follows_the_mode(int sw, int sh, int tw, int th, ScalingMode mode, int x, int y, int w, int h)
            => Assert.Equal(new Rectangle(x, y, w, h), Scaling.Place(sw, sh, tw, th, mode));

        [Theory, MemberData(nameof(AllModes))]
        public void Place_with_no_area_does_not_throw(ScalingMode mode) { Scaling.Place(1920, 1080, 0, 0, mode); }

        [Theory, MemberData(nameof(AllModes))]
        public void Place_with_an_empty_source_is_empty(ScalingMode mode)
        {
            Assert.Equal(Rectangle.Empty, Scaling.Place(0, 0, 2560, 1440, mode));
            Assert.Equal(Rectangle.Empty, Scaling.Place(0, 720, 2560, 1440, mode));
        }

        [Theory] // mode, box w, box h, expected (source 1280x720)
        [InlineData(ScalingMode.Fill,             2560, 1440, ScaleFilter.CatmullRom)]
        [InlineData(ScalingMode.WholeStepsSmooth, 2560, 1440, ScaleFilter.CatmullRom)]
        [InlineData(ScalingMode.WholeStepsCrisp,  2560, 1440, ScaleFilter.Point)]
        [InlineData(ScalingMode.WholeStepsCrisp,  1280,  720, ScaleFilter.Bilinear)]
        [InlineData(ScalingMode.WholeStepsCrisp,   640,  360, ScaleFilter.Bilinear)] // shrunk to fit: same as Fill
        [InlineData(ScalingMode.OneToOne,         1280,  720, ScaleFilter.Bilinear)]
        [InlineData(ScalingMode.Fill,              640,  360, ScaleFilter.Bilinear)]
        public void Filter_is_point_only_for_crisp_upscaling(ScalingMode mode, int bw, int bh, ScaleFilter expected)
            => Assert.Equal(expected, Scaling.Filter(mode, new Rectangle(0, 0, bw, bh), 1280, 720));

        [Theory, MemberData(nameof(AllModes))]
        public void Key_round_trips(ScalingMode mode) => Assert.Equal(mode, Scaling.Parse(Scaling.Key(mode)));

        [Theory] [InlineData(null)] [InlineData("")] [InlineData("maybe")]
        public void Unknown_keys_parse_as_fill(string key) => Assert.Equal(ScalingMode.Fill, Scaling.Parse(key));

        [Fact] public void Keys_are_the_settings_values()
            => Assert.Equal(new[] { "fill", "crisp", "smooth", "1to1" }, Scaling.All.Select(Scaling.Key).ToArray());
    }
}
