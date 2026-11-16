using System;
using System.Collections.Generic;
using Framejet.Core.Audio;
using Framejet.Platform.Audio;
using Framejet.Platform.Video;

namespace Framejet
{
    /// <summary>
    /// The A/V sync check's taps on the real backends: every frame's arrival luma, every 1 ms block's peak, and the
    /// latency measurement for the picture delay. Begin and End run on the UI thread; the taps fire on DirectShow's
    /// streaming thread and NAudio's capture thread and only add to locked lists.
    /// </summary>
    sealed class SyncTaps : ISyncTaps
    {
        // 30 s of 240 fps video and of 1 ms blocks, so a check that never ends can't grow without bound.
        const int MaxFrames = 30 * 240, MaxBlocks = 30 * 1000;

        readonly DirectShowVideoBackend _video;
        readonly WasapiAudioBackend _audio;
        readonly object _gate = new object();
        readonly List<(long Ticks, double Luma)> _frames = new List<(long, double)>();
        readonly List<(long Ticks, float Peak)> _blocks = new List<(long, float)>();
        bool _collecting; // guarded by _gate
        bool _measureBefore;

        public SyncTaps(DirectShowVideoBackend video, WasapiAudioBackend audio)
        {
            _video = video ?? throw new ArgumentNullException(nameof(video));
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        }

        public void Begin()
        {
            lock (_gate)
            {
                _frames.Clear();
                _blocks.Clear();
                _collecting = true;
            }
            _measureBefore = _video.MeasureLatency; // the self-test may already have it on
            _video.MeasureLatency = true;
            _video.ArrivalLumaTap = OnFrame;
            _audio.BlockPeakTap = OnBlock;
        }

        public (IReadOnlyList<(long Ticks, double Luma)> Frames, IReadOnlyList<(long Ticks, float Peak)> Audio, double AudioDelayMs, double PictureDelayMs) End()
        {
            _video.ArrivalLumaTap = null;
            _audio.BlockPeakTap = null;
            var picture = _video.GetStats();
            _video.MeasureLatency = _measureBefore;
            var audio = _audio.GetStats(reset: false);

            (long, double)[] frames;
            (long, float)[] blocks;
            lock (_gate)
            {
                _collecting = false; // a tap already running when it was cleared adds nothing now
                frames = _frames.ToArray();
                blocks = _blocks.ToArray();
                _frames.Clear();
                _blocks.Clear();
            }
            return (frames, blocks, audio.Delay.TotalMeanMs, picture.ArrivalToPresent.MeanMs + picture.PresentToScreen.MeanMs);
        }

        void OnFrame(long ticks, double luma)
        {
            lock (_gate)
                if (_collecting && _frames.Count < MaxFrames) _frames.Add((ticks, luma));
        }

        void OnBlock(long ticks, float peak)
        {
            lock (_gate)
                if (_collecting && _blocks.Count < MaxBlocks) _blocks.Add((ticks, peak));
        }
    }
}
