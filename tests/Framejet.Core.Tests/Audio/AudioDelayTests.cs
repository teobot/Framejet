using Framejet.Core.Audio;
using Xunit;

namespace Framejet.Core.Tests.Audio
{
    public class AudioDelayTests
    {
        [Fact]
        public void Totals_sum_the_parts()
        {
            var d = new AudioDelay { CaptureMs = 3, BufferMeanMs = 9.8, BufferP95Ms = 12.1, OutputMs = 6.2 };
            Assert.Equal(19.0, d.TotalMeanMs, 1);
            Assert.Equal(21.3, d.TotalP95Ms, 1);
        }

        [Fact]
        public void Report_line_matches_the_spec_shape()
        {
            var d = new AudioDelay { CaptureMs = 3, BufferMeanMs = 9.8, BufferP95Ms = 12.1, OutputMs = 6.2, Mode = "low-latency", PeriodMs = 2.667, Underruns = 0 };
            Assert.Equal("audio delay: capture 3.0 + buffer 9.8 (p95 12.1) + output 6.2 = 19.0 ms (p95 21.3); mode=low-latency 2.67 ms; underruns=0 (start-up 0)", d.ReportLine());
        }

        [Fact]
        public void Report_line_counts_start_up_underruns_in_the_total()
        {
            var d = new AudioDelay { Mode = "normal", PeriodMs = 10, Underruns = 1, StartupUnderruns = 2 };
            Assert.EndsWith("; mode=normal 10.00 ms; underruns=3 (start-up 2)", d.ReportLine());
        }
    }
}
