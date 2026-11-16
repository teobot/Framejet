using System;
using NAudio.Wave;

namespace Framejet.Platform.Audio
{
    /// <summary>
    /// Reads from the playback ring buffer and counts the reads it could not fill from audio already buffered.
    /// Counting starts once the buffer has held audio, so the wait for the first packet is not an underrun.
    /// While <c>primed</c> says no, reads return silence without touching the ring and are not counted (prefill).
    /// </summary>
    internal sealed class UnderrunCountingProvider : IWaveProvider
    {
        readonly BufferedWaveProvider _source;
        readonly Func<bool> _primed;
        readonly Action _underrun;
        int _underruns;
        bool _seenAudio;

        /// <param name="primed">Whether playback may take from the ring yet; null means always.</param>
        /// <param name="underrun">Called on the reading thread after each counted underrun; it must return at once.</param>
        public UnderrunCountingProvider(BufferedWaveProvider source, Func<bool> primed = null, Action underrun = null)
        {
            _source = source; _primed = primed; _underrun = underrun;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Underruns => System.Threading.Volatile.Read(ref _underruns);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_primed != null && !_primed())
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            int buffered = _source.BufferedBytes;
            if (buffered > 0) _seenAudio = true;
            if (_seenAudio && buffered < count)
            {
                System.Threading.Interlocked.Increment(ref _underruns);
                _underrun?.Invoke();
            }
            return _source.Read(buffer, offset, count);
        }
    }
}
