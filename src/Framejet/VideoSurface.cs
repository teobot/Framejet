using System;
using System.Drawing;
using System.Windows.Forms;
using Framejet.Platform.Video;

namespace Framejet
{
    /// <summary>
    /// The picture area. Video is drawn into a child panel that the backend shows while it runs; with no video, the
    /// surface is black with <see cref="Message"/> centred on it.
    /// </summary>
    sealed class VideoSurface : Control, IVideoHost
    {
        static readonly Color MessageColor = Color.FromArgb(0xA9, 0xAA, 0xB2);

        readonly VideoPanel _videoHost;
        readonly Font _messageFont = new Font("Segoe UI", 12f);
        string _message;
        Point _lastMouse = new Point(int.MinValue, int.MinValue);

        public VideoSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            _videoHost = new VideoPanel { Dock = DockStyle.Fill, Visible = false };
            _videoHost.MouseMove += (s, e) => OnVideoMouseMove();
            _videoHost.DoubleClick += (s, e) => VideoDoubleClick?.Invoke(this, EventArgs.Empty);
            Controls.Add(_videoHost);
        }

        /// <summary>Raised when the mouse moves over the picture, whether or not video is showing.</summary>
        public event EventHandler VideoMouseMove;

        /// <summary>Raised on a double-click on the picture, whether or not video is showing.</summary>
        public event EventHandler VideoDoubleClick;

        /// <summary>Shown centred while there is no video; null for none.</summary>
        public string Message
        {
            get => _message;
            set
            {
                if (_message == value) return;
                _message = value;
                Invalidate();
            }
        }

        /// <summary>The menu for a right-click on the surface or on the video over it.</summary>
        public void SetMenu(ContextMenuStrip menu)
        {
            ContextMenuStrip = menu;
            _videoHost.ContextMenuStrip = menu;
        }

        public IntPtr VideoHandle => _videoHost.Handle;
        public Size VideoSize => _videoHost.ClientSize;
        public void ShowVideo(bool visible) => _videoHost.Visible = visible;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            if (string.IsNullOrEmpty(_message)) return;
            var area = Rectangle.Inflate(ClientRectangle, -16, -16);
            TextRenderer.DrawText(e.Graphics, _message, _messageFont, area, MessageColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            OnVideoMouseMove();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            VideoDoubleClick?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Windows also sends WM_MOUSEMOVE when nothing moved (a window appearing under the cursor, the cursor being
        /// shown or hidden), so only a real change of position counts.
        /// </summary>
        void OnVideoMouseMove()
        {
            var position = Cursor.Position;
            if (position == _lastMouse) return;
            _lastMouse = position;
            VideoMouseMove?.Invoke(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _messageFont.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Never paints, so nothing is drawn over the swap chain (as in the spike).</summary>
        sealed class VideoPanel : Panel
        {
            public VideoPanel()
            {
                BackColor = Color.Black;
                SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }
        }
    }
}
