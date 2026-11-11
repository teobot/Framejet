using Framejet.Core.Video;
using Framejet.Platform.Tests.TestSupport;
using Framejet.Platform.Video;
using Xunit;

namespace Framejet.Platform.Tests.Video
{
    public class FrameRendererTests
    {
        static byte[] Render(CaptureFormat f, byte[] frame, int tw, int th)
        { using var d = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Hardware, SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
          using var r = new FrameRenderer(d); r.Configure(f); return r.RenderToBgra(frame, tw, th); }
        static void AssertPixel(byte[] bgra, int width, int x, int y, int b, int g, int r)
        { int i = (y * width + x) * 4; Assert.InRange(bgra[i], b - 3, b + 3); Assert.InRange(bgra[i + 1], g - 3, g + 3); Assert.InRange(bgra[i + 2], r - 3, r + 3); }

        [SkippableFact, Trait("Category", "Hardware")] public void Bt709_red_renders_red()
        { var f = new CaptureFormat(1280, 720, GrabberFormat.Nv12, false); AssertPixel(Render(f, SyntheticFrames.Solid(f, 63, 102, 240), 1280, 720), 1280, 640, 360, 0, 0, 255); }
        [SkippableFact, Trait("Category", "Hardware")] public void Bt601_white_and_black_yuy2()
        { var f = new CaptureFormat(640, 480, GrabberFormat.Yuy2, false); var px = Render(f, SyntheticFrames.SplitLeftRight(f, 235, 16), 640, 480);
          AssertPixel(px, 640, 100, 240, 255, 255, 255); AssertPixel(px, 640, 540, 240, 0, 0, 0); }
        [SkippableFact, Trait("Category", "Hardware")] public void Uyvy_white_renders_white()
        { var f = new CaptureFormat(640, 480, GrabberFormat.Uyvy, false); AssertPixel(Render(f, SyntheticFrames.Solid(f, 235, 128, 128), 640, 480), 640, 320, 240, 255, 255, 255); }
        [SkippableFact, Trait("Category", "Hardware")] public void Rgb32_bottom_up_is_flipped()
        { var f = new CaptureFormat(64, 64, GrabberFormat.Rgb32, true); var px = Render(f, SyntheticFrames.RowsTopBottom(f, firstRowsInMemoryRed: 32), 64, 64);
          AssertPixel(px, 64, 32, 63, 0, 0, 255); AssertPixel(px, 64, 32, 0, 255, 0, 0); }
        [SkippableFact, Trait("Category", "Hardware")] public void Letterboxes_4_3_into_16_9()
        { var f = new CaptureFormat(640, 480, GrabberFormat.Nv12, false); var px = Render(f, SyntheticFrames.Solid(f, 235, 128, 128), 1280, 720);
          AssertPixel(px, 1280, 100, 360, 0, 0, 0); AssertPixel(px, 1280, 640, 360, 255, 255, 255); }
    }
}
