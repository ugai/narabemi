// Horizontal split blend — Compute Shader (CS 5.0)
// Reads two B8G8R8A8 input textures via Load() — no sampler, no render pipeline.
// Output is packed as BGRA uint32 (R32_Uint) for direct CPU readback.

Texture2D<float4>  tex0   : register(t0);
Texture2D<float4>  tex1   : register(t1);
RWTexture2D<uint>  output : register(u0);

cbuffer BlendParams : register(b0)
{
    float widthPx;
    float heightPx;
    float ratio;
    float borderWidth;
    float4 borderColor;   // (R,G,B,A) normalized
};

// Pack float4(R,G,B,A) as BGRA uint32 (little-endian: B=byte0, G=byte1, R=byte2, A=byte3)
uint PackBGRA(float4 c)
{
    uint r = (uint)(saturate(c.x) * 255.0f + 0.5f);
    uint g = (uint)(saturate(c.y) * 255.0f + 0.5f);
    uint b = (uint)(saturate(c.z) * 255.0f + 0.5f);
    uint a = (uint)(saturate(c.w) * 255.0f + 0.5f);
    return b | (g << 8) | (r << 16) | (a << 24);
}

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

    output[dtid.xy] = PackBGRA(color);
}
