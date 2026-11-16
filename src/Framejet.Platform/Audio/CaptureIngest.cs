using System;
using System.Diagnostics;
using NAudio.Wave;
using Framejet.Core.Audio;
using Framejet.Core.Video;

namespace Framejet.Platform.Audio
{
    /// <summary>Takes captured buffers, converts them to float, feeds the playback buffer and trims latency.</summary>
    internal sealed class CaptureIngest
    {
        readonly BufferedWaveProvider _buffer;
        readonly WaveFormat _captureFormat;
        readonly LatencyTrimmer _trimmer;
        readonly object _statsLock = new object();
        readonly byte[] _drain = new byte[16384];
        byte[] _floatScratch = new byte[0]; // touched only on the capture thread

        // Buffer level after each packet, newest 6000 (about a minute at 10 ms packets); guarded by _statsLock.
        readonly double[] _levels = new double[6000];
        int _levelCount, _levelNext;

        long _bytes; float _peak; double _buffered, _maxBuffered; int _trimsBase;
        volatile Action<long, float> _blockPeakTap;
        long _lastBlockStamp = long.MinValue; // touched only on the capture thread

        public CaptureIngest(BufferedWaveProvider buffer, WaveFormat captureFormat, LatencyTrimmer trimmer)
        {
            _buffer = buffer; _captureFormat = captureFormat; _trimmer = trimmer;
        }

        /// <summary>
        /// While set, called on the capture thread for each 1 ms block of every packet, with the block's peak and the
        /// Stopwatch time its first sample was captured, counted back from the packet's arrival. It must return at once.
        /// </summary>
        public Action<long, float> BlockPeakTap { set => _blockPeakTap = value; }

        public void Process(byte[] data, int count)
        {
            long arrived = Stopwatch.GetTimestamp();
            if (count == 0) return;
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
            var tap = _blockPeakTap;
            if (tap != null) TapBlocks(tap, n, bf, arrived);

            _buffer.AddSamples(_floatScratch, 0, n);
            double preTrimMs = _buffer.BufferedBytes * 1000.0 / bf.AverageBytesPerSecond;

            int drop = _trimmer.BytesToDrop(_buffer.BufferedBytes, bf.AverageBytesPerSecond, bf.BlockAlign);
            while (drop > 0)
            {
                int read = _buffer.Read(_drain, 0, Math.Min(drop, _drain.Length));
                if (read <= 0) break;
                drop -= read;
            }

            double ms = _buffer.BufferedBytes * 1000.0 / bf.AverageBytesPerSecond;
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
                var s = new AudioStats { BytesCaptured = _bytes, Peak = _peak, BufferedMs = _buffered, MaxBufferedMs = _maxBuffered, Trims = _trimmer.Trims - _trimsBase,
                    Delay = new AudioDelay { BufferMeanMs = level.MeanMs, BufferP95Ms = level.P95Ms } };
                if (reset) { _bytes = 0; _peak = 0; _maxBuffered = _buffered; _trimsBase = _trimmer.Trims; _levelCount = 0; _levelNext = 0; }
                return s;
            }
        }
    }
}
