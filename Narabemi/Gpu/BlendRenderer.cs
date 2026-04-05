using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Narabemi.Gpu
{
    /// <summary>
    /// D3D11 pixel shader pipeline for blending two video textures.
    /// Renders two input SRVs into a shared output texture using a fullscreen triangle.
    /// Supports runtime mode switching between horizontal and vertical split.
    /// </summary>
    public sealed class BlendRenderer : IDisposable
    {
        private readonly D3D11DeviceManager _deviceManager;
        private readonly ILogger<BlendRenderer> _logger;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psHorizontal;
        private ID3D11PixelShader? _psVertical;
        private ID3D11PixelShader? _psActive;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _constantBuffer;
        private ID3D11RenderTargetView? _outputRtv;
        private GpuTexture? _outputTexture;

        private bool _disposed;

        public GpuTexture? OutputTexture => _outputTexture;

        public BlendRenderer(D3D11DeviceManager deviceManager, ILogger<BlendRenderer> logger)
        {
            _deviceManager = deviceManager;
            _logger = logger;
        }

        public void Initialize(int width, int height)
        {
            var device = _deviceManager.Device;

            _vs = device.CreateVertexShader(LoadShaderBytes("fullscreen_vs.cso"));
            _psHorizontal = device.CreatePixelShader(LoadShaderBytes("blend_horizontal.cso"));
            _psVertical = device.CreatePixelShader(LoadShaderBytes("blend_vertical.cso"));
            _psActive = _psHorizontal;

            var samplerDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
            };
            _sampler = device.CreateSamplerState(samplerDesc);

            // Constant buffer size must be a multiple of 16 bytes
            var cbSize = (uint)((Marshal.SizeOf<BlendParams>() + 15) & ~15);
            _constantBuffer = device.CreateBuffer(new BufferDescription(
                cbSize,
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

            CreateOutputResources(width, height);
            _logger.LogInformation("BlendRenderer initialized ({W}x{H})", width, height);
        }

        public void SetMode(BlendMode mode)
        {
            _psActive = mode == BlendMode.Vertical ? _psVertical : _psHorizontal;
        }

        public void Resize(int width, int height)
        {
            _outputRtv?.Dispose();
            _outputTexture?.Dispose();
            CreateOutputResources(width, height);
        }

        /// <summary>
        /// Renders the blend pass. Must be called from the D3D11 thread.
        /// </summary>
        public void Render(ID3D11ShaderResourceView srvA, ID3D11ShaderResourceView srvB, BlendParams p)
        {
            if (_outputRtv is null || _vs is null || _psActive is null || _outputTexture is null) return;

            var ctx = _deviceManager.Context;

            // Update constant buffer via Map/Unmap (dynamic usage, write-discard)
            var mapped = ctx.Map(_constantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(p, mapped.DataPointer, false);
            ctx.Unmap(_constantBuffer!, 0);

            // IA: no vertex buffer, fullscreen triangle
            ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);

            // VS + PS
            ctx.VSSetShader(_vs, null, 0);
            ctx.PSSetShader(_psActive, null, 0);

            // SRVs: bind srvA to slot 0, srvB to slot 1
            ctx.PSSetShaderResources(0, new[] { srvA, srvB });

            // Sampler slot 0
            ctx.PSSetSamplers(0, new[] { _sampler! });

            // Constant buffer slot 0
            ctx.PSSetConstantBuffers(0, new[] { _constantBuffer! });

            // OM: single RTV, no depth
            ctx.OMSetRenderTargets(new[] { _outputRtv }, null);

            // Viewport
            ctx.RSSetViewports(new[] { new Viewport(0, 0, _outputTexture.Width, _outputTexture.Height) });

            // Draw fullscreen triangle (3 vertices, no VB)
            ctx.Draw(3, 0);

            // Unbind SRVs and RTV to avoid validation warnings
            ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
            ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        }

        private void CreateOutputResources(int width, int height)
        {
            var device = _deviceManager.Device;
            _outputTexture = _deviceManager.CreateOutputTexture(width, height);

            var rtvDesc = new RenderTargetViewDescription
            {
                Format = Format.B8G8R8A8_UNorm,
                ViewDimension = RenderTargetViewDimension.Texture2D,
                Texture2D = new Texture2DRenderTargetView { MipSlice = 0 },
            };
            _outputRtv = device.CreateRenderTargetView(_outputTexture.Texture, rtvDesc);
        }

        private static byte[] LoadShaderBytes(string filename)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Shaders", filename);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Compiled shader not found: {path}. Run Shaders/compile_shaders.bat first.");
            return File.ReadAllBytes(path);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _outputRtv?.Dispose();
            _outputTexture?.Dispose();
            _constantBuffer?.Dispose();
            _sampler?.Dispose();
            _psVertical?.Dispose();
            _psHorizontal?.Dispose();
            _vs?.Dispose();

            _logger.LogInformation("BlendRenderer disposed");
        }
    }
}
