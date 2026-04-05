// Horizontal split blend shader (PS 5.0)
// Ports Narabemi.Wpf/Shaders/blend.hlsl from PS 2.0 to D3D11 PS 5.0.
// Shows texture0 on the left and texture1 on the right, split at `ratio`.

Texture2D    tex0     : register(t0);
Texture2D    tex1     : register(t1);
SamplerState sampler0 : register(s0);

cbuffer BlendParams : register(b0)
{
    float widthPx;
    float heightPx;
    float ratio;
    float borderWidth;
    float4 borderColor;
};

float4 main(float2 uv : TEXCOORD0) : SV_Target
{
    float4 color0 = tex0.Sample(sampler0, uv);
    float4 color1 = tex1.Sample(sampler0, uv);
    float4 color  = lerp(color0, color1, step(ratio, uv.x));

    // Border: lerp toward borderColor when within borderWidth/2 of the split line
    float halfBorder = (borderWidth / widthPx) * 0.5f;
    float inBorder = step(uv.x - halfBorder, ratio) * step(ratio, uv.x + halfBorder);
    return lerp(color, borderColor, inBorder);
}
