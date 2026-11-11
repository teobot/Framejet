namespace Framejet.Core.Audio
{
    public static class VolumeCurve
    {
        public static float ToGain(float slider)
        {
            float s = slider < 0f ? 0f : slider > 1f ? 1f : slider;
            return s * s;
        }
    }
}
