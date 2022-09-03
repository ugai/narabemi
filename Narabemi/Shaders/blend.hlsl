// Blend shader 

sampler2D input0 : register(s0);
sampler2D input1 : register(s1);
float widthPx : register(c0);
float heightPx : register(c1);
float ratio : register(c2);
float borderWidth : register(c3);
float4 borderColor : register(c4);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color0 = tex2D(input0, uv);
    float4 color1 = tex2D(input1, uv);
    float4 color = lerp(color0, color1, step(ratio, uv.x));
    return lerp(color, borderColor,
        step(uv.x - (borderWidth / widthPx / 2.0f), ratio) *
        step(ratio, uv.x + borderWidth / widthPx / 2.0f));
}
