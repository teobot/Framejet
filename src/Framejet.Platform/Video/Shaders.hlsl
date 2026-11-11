// Framejet video shaders.
// Pass 1 converts the captured frame to RGB at source size (PS_Nv12, PS_Yuy2, PS_Uyvy, PS_Rgb32).
// Pass 2 scales that picture into the letterboxed viewport (PS_ScaleCatmullRom, PS_ScaleBilinear).

cbuffer Convert : register(b0)
{
    float4 rowR;     // R = dot(rowR, float4(y, u, v, 1)) with y, u, v as byte / 255 (ColorMatrix.YuvToRgb)
    float4 rowG;
    float4 rowB;
    float flipY;     // 1 for bottom-up RGB32
    float3 padding;
};

Texture2D tex0 : register(t0);
Texture2D tex1 : register(t1);
SamplerState linearClamp : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

// One triangle that covers the whole viewport; uv runs from 0 to 1 across it.
VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 YuvToRgb(float y, float u, float v)
{
    float4 yuv = float4(y, u, v, 1);
    return float4(saturate(float3(dot(rowR, yuv), dot(rowG, yuv), dot(rowB, yuv))), 1);
}

// NV12: tex0 is the full-size luma plane (R8), tex1 the half-size interleaved U/V plane (R8G8).
// Chroma is co-sited with the even luma columns (MPEG-2 siting), so it is sampled half a luma pixel right.
float4 PS_Nv12(VSOut i) : SV_Target
{
    uint w, h;
    tex0.GetDimensions(w, h);
    float y = tex0.Load(int3(int2(i.pos.xy), 0)).r;
    float2 c = tex1.Sample(linearClamp, i.uv + float2(0.5 / w, 0)).rg;
    return YuvToRgb(y, c.x, c.y);
}

// YUY2 and UYVY hold two pixels per RGBA texel: (Y0, U, Y1, V) and (U, Y0, V, Y1).
// Chroma belongs to the even pixel, so an odd pixel takes the mean of its two neighbours' chroma.
float4 PS_Yuy2(VSOut i) : SV_Target
{
    uint w, h;
    tex0.GetDimensions(w, h);
    int2 p = int2(i.pos.xy);
    int tx = p.x >> 1;
    float4 a = tex0.Load(int3(tx, p.y, 0));
    float4 b = tex0.Load(int3(min(tx + 1, (int)w - 1), p.y, 0));
    bool odd = (p.x & 1) != 0;
    float2 c = odd ? (a.yw + b.yw) * 0.5 : a.yw;
    return YuvToRgb(odd ? a.z : a.x, c.x, c.y);
}

float4 PS_Uyvy(VSOut i) : SV_Target
{
    uint w, h;
    tex0.GetDimensions(w, h);
    int2 p = int2(i.pos.xy);
    int tx = p.x >> 1;
    float4 a = tex0.Load(int3(tx, p.y, 0));
    float4 b = tex0.Load(int3(min(tx + 1, (int)w - 1), p.y, 0));
    bool odd = (p.x & 1) != 0;
    float2 c = odd ? (a.xz + b.xz) * 0.5 : a.xz;
    return YuvToRgb(odd ? a.w : a.y, c.x, c.y);
}

// RGB32 arrives as BGRA; bottom-up frames store the last picture row first.
float4 PS_Rgb32(VSOut i) : SV_Target
{
    uint w, h;
    tex0.GetDimensions(w, h);
    int2 p = int2(i.pos.xy);
    if (flipY > 0.5) p.y = (int)h - 1 - p.y;
    return float4(tex0.Load(int3(p, 0)).rgb, 1);
}

// 1:1 or downscaling.
float4 PS_ScaleBilinear(VSOut i) : SV_Target
{
    return float4(tex0.Sample(linearClamp, i.uv).rgb, 1);
}

// Upscaling: Catmull-Rom over the 4x4 texels around the sample point, in 9 bilinear taps.
// On each axis the two middle weights are both positive, so one bilinear fetch placed between
// those texels (offset w2 / (w1 + w2)) gives their weighted sum; 3 fetches per axis instead of 4.
float4 PS_ScaleCatmullRom(VSOut i) : SV_Target
{
    uint w, h;
    tex0.GetDimensions(w, h);
    float2 size = float2(w, h);
    float2 pos = i.uv * size;
    float2 centre1 = floor(pos - 0.5) + 0.5;   // centre of texel 1 of the 4x4 grid
    float2 f = pos - centre1;

    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;

    float2 t0 = (centre1 - 1.0) / size;
    float2 t12 = (centre1 + w2 / w12) / size;
    float2 t3 = (centre1 + 2.0) / size;

    float3 c = 0;
    c += tex0.SampleLevel(linearClamp, float2(t0.x, t0.y), 0).rgb * (w0.x * w0.y);
    c += tex0.SampleLevel(linearClamp, float2(t12.x, t0.y), 0).rgb * (w12.x * w0.y);
    c += tex0.SampleLevel(linearClamp, float2(t3.x, t0.y), 0).rgb * (w3.x * w0.y);
    c += tex0.SampleLevel(linearClamp, float2(t0.x, t12.y), 0).rgb * (w0.x * w12.y);
    c += tex0.SampleLevel(linearClamp, float2(t12.x, t12.y), 0).rgb * (w12.x * w12.y);
    c += tex0.SampleLevel(linearClamp, float2(t3.x, t12.y), 0).rgb * (w3.x * w12.y);
    c += tex0.SampleLevel(linearClamp, float2(t0.x, t3.y), 0).rgb * (w0.x * w3.y);
    c += tex0.SampleLevel(linearClamp, float2(t12.x, t3.y), 0).rgb * (w12.x * w3.y);
    c += tex0.SampleLevel(linearClamp, float2(t3.x, t3.y), 0).rgb * (w3.x * w3.y);
    return float4(saturate(c), 1);
}
