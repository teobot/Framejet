using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;
using Framejet.Core;
using Framejet.Core.Audio;
using Framejet.Core.Video;

namespace Framejet.Platform.Audio
{
    /// <summary>
    /// Takes captured buffers, converts them to float and feeds the playback buffer. A <see cref="DriftController"/>
    /// steers the playback rate to hold the buffer at its target; samples are dropped only after a stall.
    /// Playback reads silence until the buffer reaches the target (prefill), at start and again after a stall.
    /// <para>
    /// The level the controller holds at its target is the <b>lowest buffer level right after a packet landed, over
    /// the last second</b>. With 10 ms packets a 10 ms target therefore means the ring is about empty just before each
    /// packet, as it was before rate control. A packet that lands early, before playback's read, lifts one sample but
    /// not the minimum, so it can't speed playback up at that floor and starve the next read.
    /// </para>
    /// </summary>
    internal sealed class CaptureIngest
    {
        // An underrun raises the target only if capture delivered this recently; a stalled card must not ratchet it.
        static readonly long DeliveringTicks = Stopwatch.Frequency / 10;

        // How far back the controller's level looks for its minimum.
        static readonly long MinWindowTicks = Stopwatch.Frequency;

        // After the gate opens, both streams settle: the output can ask for a catch-up read larger than anything
        // buffered. Underruns this soon after are counted in the stats but don't raise the target.
        public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);
        static readonly long SettleTicks = (long)(Stopwatch.Frequency * SettleTime.TotalSeconds);

        readonly BufferedWaveProvider _buffer;
        readonly WaveFormat _captureFormat;
        readonly DriftController _drift;
        readonly ILog _log;
        readonly UnderrunCountingProvider _output;
        readonly Func<long> _clock;
        readonly object _statsLock = new object();
        readonly byte[] _drain;
        byte[] _floatScratch = new byte[0]; // touched only on the capture thread

        // Buffer level after each packet, newest 6000 (about a minute at 10 ms packets); guarded by _statsLock.
        readonly double[] _levels = new double[6000];
        int _levelCount, _levelNext;

        long _bytes; float _peak; double _buffered, _maxBuffered; int _trimsBase;
        volatile Action<long, float> _blockPeakTap;
        volatile VariableRateResampler _resampler;
        volatile bool _primed;
        long _lastPacket;        // Stopwatch time of the newest packet; written on the capture thread, read on the output thread
        long _primedAt;          // Stopwatch time the prefill gate last opened; same threads
        // Post-packet levels of the last MinWindowTicks, oldest first from _winStart; touched only on the capture thread.
        readonly long[] _winTicks = new long[4096];
        readonly double[] _winLevels = new double[4096];
        int _winStart, _winCount;
        int _pendingUnderruns;   // underruns while capture delivered, not yet passed to the controller
        long _lastBlockStamp = long.MinValue; // touched only on the capture thread

        /// <param name="clock">Stopwatch ticks; tests pass a simulated clock.</param>
        public CaptureIngest(BufferedWaveProvider buffer, WaveFormat captureFormat, DriftController drift, ILog log, Func<long> clock = null)
        {
            _clock = clock ?? Stopwatch.GetTimestamp;
            _buffer = buffer; _captureFormat = captureFormat; _drift = drift; _log = log ?? NullLog.Instance;
            _drain = new byte[buffer.WaveFormat.BlockAlign * 2048]; // whole frames, so a trim never splits one
            _output = new UnderrunCountingProvider(buffer, IsPrimed, OnUnderrun);
        }

        /// <summary>What playback reads: the ring, behind the prefill gate and the underrun count.</summary>
        public IWaveProvider Output => _output;

        public int Underruns => _output.Underruns;

        /// <summary>Receives the controller's ratio after each packet, from the capture thread.</summary>
        public VariableRateResampler Resampler { set => _resampler = value; }

        /// <summary>
        /// While set, called on the capture thread for each 1 ms block of every packet, with the block's peak and the
        /// Stopwatch time its first sample was captured, counted back from the packet's arrival. It must return at once.
        /// </summary>
        public Action<long, float> BlockPeakTap { set => _blockPeakTap = value; }

        public void Process(byte[] data, int count)
        {
            long arrived = _clock();
            if (count == 0) return;
            long previous = Interlocked.Exchange(ref _lastPacket, arrived);
            double elapsedMs = previous == 0 ? 0 : Math.Max(0, (arrived - previous) * 1000.0 / Stopwatch.Frequency);
            int needed = _captureFormat.Encoding == WaveFormatEncoding.IeeeFloat || _captureFormat.BitsPerSample >= 32
                ? count : count * 4 / (_captureFormat.BitsPerSample / 8);
            if (_floatScratch.Length < needed) _floatScratch = new byte[needed];
            int n = PlaythroughChain.ToFloat(data, count, _captureFormat, _floatScratch);

            float peak = 0;
            for (int i = 0; i + 4 <= n; i += 4)
            {
                float a = Math.Abs(BitConverter.ToSingle(_floatScratch, i));
                if (a > peak) peak = a;
            }

            var bf = _buffer.WaveFormat;
            _buffer.AddSamples(_floatScratch, 0, n);
            double preTrimMs = _buffer.BufferedBytes * 1000.0 / bf.AverageBytesPerSecond;

            if (Interlocked.Exchange(ref _pendingUnderruns, 0) > 0)
            {
                _drift.Underrun();
                _log.Info("Audio underrun; buffer target now " + _drift.Target.TotalMilliseconds + " ms");
            }

            bool trimmed = _drift.BytesToTrim(_buffer.BufferedBytes, bf.AverageBytesPerSecond, bf.BlockAlign) > 0;
            if (trimmed)
            {
                // Recompute from the live level on each pass: playback reads concurrently, and a fixed drop
                // worked out up front could take the level below the target.
                int keep = (int)(bf.AverageBytesPerSecond * _drift.Target.TotalSeconds) / bf.BlockAlign * bf.BlockAlign;
                while (true)
                {
                    int excess = (_buffer.BufferedBytes - keep) / bf.BlockAlign * bf.BlockAlign;
                    if (excess <= 0) break;
                    if (_buffer.Read(_drain, 0, Math.Min(excess, _drain.Length)) <= 0) break;
                }
            }

            double ms = _buffer.BufferedBytes * 1000.0 / bf.AverageBytesPerSecond;
            if (trimmed) _log.Info(FormattableString.Invariant($"Audio buffer trimmed from {preTrimMs:0.0} ms to {ms:0.0} ms after a stall"));

            if (!_primed && ms >= _drift.Target.TotalMilliseconds) { _primed = true; Interlocked.Exchange(ref _primedAt, arrived); }
            if (_primed)
            {
                double ratio = _drift.Update(WindowMin(arrived, ms), elapsedMs);
                var resampler = _resampler;
                if (resampler != null) resampler.Ratio = ratio;
            }

            var tap = _blockPeakTap;
            if (tap != null) TapBlocks(tap, n, bf, arrived);
            lock (_statsLock)
            {
                _bytes += count;
                if (peak > _peak) _peak = peak;
                _buffered = ms;
                _levels[_levelNext] = ms;
                _levelNext = (_levelNext + 1) % _levels.Length;
                if (_levelCount < _levels.Length) _levelCount++;
                if (preTrimMs > _maxBuffered) _maxBuffered = preTrimMs;
            }
        }

        /// <summary>Adds this packet's level and returns the lowest of the last second.</summary>
        double WindowMin(long now, double levelMs)
        {
            while (_winCount > 0 && (now - _winTicks[_winStart] > MinWindowTicks || _winCount == _winTicks.Length))
            {
                _winStart = (_winStart + 1) % _winTicks.Length;
                _winCount--;
            }
            int slot = (_winStart + _winCount) % _winTicks.Length;
            _winTicks[slot] = now; _winLevels[slot] = levelMs; _winCount++;
            double min = double.MaxValue;
            for (int i = 0; i < _winCount; i++) min = Math.Min(min, _winLevels[(_winStart + i) % _winLevels.Length]);
            return min;
        }

        bool Delivering => _clock() - Interlocked.Read(ref _lastPacket) <= DeliveringTicks;

        // On the output thread. Once the ring has run dry while the card sends nothing, go back to prefill, so the
        // card's return refills to the target instead of starting at one packet and underrunning.
        bool IsPrimed()
        {
            if (!_primed) return false;
            if (_buffer.BufferedBytes == 0 && !Delivering) { _primed = false; return false; }
            return true;
        }

        // On the output thread.
        void OnUnderrun()
        {
            bool settled = _clock() - Interlocked.Read(ref _primedAt) > SettleTicks;
            if (settled && Delivering) Interlocked.Increment(ref _pendingUnderruns);
        }

        /// <summary>
        /// Splits the packet's <paramref name="bytes"/> of float samples into blocks of about 1 ms (the remainder spread
        /// over them) and reports each block's peak, stamped arrival − (samples from the block's start to the packet's
        /// end) / rate. Callback jitter can put a packet's first block up to a millisecond or so before the previous
        /// packet's last one; such a stamp is held at the previous one, so stamps never go backwards.
        /// </summary>
        void TapBlocks(Action<long, float> tap, int bytes, WaveFormat format, long arrived)
        {
            int frameBytes = 4 * format.Channels;
            int frames = bytes / frameBytes;
            if (frames == 0) return;
            int perMs = Math.Max(1, format.SampleRate / 1000);
            int blocks = Math.Max(1, (frames + perMs / 2) / perMs);
            for (int b = 0; b < blocks; b++)
            {
                int start = (int)((long)b * frames / blocks), end = (int)((long)(b + 1) * frames / blocks);
                float peak = 0;
                for (int i = start * frameBytes; i < end * frameBytes; i += 4)
                {
                    float a = Math.Abs(BitConverter.ToSingle(_floatScratch, i));
                    if (a > peak) peak = a;
                }
                long after = frames - start;
                long stamp = Math.Max(arrived - after * Stopwatch.Frequency / format.SampleRate, _lastBlockStamp);
                _lastBlockStamp = stamp;
                tap(stamp, peak);
            }
        }

        public AudioStats Snapshot(bool reset)
        {
            lock (_statsLock)
            {
                var level = TimingStats.From(new ArraySegment<double>(_levels, 0, _levelCount));
                var s = new AudioStats { BytesCaptured = _bytes, Peak = _peak, BufferedMs = _buffered, MaxBufferedMs = _maxBuffered, Trims = _drift.Trims - _trimsBase,
                    Delay = new AudioDelay { BufferMeanMs = level.MeanMs, BufferP95Ms = level.P95Ms } };
                if (reset) { _bytes = 0; _peak = 0; _maxBuffered = _buffered; _trimsBase = _drift.Trims; _levelCount = 0; _levelNext = 0; }
                return s;
            }
        }
    }
}
