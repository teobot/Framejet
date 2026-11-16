using System.Drawing;

namespace Framejet.Core.Settings
{
    public sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HasBounds { get; set; }
        public bool Maximized { get; set; }
        public bool Fullscreen { get; set; }
        public string ScreenName { get; set; } = "";

        public Rectangle Bounds => new Rectangle(X, Y, Width, Height);
    }

    public sealed class AppSettings
    {
        public string VideoDevicePath { get; set; } = "";
        public string VideoDeviceName { get; set; } = "";
        public string ModeKey { get; set; } = "";
        /// <summary>Empty means the Default output.</summary>
        public string AudioOutputId { get; set; } = "";
        public string AudioOutputName { get; set; } = "";
        /// <summary>Empty means Automatic.</summary>
        public string AudioInputId { get; set; } = "";
        public string AudioInputName { get; set; } = "";
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }
        public bool VSync { get; set; } = true;
        /// <summary>With V-Sync On, every frame waits up to 1 s for a display-queue slot instead of being skipped after 4 ms. That costs up to a refresh of extra delay at 60 Hz.</summary>
        public bool NeverSkipFrames { get; set; }
        /// <summary>Whether to look for a newer release on start.</summary>
        public bool CheckForUpdates { get; set; } = true;
        public WindowPlacement Window { get; set; } = new();
    }
}
