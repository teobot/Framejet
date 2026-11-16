using System;
using System.Drawing;
using System.Windows.Forms;

namespace Framejet.Platform.Tests.TestSupport
{
    /// <summary>
    /// Puts test windows on the primary screen. <see cref="FormStartPosition.CenterScreen"/> follows the mouse to
    /// whichever screen it is on, so a timing test could land on a slower screen and fail for that screen's refresh.
    /// </summary>
    internal static class OnPrimaryScreen
    {
        /// <summary>
        /// Centres <paramref name="form"/> in the primary screen's working area, keeping its top-left corner inside it
        /// as CenterScreen does. Call after sizing it and before Show.
        /// </summary>
        public static void Centre(Form form)
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(Math.Max(area.Left, area.Left + (area.Width - form.Width) / 2),
                                      Math.Max(area.Top, area.Top + (area.Height - form.Height) / 2));
        }

        /// <summary>Makes <paramref name="form"/> borderless and covers the whole primary screen. Call before Show.</summary>
        public static void Cover(Form form)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = Screen.PrimaryScreen.Bounds;
        }
    }
}
