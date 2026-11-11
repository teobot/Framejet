using System;
using NAudio.Wave;
using Framejet.Core.Audio;

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

        long _bytes; float _peak; double _buffered, _maxBuffered; int _trimsBase;

        public CaptureIngest(BufferedWaveProvider buffer, WaveFormat captureFormat, LatencyTrimmer trimmer)
        {
            _buffer = buffer; _captureFormat = captureFormat; _trimmer = trimmer;
        }

        public void Process(byte[] data, int count)
        {
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

            _buffer.AddSamples(_floatScratch, 0, n);
            var bf = _buffer.WaveFormat;
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
                if (preTrimMs > _maxBuffered) _maxBuffered = preTrimMs;
            }
        }

        public AudioStats Snapshot(bool reset)
        {
            lock (_statsLock)
            {
                var s = new AudioStats { BytesCaptured = _bytes, Peak = _peak, BufferedMs = _buffered, MaxBufferedMs = _maxBuffered, Trims = _trimmer.Trims - _trimsBase };
                if (reset) { _bytes = 0; _peak = 0; _maxBuffered = _buffered; _trimsBase = _trimmer.Trims; }
                return s;
            }
        }
    }
}
