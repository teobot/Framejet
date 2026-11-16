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
        /// <summary>Centres <paramref name="form"/> in the primary screen's working area. Call after sizing it and before Show.</summary>
        public static T Centre<T>(T form) where T : Form
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2);
            return form;
        }
    }
}
