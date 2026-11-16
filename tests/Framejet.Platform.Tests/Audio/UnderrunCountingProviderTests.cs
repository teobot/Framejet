using System;
using NAudio.Wave;
using Framejet.Platform.Audio;
using Xunit;

namespace Framejet.Platform.Tests.Audio
{
    public class UnderrunCountingProviderTests
    {
        static BufferedWaveProvider Ring() =>
            new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { BufferDuration = TimeSpan.FromMilliseconds(500), ReadFully = true };

        [Fact] public void No_count_before_first_audio()
        {
            var p = new UnderrunCountingProvider(Ring());
            p.Read(new byte[3840], 0, 3840);
            Assert.Equal(0, p.Underruns);
        }

        [Fact] public void Counts_a_short_read()
        {
            var ring = Ring(); var p = new UnderrunCountingProvider(ring);
            ring.AddSamples(new byte[1920], 0, 1920);
            p.Read(new byte[3840], 0, 3840);
            Assert.Equal(1, p.Underruns);
        }

        [Fact] public void Does_not_count_a_full_read()
        {
            var ring = Ring(); var p = new UnderrunCountingProvider(ring);
            ring.AddSamples(new byte[3840], 0, 3840);
            p.Read(new byte[3840], 0, 3840);
            Assert.Equal(0, p.Underruns);
        }
    }
}
