// Vertical split blend — Compute Shader (CS 5.0)
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
    float4 borderColor;
};

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

    float4 c0 = tex0.Load(int3(x, y, 0));
    float4 c1 = tex1.Load(int3(x, y, 0));

    float splitY     = ratio * heightPx;
    float halfBorder = borderWidth * 0.5f;
    float fy = (float)y;

    float4 color = (fy < splitY) ? c0 : c1;

    if (abs(fy - splitY) <= halfBorder)
        color = borderColor;

    output[dtid.xy] = PackBGRA(color);
}
