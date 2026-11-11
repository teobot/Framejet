using System;
using System.Linq;
using Framejet.Core.Devices;
using Framejet.Core.Video;
using Framejet.Platform.Tests.TestSupport;
using Framejet.Platform.Video;
using Xunit;

namespace Framejet.Platform.Tests.Video
{
    public class DirectShowDevicesTests
    {
        [SkippableFact, Trait("Category", "Hardware")]
        public void Ripsaw_offers_1440p60_nv12_and_1080p120()
        {
            var modes = ModeCatalog.Build(DirectShowDevices.ListRawModes(Hardware.Ripsaw()));
            var keys = modes.Select(m => m.Key).ToList();
            Assert.Contains("2560x1440@60/NV12", keys);
            Assert.Contains(keys, k => k.StartsWith("1920x1080@120/"));
            Assert.Equal("2560x1440@60/NV12", ModeCatalog.PickDefault(modes, 2560, 1440).Key);
        }

        [SkippableFact, Trait("Category", "Hardware")]
        public void Webcam_offers_1080p30_mjpg()
            => Assert.Contains("1920x1080@30/MJPG", ModeCatalog.Build(DirectShowDevices.ListRawModes(Hardware.Webcam())).Select(m => m.Key));

        [SkippableFact, Trait("Category", "Hardware")]
        public void Raw_modes_carry_their_subtype()
            => Assert.All(DirectShowDevices.ListRawModes(Hardware.Ripsaw()), m => Assert.NotEqual(Guid.Empty, m.Subtype));

        [Fact]
        public void Unknown_device_error_is_prefixed_with_the_device_name()
        {
            var ex = Assert.Throws<VideoStartException>(
                () => DirectShowDevices.ListRawModes(new VideoDeviceInfo("Ghost Cam", "no-such-path")));
            Assert.StartsWith("Ghost Cam: ", ex.Message);
        }
    }
}
