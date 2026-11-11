namespace Framejet.Core.Video
{
    public enum PixelFormat { NV12, YUY2, UYVY, OtherRaw, MJPG, H264 }

    public enum GrabberFormat { Nv12, Yuy2, Uyvy, Rgb32 }

    public static class PixelFormats
    {
        /// <summary>Lower is better.</summary>
        public static int Rank(PixelFormat f)
        {
            switch (f)
            {
                case PixelFormat.NV12: return 0;
                case PixelFormat.YUY2: return 1;
                case PixelFormat.UYVY: return 2;
                case PixelFormat.OtherRaw: return 3;
                case PixelFormat.MJPG: return 4;
                default: return 5;
            }
        }

        public static PixelFormat FromFourCC(string fourCC)
        {
            switch (fourCC)
            {
                case "NV12": return PixelFormat.NV12;
                case "YUY2":
                case "YUYV": return PixelFormat.YUY2;
                case "UYVY": return PixelFormat.UYVY;
                case "MJPG": return PixelFormat.MJPG;
                case "H264": return PixelFormat.H264;
                default: return PixelFormat.OtherRaw;
            }
        }

        public static GrabberFormat GrabberFormatFor(PixelFormat f)
        {
            switch (f)
            {
                case PixelFormat.NV12: return GrabberFormat.Nv12;
                case PixelFormat.YUY2: return GrabberFormat.Yuy2;
                case PixelFormat.UYVY: return GrabberFormat.Uyvy;
                default: return GrabberFormat.Rgb32;
            }
        }

        public static string Name(PixelFormat f)
        {
            switch (f)
            {
                case PixelFormat.NV12: return "NV12";
                case PixelFormat.YUY2: return "YUY2";
                case PixelFormat.UYVY: return "UYVY";
                case PixelFormat.OtherRaw: return "RAW";
                case PixelFormat.MJPG: return "MJPG";
                default: return "H264";
            }
        }
    }
}
