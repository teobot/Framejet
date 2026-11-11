using Framejet.Platform.Tests.TestSupport;
using Xunit;

namespace Framejet.Platform.Tests.TestSupport
{
    public class HardwareFileTests
    {
        [Fact] public void Parse_skips_comments_and_blank_values()
        {
            var keys = Hardware.Parse(new[] { "# note", "; note", "", "webcam = USB Webcam", "microphone =", "Ripsaw.Container={A1B2C3D4-0000-4000-8000-000000000001}" });
            Assert.Equal("USB Webcam", keys["webcam"]);
            Assert.Equal("{A1B2C3D4-0000-4000-8000-000000000001}", keys["ripsaw.container"]);
            Assert.False(keys.ContainsKey("microphone"));
            Assert.Equal(2, keys.Count);
        }
    }
}
