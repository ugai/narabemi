// Horizontal split blend — Compute Shader (CS 5.0)
// Reads two R8G8B8A8 input textures via Load() — no sampler, no render pipeline.
// Output is B8G8R8A8_UNorm; D3D11 UAV auto-swizzles RGBA write → BGRA memory,
// matching WriteableBitmap.Bgra8888 byte layout for CPU readback.

Texture2D<float4>        tex0   : register(t0);
Texture2D<float4>        tex1   : register(t1);
RWTexture2D<unorm float4> output : register(u0);

cbuffer BlendParams : register(b0)
{
    float widthPx;
    float heightPx;
    float ratio;
    float borderWidth;
    float4 borderColor;   // (R,G,B,A) normalized
};

[numthreads(16, 16, 1)]
void main(uint3 dtid : SV_DispatchThreadID)
{
    uint x = dtid.x;
    uint y = dtid.y;
    if (x >= (uint)widthPx || y >= (uint)heightPx)
        return;

    // Direct texel fetch — no sampler involved
    float4 c0 = tex0.Load(int3(x, y, 0));
    float4 c1 = tex1.Load(int3(x, y, 0));

    float splitX    = ratio * widthPx;
    float halfBorder = borderWidth * 0.5f;
    float fx = (float)x;

    float4 color = (fx < splitX) ? c0 : c1;

    // Border overlay
    if (abs(fx - splitX) <= halfBorder)
        color = borderColor;

    output[dtid.xy] = color;
}
