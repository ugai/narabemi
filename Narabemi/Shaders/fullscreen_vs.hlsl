// Fullscreen quad vertex shader (VS 5.0)
// Draws a screen-filling quad as two triangles (6 vertices, no vertex buffer).
// Uses SV_VertexID to index into explicit position/UV arrays.

struct VS_Output
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VS_Output main(uint vid : SV_VertexID)
{
    // Two triangles forming a fullscreen quad in NDC:
    //   Triangle 1: top-left, top-right, bottom-left
    //   Triangle 2: top-right, bottom-right, bottom-left
    float2 positions[6] =
    {
        float2(-1.0f,  1.0f),   // 0: top-left
        float2( 1.0f,  1.0f),   // 1: top-right
        float2(-1.0f, -1.0f),   // 2: bottom-left
        float2( 1.0f,  1.0f),   // 3: top-right
        float2( 1.0f, -1.0f),   // 4: bottom-right
        float2(-1.0f, -1.0f),   // 5: bottom-left
    };
    // UV: (0,0)=top-left, (1,1)=bottom-right (D3D11 convention)
    float2 uvs[6] =
    {
        float2(0.0f, 0.0f),     // 0: top-left
        float2(1.0f, 0.0f),     // 1: top-right
        float2(0.0f, 1.0f),     // 2: bottom-left
        float2(1.0f, 0.0f),     // 3: top-right
        float2(1.0f, 1.0f),     // 4: bottom-right
        float2(0.0f, 1.0f),     // 5: bottom-left
    };

    VS_Output o;
    o.pos = float4(positions[vid], 0.0f, 1.0f);
    o.uv  = uvs[vid];
    return o;
}
