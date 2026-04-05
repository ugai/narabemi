using System.Numerics;
using System.Runtime.InteropServices;

namespace Narabemi.Gpu
{
    /// <summary>
    /// CPU-side mirror of the HLSL cbuffer BlendParams.
    /// Must be 16-byte aligned for D3D11 constant buffer upload.
    /// Layout: widthPx, heightPx, ratio, borderWidth (4 floats = 16 bytes) + borderColor (float4 = 16 bytes)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct BlendParams
    {
        public float WidthPx;
        public float HeightPx;
        public float Ratio;
        public float BorderWidth;
        public Vector4 BorderColor; // RGBA float4

        public static BlendParams Default(int width, int height) => new()
        {
            WidthPx = width,
            HeightPx = height,
            Ratio = 0.5f,
            BorderWidth = 1.0f,
            BorderColor = Vector4.One, // white
        };
    }
}
