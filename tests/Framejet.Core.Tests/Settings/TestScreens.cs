using System.Drawing;
using Framejet.Core.Settings;

namespace Framejet.Core.Tests.Settings
{
    public static class TestScreens
    {
        public static readonly ScreenInfo Main = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1392), true);
        public static readonly ScreenInfo Left = new(@"\\.\DISPLAY2", new Rectangle(-1920, 270, 1920, 1080), new Rectangle(-1920, 270, 1920, 1032), false);
        public static readonly ScreenInfo Right = new(@"\\.\DISPLAY3", new Rectangle(2560, 270, 1920, 1080), new Rectangle(2560, 270, 1920, 1032), false);
        // A layout with the taskbar on DISPLAY1 only.
        public static readonly ScreenInfo MainTaskbarD1 = new(@"\\.\DISPLAY1", new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1400), true);
        public static readonly ScreenInfo MainTaskbarD2 = new(@"\\.\DISPLAY2", new Rectangle(-1920, 270, 1920, 1080), new Rectangle(-1920, 270, 1920, 1080), false);
        public static readonly ScreenInfo MainTaskbarD3 = new(@"\\.\DISPLAY3", new Rectangle(2560, 270, 1920, 1080), new Rectangle(2560, 270, 1920, 1080), false);
        public static ScreenInfo[] MainTaskbarOnly => new[] { MainTaskbarD1, MainTaskbarD2, MainTaskbarD3 };
        public static ScreenInfo[] All => new[] { Main, Left, Right };
    }
}
