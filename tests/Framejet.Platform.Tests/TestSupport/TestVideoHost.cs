using System;
using System.Drawing;
using System.Windows.Forms;
using Framejet.Platform.Video;

namespace Framejet.Platform.Tests.TestSupport
{
    /// <summary>
    /// A TopMost window with a child panel to draw video on, shaped like the app's video surface: the panel stays
    /// hidden until the backend shows it. Create, use and dispose it on an STA thread.
    /// </summary>
    internal sealed class TestVideoHost : IVideoHost, IDisposable
    {
        readonly Form _form;
        readonly VideoPanel _panel;

        /// <param name="width">Client width; ignored when <paramref name="fullscreenOnPrimary"/>.</param>
        /// <param name="height">Client height; ignored when <paramref name="fullscreenOnPrimary"/>.</param>
        /// <param name="fullscreenOnPrimary">Borderless, covering the whole primary screen.</param>
        public TestVideoHost(int width, int height, bool fullscreenOnPrimary)
        {
            _form = new Form { Text = "Framejet test video", TopMost = true, BackColor = Color.Black };
            if (fullscreenOnPrimary)
            {
                _form.FormBorderStyle = FormBorderStyle.None;
                _form.StartPosition = FormStartPosition.Manual;
                _form.Bounds = Screen.PrimaryScreen.Bounds;
            }
            else
            {
                _form.StartPosition = FormStartPosition.CenterScreen;
                _form.ClientSize = new Size(width, height);
            }
            _panel = new VideoPanel { Dock = DockStyle.Fill, Visible = false };
            _form.Controls.Add(_panel);
            _form.Show();
        }

        public IntPtr VideoHandle => _panel.Handle;
        public Size VideoSize => _panel.ClientSize;
        public void ShowVideo(bool visible) => _panel.Visible = visible;
        public void Dispose() => _form.Dispose();

        /// <summary>Never paints, so nothing is drawn over the swap chain (as in the spike).</summary>
        sealed class VideoPanel : Panel
        {
            public VideoPanel()
            {
                BackColor = Color.Black;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }
        }
    }
}
