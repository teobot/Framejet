using System.Globalization;

namespace Framejet.Core.Video
{
    public sealed class DelayVerdict
    {
        public DelayVerdict(bool pass, string reason) { Pass = pass; Reason = reason; }
        public bool Pass { get; }
        public string Reason { get; }
    }

    /// <summary>Decides whether the measured frame timings show no added delay.</summary>
    public static class DelayCheck
    {
        public const double MaxArrivalToPresentP95Ms = 2.5;
        public const double SlackMs = 1.5;

        static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        static DelayVerdict Fail(string reason) => new DelayVerdict(false, reason);

        public static DelayVerdict Evaluate(TimingStats arrivalToPresent, TimingStats presentToScreen, double refreshHz)
        {
            double refresh = 1000.0 / refreshHz;
            if (arrivalToPresent.Count == 0 || presentToScreen.Count == 0) return Fail("no frames measured");
            if (arrivalToPresent.P95Ms > MaxArrivalToPresentP95Ms)
                return Fail($"arrival → Present p95 {F(arrivalToPresent.P95Ms)} ms > 2.5 ms");
            double meanLimit = refresh / 2 + SlackMs;
            if (presentToScreen.MeanMs > meanLimit)
                return Fail($"Present → screen mean {F(presentToScreen.MeanMs)} ms > {F(meanLimit)} ms");
            double p95Limit = refresh + SlackMs;
            if (presentToScreen.P95Ms > p95Limit)
                return Fail($"Present → screen p95 {F(presentToScreen.P95Ms)} ms > {F(p95Limit)} ms (a frame waited an extra refresh)");
            return new DelayVerdict(true, "");
        }
    }
}
