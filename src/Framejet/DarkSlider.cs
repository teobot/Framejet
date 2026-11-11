using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Framejet
{
    /// <summary>A small dark horizontal slider, 0..1: drag, click or use the wheel. <see cref="ValueChanged"/> is raised whenever the value changes.</summary>
    sealed class DarkSlider : Control
    {
        const float WheelStep = 0.05f;
        float _value;
        bool _dragging;

        public DarkSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = DarkTheme.Toolbar;
        }

        public event EventHandler ValueChanged;

        /// <summary>True while the user is dragging the thumb.</summary>
        public bool Dragging => _dragging;

        public float Value
        {
            get => _value;
            set
            {
                var clamped = float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));
                if (clamped == _value) return;
                _value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        float DpiScale => DeviceDpi / 96f;
        float ThumbRadius => 6f * DpiScale;

        void SetFromX(int x)
        {
            var left = ThumbRadius;
            var width = Math.Max(1f, Width - 2 * ThumbRadius);
            Value = (x - left) / width;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            Capture = true;
            SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) _dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta != 0) Value += e.Delta > 0 ? WheelStep : -WheelStep;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = ThumbRadius;
            var trackHeight = Math.Max(2f, 4f * DpiScale);
            var left = r;
            var width = Math.Max(1f, Width - 2 * r);
            var cy = Height / 2f;
            var thumbX = left + width * _value;
            using (var back = new SolidBrush(DarkTheme.ControlBorder))
            using (var fill = new SolidBrush(DarkTheme.SliderFill))
            using (var thumb = new SolidBrush(DarkTheme.Text))
            {
                g.FillRectangle(back, left, cy - trackHeight / 2, width, trackHeight);
                g.FillRectangle(fill, left, cy - trackHeight / 2, thumbX - left, trackHeight);
                g.FillEllipse(thumb, thumbX - r, cy - r, 2 * r, 2 * r);
            }
        }
    }
}
