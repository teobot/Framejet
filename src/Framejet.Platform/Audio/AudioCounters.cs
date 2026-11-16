namespace Framejet.Platform.Audio
{
    /// <summary>The audio chain's running totals since it started: cheap enough to poll every second.</summary>
    public struct AudioCounters
    {
        public bool Running;
        /// <summary>Times the latency trimmer dropped buffered audio.</summary>
        public int Trims;
        /// <summary>Times the output asked for audio the buffer didn't have.</summary>
        public int Underruns;
    }
}
