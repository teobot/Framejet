using System.Globalization;

namespace Framejet.Core.Audio
{
    /// <summary>The audio path's delay, split into capture, buffer and output parts.</summary>
    public sealed class AudioDelay
    {
        public double CaptureMs { get; set; }
        public double BufferMeanMs { get; set; }
        public double BufferP95Ms { get; set; }
        public double OutputMs { get; set; }

        /// <summary>"normal", "low-latency" (both ends), or "low-latency capture" / "low-latency output" (one end).</summary>
        public string Mode { get; set; }

        public double PeriodMs { get; set; }
        /// <summary>Underruns since the stats were last reset.</summary>
        public int Underruns { get; set; }

        /// <summary>Underruns before the stats were last reset, since playback started: for the self-test, its start-up.</summary>
        public int StartupUnderruns { get; set; }

        public double TotalMeanMs => CaptureMs + BufferMeanMs + OutputMs;
        public double TotalP95Ms => CaptureMs + BufferP95Ms + OutputMs;

        public string ReportLine()
        {
            var c = CultureInfo.InvariantCulture;
            return "audio delay: capture " + CaptureMs.ToString("0.0", c)
                + " + buffer " + BufferMeanMs.ToString("0.0", c)
                + " (p95 " + BufferP95Ms.ToString("0.0", c) + ")"
                + " + output " + OutputMs.ToString("0.0", c)
                + " = " + TotalMeanMs.ToString("0.0", c)
                + " ms (p95 " + TotalP95Ms.ToString("0.0", c) + ")"
                + "; mode=" + Mode + " " + PeriodMs.ToString("0.00", c)
                + " ms; underruns=" + (Underruns + StartupUnderruns).ToString(c)
                + " (start-up " + StartupUnderruns.ToString(c) + ")";
        }
    }
}
