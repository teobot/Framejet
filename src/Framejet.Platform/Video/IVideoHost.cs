using System;
using System.Drawing;

namespace Framejet.Platform.Video
{
    /// <summary>The window area that video is drawn into. Every member is used on the UI thread.</summary>
    public interface IVideoHost
    {
        IntPtr VideoHandle { get; }
        Size VideoSize { get; }
        void ShowVideo(bool visible);
    }
}
