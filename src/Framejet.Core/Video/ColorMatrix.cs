namespace Framejet.Core.Video
{
    /// <summary>Limited-range YUV to RGB conversion (BT.709 from 720 lines up, BT.601 below).</summary>
    public static class ColorMatrix
    {
        const float Ky = 1.164384f;

        /// <summary>12 floats: rows R, G, B of (cY, cU, cV, offset), with y, u, v as byte/255.</summary>
        public static float[] YuvToRgb(int sourceHeight)
        {
            float rv, gu, gv, bu;
            if (sourceHeight >= 720) { rv = 1.792741f; gu = -0.213249f; gv = -0.532909f; bu = 2.112402f; }
            else { rv = 1.596027f; gu = -0.391762f; gv = -0.812968f; bu = 2.017232f; }
            const float y0 = 16f / 255f, c0 = 128f / 255f;
            return new[]
            {
                Ky, 0f, rv, -Ky * y0 - rv * c0,
                Ky, gu, gv, -Ky * y0 - (gu + gv) * c0,
                Ky, bu, 0f, -Ky * y0 - bu * c0,
            };
        }

        /// <summary>Applies the matrix to one pixel; the result is not clamped.</summary>
        public static (float R, float G, float B) Apply(float[] m, byte y, byte u, byte v)
        {
            float fy = y / 255f, fu = u / 255f, fv = v / 255f;
            return (m[0] * fy + m[1] * fu + m[2] * fv + m[3],
                    m[4] * fy + m[5] * fu + m[6] * fv + m[7],
                    m[8] * fy + m[9] * fu + m[10] * fv + m[11]);
        }
    }
}
