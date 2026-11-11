using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Framejet
{
    /// <summary>The app's dark colours, the dark title bar and the dark menu look, shared by the window, the menu and the toolbar.</summary>
    static class DarkTheme
    {
        public static readonly Color Window = Color.FromArgb(0x0F, 0x0F, 0x12);
        public static readonly Color Toolbar = Color.FromArgb(0x14, 0x14, 0x18);
        public static readonly Color Control = Color.FromArgb(0x24, 0x24, 0x2B);
        public static readonly Color ControlBorder = Color.FromArgb(0x33, 0x33, 0x3B);
        public static readonly Color Text = Color.FromArgb(0xE6, 0xE6, 0xEA);
        public static readonly Color MutedText = Color.FromArgb(0x9F, 0xA0, 0xA8);
        public static readonly Color AccentBack = Color.FromArgb(0x1F, 0x3A, 0x5A);
        public static readonly Color AccentBorder = Color.FromArgb(0x2F, 0x5F, 0x93);
        public static readonly Color SliderFill = Color.FromArgb(0x5B, 0x8E, 0xE6);
        public static readonly Color MessageText = Color.FromArgb(0xA9, 0xAA, 0xB2);
        public static readonly Color MenuBack = Color.FromArgb(0x1F, 0x1F, 0x25);
        public static readonly Color MenuBorder = Color.FromArgb(0x34, 0x34, 0x3C);

        const int DwmwaUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        }

        public static ToolStripRenderer MenuRenderer() => new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };

        sealed class MenuColors : ProfessionalColorTable
        {
            public MenuColors() { UseSystemColors = false; }
            public override Color ToolStripDropDownBackground => MenuBack;
            public override Color MenuBorder => DarkTheme.MenuBorder;
            public override Color MenuItemBorder => AccentBorder;
            public override Color MenuItemSelected => AccentBack;
            public override Color MenuItemSelectedGradientBegin => AccentBack;
            public override Color MenuItemSelectedGradientEnd => AccentBack;
            public override Color MenuItemPressedGradientBegin => AccentBack;
            public override Color MenuItemPressedGradientEnd => AccentBack;
            public override Color ImageMarginGradientBegin => MenuBack;
            public override Color ImageMarginGradientMiddle => MenuBack;
            public override Color ImageMarginGradientEnd => MenuBack;
            public override Color SeparatorDark => DarkTheme.MenuBorder;
            public override Color SeparatorLight => DarkTheme.MenuBorder;
            public override Color CheckBackground => AccentBack;
            public override Color CheckSelectedBackground => AccentBack;
            public override Color CheckPressedBackground => AccentBack;
            public override Color ButtonSelectedBorder => AccentBorder;
        }
    }
}
