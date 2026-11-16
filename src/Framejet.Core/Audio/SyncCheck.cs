using System;
using System.Collections.Generic;
using Framejet.Core.Timing;
using Framejet.Core.Ui;

namespace Framejet.Core.Audio
{
    /// <summary>Gathers flash and beep data while the taps are on.</summary>
    public interface ISyncTaps
    {
        /// <summary>Turns on the every-frame luma tap, the audio block tap and latency measurement.</summary>
        void Begin();

        /// <summary>Turns the taps off and returns what they gathered.</summary>
        (IReadOnlyList<(long Ticks, double Luma)> Frames, IReadOnlyList<(long Ticks, float Peak)> Audio, double AudioDelayMs, double PictureDelayMs) End();
    }

    /// <summary>
    /// A 20 s countdown that gathers flash and beep data through <see cref="ISyncTaps"/>, runs
    /// <see cref="SyncAnalyzer"/> and produces the user-facing text.
    /// </summary>
    public sealed class SyncCheck
    {
        public static readonly TimeSpan Duration = TimeSpan.FromSeconds(20);

        readonly ISyncTaps _taps;
        readonly IScheduler _scheduler;
        readonly long _ticksPerSecond;
        IDisposable _timer;
        int _left;
        bool _running;

        public SyncCheck(ISyncTaps taps, IScheduler scheduler, long ticksPerSecond)
        {
            _taps = taps;
            _scheduler = scheduler;
            _ticksPerSecond = ticksPerSecond;
        }

        public event Action<int> SecondsLeft;

        /// <summary>The headline and the split line. The split is null on failure.</summary>
        public event Action<string, string> Finished;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _left = (int)Duration.TotalSeconds;
            _taps.Begin();
            SecondsLeft?.Invoke(_left);
            Schedule();
        }

        public void Cancel()
        {
            if (!_running) return;
            Stop();
            _taps.End();
        }

        public void SourceStopped()
        {
            if (!_running) return;
            Stop();
            _taps.End();
            Finished?.Invoke(UiText.SyncSourceStopped, null);
        }

        void Schedule() => _timer = _scheduler.After(TimeSpan.FromSeconds(1), Tick);

        void Tick()
        {
            if (!_running) return;
            _left--;
            if (_left > 0)
            {
                SecondsLeft?.Invoke(_left);
                Schedule();
                return;
            }
            Complete();
        }

        void Stop()
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }

        void Complete()
        {
            Stop();
            var data = _taps.End();
            var r = SyncAnalyzer.Analyze(data.Frames, data.Audio, _ticksPerSecond);
            switch (r.Outcome)
            {
                case SyncOutcome.Ok:
                    int card = Round(r.CardGapMs);
                    int audio = Round(data.AudioDelayMs);
                    int picture = Round(data.PictureDelayMs);
                    int total = Round(r.CardGapMs + data.AudioDelayMs - data.PictureDelayMs);
                    Finished?.Invoke(UiText.SyncResult(total, r.Pairs, Round(r.SpreadMs)), UiText.SyncSplit(card, audio, picture));
                    break;
                case SyncOutcome.NoFlashes: Finished?.Invoke(UiText.SyncNoFlashes, null); break;
                case SyncOutcome.NoBeeps: Finished?.Invoke(UiText.SyncNoBeeps, null); break;
                case SyncOutcome.TooFewPairs: Finished?.Invoke(UiText.SyncTooFewPairs, null); break;
                default: Finished?.Invoke(UiText.SyncAmbiguous, null); break;
            }
        }

        static int Round(double ms) => (int)Math.Round(ms, MidpointRounding.AwayFromZero);
    }
}
