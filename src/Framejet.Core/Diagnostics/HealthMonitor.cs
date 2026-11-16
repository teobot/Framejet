using System;
using System.Collections.Generic;
using System.Globalization;

namespace Framejet.Core.Diagnostics
{
    /// <summary>One reading of the playback counters. The counts are totals since the backend started.</summary>
    public struct HealthSample
    {
        /// <summary>A monotonic clock, in seconds.</summary>
        public double Seconds;
        public bool VideoRunning;
        /// <summary>The window is minimised, so the presenter skips every frame on purpose.</summary>
        public bool Paused;
        /// <summary>The running mode's frame rate; 0 when unknown.</summary>
        public double ModeFps;
        public long Arrived, Drawn, Dropped;
        public int DeviceResets;
        /// <summary>Frames drawn per second over the last second.</summary>
        public double Fps;
        public bool AudioRunning;
        public int AudioTrims, AudioUnderruns;
    }

    /// <summary>What <see cref="HealthMonitor"/> saw since the last <see cref="HealthMonitor.TakeMinute"/>.</summary>
    public sealed class HealthMinute
    {
        /// <summary>The time covered.</summary>
        public double Seconds { get; set; }
        /// <summary>Frame counts over the seconds that were judged (video running and not minimised).</summary>
        public long Arrived { get; set; }
        public long Drawn { get; set; }
        public long Dropped { get; set; }
        /// <summary>The lowest draw rate in a judged second; 0 when none was judged.</summary>
        public double LowestFps { get; set; }
        public int Hiccups { get; set; }
        public int DeviceResets { get; set; }
        public int AudioTrims { get; set; }
        public int AudioUnderruns { get; set; }
        public int SlowTicks { get; set; }
    }

    /// <summary>
    /// Judges one counter sample a second and names what went wrong in it: the card sent under
    /// <see cref="LowArrival"/> of the mode's frames, over <see cref="HighDrop"/> of the frames were dropped, the
    /// Direct3D device was reset, audio was trimmed or ran dry, or the sample came over <see cref="SlowTickSeconds"/>
    /// after the last. Frames are judged only between two samples with video running, not minimised, at the same mode;
    /// audio only between two with audio running. A total that falls means the backend restarted and counts from 0.
    /// </summary>
    public sealed class HealthMonitor
    {
        public const double LowArrival = 0.9, HighDrop = 0.1, SlowTickSeconds = 2;

        HealthSample _last;
        bool _hasLast;
        HealthMinute _minute = new HealthMinute();

        /// <summary>A <c>hiccup</c> log line for the time since the last sample, or null when it was fine.</summary>
        public string Sample(HealthSample s)
        {
            if (!_hasLast) { _last = s; _hasLast = true; return null; }
            var prev = _last;
            _last = s;
            double elapsed = s.Seconds - prev.Seconds;
            if (elapsed <= 0) return null;
            _minute.Seconds += elapsed;

            var reasons = new List<string>();
            if (elapsed > SlowTickSeconds) { reasons.Add(Invariant($"slow tick {elapsed:0.0} s")); _minute.SlowTicks++; }

            long arrived = Delta(prev.Arrived, s.Arrived), drawn = Delta(prev.Drawn, s.Drawn), dropped = Delta(prev.Dropped, s.Dropped);
            double expected = s.ModeFps * elapsed;
            bool framesJudged = prev.VideoRunning && s.VideoRunning && !prev.Paused && !s.Paused && s.ModeFps > 0 && prev.ModeFps == s.ModeFps;
            if (framesJudged)
            {
                if (arrived < LowArrival * expected) reasons.Add("card sent too few frames");
                if (dropped > HighDrop * arrived) reasons.Add("dropped frames");
                _minute.Arrived += arrived; _minute.Drawn += drawn; _minute.Dropped += dropped;
                if (_minute.LowestFps == 0 || s.Fps < _minute.LowestFps) _minute.LowestFps = s.Fps;
            }
            if (prev.VideoRunning && s.VideoRunning)
            {
                int resets = (int)Delta(prev.DeviceResets, s.DeviceResets);
                if (resets > 0) { reasons.Add("device reset"); _minute.DeviceResets += resets; }
            }
            int trims = 0, underruns = 0;
            if (prev.AudioRunning && s.AudioRunning)
            {
                trims = (int)Delta(prev.AudioTrims, s.AudioTrims);
                underruns = (int)Delta(prev.AudioUnderruns, s.AudioUnderruns);
                if (trims > 0) { reasons.Add("audio trimmed"); _minute.AudioTrims += trims; }
                if (underruns > 0) { reasons.Add("audio underrun"); _minute.AudioUnderruns += underruns; }
            }

            if (reasons.Count == 0) return null;
            _minute.Hiccups++;
            return Invariant($"hiccup ({string.Join(", ", reasons)}): {elapsed:0.00} s, arrived {arrived} (expected {expected:0}), drawn {drawn}, dropped {dropped}, {s.Fps:0.0} fps; audio trims {trims}, underruns {underruns}");
        }

        /// <summary>The totals since the last call, then starts new ones.</summary>
        public HealthMinute TakeMinute()
        {
            var m = _minute;
            _minute = new HealthMinute();
            return m;
        }

        static long Delta(long prev, long cur) => cur >= prev ? cur - prev : cur;

        static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
