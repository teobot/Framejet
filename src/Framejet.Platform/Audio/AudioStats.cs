namespace Framejet.Platform.Audio
{
    public sealed class AudioStats
    {
        public long BytesCaptured { get; set; }
        public float Peak { get; set; }
        public double BufferedMs { get; set; }
        /// <summary>Highest buffer level right after adding captured audio, before trimming: the worst delay a listener could hear.</summary>
        public double MaxBufferedMs { get; set; }
        public int Trims { get; set; }
    }
}
