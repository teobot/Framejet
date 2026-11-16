using NAudio.Wave;

namespace Framejet.Platform.Audio
{
    /// <summary>
    /// Reads from the playback ring buffer and counts the reads it could not fill from audio already buffered.
    /// Counting starts once the buffer has held audio, so the wait for the first packet is not an underrun.
    /// </summary>
    internal sealed class UnderrunCountingProvider : IWaveProvider
    {
        readonly BufferedWaveProvider _source;
        int _underruns;
        bool _seenAudio;

        public UnderrunCountingProvider(BufferedWaveProvider source) { _source = source; }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Underruns => System.Threading.Volatile.Read(ref _underruns);

        public int Read(byte[] buffer, int offset, int count)
        {
            int buffered = _source.BufferedBytes;
            if (buffered > 0) _seenAudio = true;
            if (_seenAudio && buffered < count) System.Threading.Interlocked.Increment(ref _underruns);
            return _source.Read(buffer, offset, count);
        }
    }
}
