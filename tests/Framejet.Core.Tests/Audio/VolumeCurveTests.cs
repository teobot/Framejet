using Framejet.Core.Audio;
using Xunit;

namespace Framejet.Core.Tests.Audio
{
    public class VolumeCurveTests
    {
        [Theory]
        [InlineData(0f, 0f)] [InlineData(0.5f, 0.25f)] [InlineData(1f, 1f)]
        [InlineData(-0.2f, 0f)] [InlineData(1.5f, 1f)]
        public void ToGain_squares_the_clamped_slider(float slider, float gain)
            => Assert.Equal((double)gain, (double)VolumeCurve.ToGain(slider), 4);
    }
}
