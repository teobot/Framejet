using System.Windows.Forms;
using Framejet.Platform.Os;
using Xunit;

namespace Framejet.Platform.Tests.Os
{
    public class DisplayModeTests
    {
        [Fact] public void The_primary_screens_display_mode_refresh_is_a_real_rate()
        {
            int hz = DisplayMode.CurrentRefreshHz(Screen.PrimaryScreen.DeviceName);
            Assert.InRange(hz, 23, 500);
        }
    }
}
