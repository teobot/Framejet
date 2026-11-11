using System;
using System.Collections.Generic;
using System.Linq;
using Framejet.Core.Video;
using static Framejet.Core.Video.PixelFormat;

namespace Framejet.Core.Tests.Video
{
    public static class TestModes
    {
        public static VideoMode M(int w, int h, double fps, PixelFormat f) => new VideoMode(w, h, (long)Math.Round(1e7 / fps), f);
        public static readonly VideoMode[] RipsawRaw = {
            M(640,480,60,NV12), M(720,480,60,NV12), M(720,576,50,NV12), M(1280,720,60,NV12),
            M(1920,1080,120,NV12), M(1920,1080,60,NV12), M(1920,1080,30,NV12),
            M(2560,1440,60,NV12), M(2560,1440,30,NV12), M(3840,2160,30,NV12),
            M(640,480,60,YUY2), M(720,480,60,YUY2), M(720,576,50,YUY2), M(1280,720,60,YUY2),
            M(1920,1080,60,YUY2), M(2560,1440,50,YUY2),
            M(1920,1080,60,NV12),                          // the same cap listed twice (VIH and VIH2)
        };
        public static readonly VideoMode[] WebcamRaw = {
            M(160,120,30,MJPG), M(320,240,30,MJPG), M(640,480,30,MJPG), M(1280,720,30,MJPG), M(1920,1080,30,MJPG),
            M(320,240,30,YUY2), M(640,480,30,YUY2), M(800,600,24,YUY2), M(960,720,15,YUY2), M(1280,720,10,YUY2), M(1920,1080,5,YUY2),
        };
        public static string[] Keys(IEnumerable<VideoMode> m) => m.Select(x => x.Key).ToArray();
    }
}
