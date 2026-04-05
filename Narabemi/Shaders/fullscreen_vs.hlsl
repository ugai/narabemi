// Fullscreen triangle vertex shader (VS 5.0)
// Generates a full-screen triangle from SV_VertexID — no vertex buffer required.
// Draw with 3 vertices and no index buffer.

struct VS_Output
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VS_Output main(uint vid : SV_VertexID)
{
    // Triangle that covers NDC [-1,1]x[-1,1]:
    //   vid=0: (-1,-1) uv=(0,1)
    //   vid=1: (-1, 3) uv=(0,-1)
    //   vid=2: ( 3,-1) uv=(2,1)
    VS_Output o;
    o.uv  = float2((vid << 1) & 2, vid & 2);
    o.pos = float4(o.uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    return o;
}
