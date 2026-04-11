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
        private ID3D11Texture2D? _stagingTexture;
        private byte[]? _cpuOutput;
        private readonly object _cpuOutputLock = new();

        // Intermediate Default-usage copies of the Dynamic input textures.
        // Dynamic textures cannot be sampled correctly by the shader on some drivers
        // due to internal memory layout differences. We CopyResource to Default first.
        private ID3D11Texture2D? _inputCopyA;
        private ID3D11Texture2D? _inputCopyB;
        private ID3D11ShaderResourceView? _inputSrvA;
        private ID3D11ShaderResourceView? _inputSrvB;

        private bool _disposed;

        public GpuTexture? OutputTexture => _outputTexture;

        /// <summary>CPU-readable copy of the last blend result (BGRA). Lock on CpuOutputLock before reading.</summary>
        public byte[]? CpuOutput => _cpuOutput;
        public object CpuOutputLock => _cpuOutputLock;
        public int OutputWidth => _outputTexture?.Width ?? 0;
        public int OutputHeight => _outputTexture?.Height ?? 0;

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
        /// Copies Dynamic input textures to intermediate Default textures for shader sampling.
        /// Caller must hold ContextLock.
        /// </summary>
        public void PrepareInputs(ID3D11Texture2D? dynA, ID3D11Texture2D? dynB)
        {
            var ctx = _deviceManager.Context;
            var effectiveA = dynA ?? dynB!;
            var effectiveB = dynB ?? dynA!;
            ctx.CopyResource(_inputCopyA!, effectiveA);
            ctx.CopyResource(_inputCopyB!, effectiveB);
        }

        /// <summary>
        /// Renders the blend pass using the pre-copied Default input textures.
        /// Caller must hold ContextLock. Call PrepareInputs() first.
        /// </summary>
        public void Render(BlendParams p)
        {
            if (_outputRtv is null || _vs is null || _psActive is null || _outputTexture is null) return;
            if (_inputSrvA is null || _inputSrvB is null) return;

            var ctx = _deviceManager.Context;

            // Update constant buffer
            var mapped = ctx.Map(_constantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(p, mapped.DataPointer, false);
            ctx.Unmap(_constantBuffer!, 0);

            // IA: no vertex buffer
            ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);

            // VS + PS
            ctx.VSSetShader(_vs, null, 0);
            ctx.PSSetShader(_psActive, null, 0);

            // SRVs from Default (not Dynamic) textures
            ctx.PSSetShaderResources(0, new[] { _inputSrvA, _inputSrvB });
            ctx.PSSetSamplers(0, new[] { _sampler! });
            ctx.PSSetConstantBuffers(0, new[] { _constantBuffer! });

            // OM + Viewport
            ctx.OMSetRenderTargets(new[] { _outputRtv }, null);
            ctx.RSSetViewports(new[] { new Viewport(0, 0, _outputTexture.Width, _outputTexture.Height) });

            // Draw fullscreen quad (6 vertices, two triangles, no VB)
            ctx.Draw(6, 0);

            // Unbind to avoid validation warnings
            ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
            ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        }

        /// <summary>
        /// Copies the GPU blend output to a staging texture and reads it back to CpuOutput.
        /// Must be called inside ContextLock.
        /// </summary>
        public void ReadBackOutput()
        {
            if (_outputTexture is null || _stagingTexture is null || _cpuOutput is null) return;

            var ctx = _deviceManager.Context;

            // GPU → staging copy
            ctx.CopyResource(_stagingTexture, _outputTexture.Texture);

            // staging → CPU read
            var mapped = ctx.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                int w = _outputTexture.Width;
                int h = _outputTexture.Height;
                int dstStride = w * 4;
                lock (_cpuOutputLock)
                {
                    unsafe
                    {
                        fixed (byte* dst = _cpuOutput)
                        {
                            for (int y = 0; y < h; y++)
                            {
                                Buffer.MemoryCopy(
                                    (void*)(mapped.DataPointer + (long)y * mapped.RowPitch),
                                    dst + (long)y * dstStride,
                                    dstStride, dstStride);
                            }
                        }
                    }
                }
            }
            finally
            {
                ctx.Unmap(_stagingTexture, 0);
            }
        }

        private void CreateOutputResources(int width, int height)
        {
            var device = _deviceManager.Device;

            // Output render target (Default, no Shared flag needed — WGL interop removed)
            var outDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            var outTex = device.CreateTexture2D(outDesc);
            var outSrv = device.CreateShaderResourceView(outTex);
            _outputTexture = new GpuTexture(outTex, outSrv, width, height, _logger);

            var rtvDesc = new RenderTargetViewDescription
            {
                Format = Format.B8G8R8A8_UNorm,
                ViewDimension = RenderTargetViewDimension.Texture2D,
                Texture2D = new Texture2DRenderTargetView { MipSlice = 0 },
            };
            _outputRtv = device.CreateRenderTargetView(_outputTexture.Texture, rtvDesc);

            // Staging texture for GPU → CPU readback
            _stagingTexture?.Dispose();
            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            _stagingTexture = device.CreateTexture2D(stagingDesc);
            _cpuOutput = new byte[height * width * 4];

            // Intermediate Default-usage copies of input Dynamic textures.
            // Workaround: Dynamic textures cannot be sampled correctly by the shader
            // on some GPU drivers due to internal tiled vs. linear layout mismatch.
            _inputSrvA?.Dispose(); _inputCopyA?.Dispose();
            _inputSrvB?.Dispose(); _inputCopyB?.Dispose();
            var inputDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };
            _inputCopyA = device.CreateTexture2D(inputDesc);
            _inputSrvA  = device.CreateShaderResourceView(_inputCopyA);
            _inputCopyB = device.CreateTexture2D(inputDesc);
            _inputSrvB  = device.CreateShaderResourceView(_inputCopyB);
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

            _inputSrvA?.Dispose(); _inputCopyA?.Dispose();
            _inputSrvB?.Dispose(); _inputCopyB?.Dispose();
            _stagingTexture?.Dispose();
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
