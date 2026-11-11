using System;
using Framejet.Platform.Devices;
using Framejet.Platform.Tests.TestSupport;
using Xunit;

namespace Framejet.Platform.Tests.Devices
{
    public class DeviceContainersTests
    {
        [SkippableFact, Trait("Category", "Hardware")]
        public void Ripsaw_container_matches_windows()
            => Assert.Equal(Hardware.RipsawContainer(), DeviceContainers.ForDirectShowDevicePath(Hardware.Ripsaw().Path));

        [SkippableFact, Trait("Category", "Hardware")]
        public void Webcam_container_matches_windows()
            => Assert.Equal(Hardware.WebcamContainer(), DeviceContainers.ForDirectShowDevicePath(Hardware.Webcam().Path));

        [Fact]
        public void Unknown_path_gives_empty()
            => Assert.Equal(Guid.Empty, DeviceContainers.ForDirectShowDevicePath(@"@device:pnp:\\?\nope#nothing"));
    }
}
