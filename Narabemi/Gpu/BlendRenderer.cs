using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Narabemi.Gpu
{
    /// <summary>
    /// D3D11 Compute Shader pipeline for blending two video textures.
    /// Uses CS 5.0 with Texture2D.Load() — no vertex shader, no rasterizer,
    /// no pixel shader, no sampler. Avoids Dynamic-texture sampling driver issues.
    /// Output is R32_Uint (packed BGRA) for direct staging readback.
    /// </summary>
    public sealed class BlendRenderer : IDisposable
    {
        private readonly D3D11DeviceManager _deviceManager;
        private readonly ILogger<BlendRenderer> _logger;

        // Compute shaders
        private ID3D11ComputeShader? _csHorizontal;
        private ID3D11ComputeShader? _csVertical;
        private ID3D11ComputeShader? _csActive;

        // Constant buffer (BlendParams)
        private ID3D11Buffer? _constantBuffer;

        // Output: R32_Uint texture + UAV (CS writes here)
        private ID3D11Texture2D? _outputTex;
        private ID3D11UnorderedAccessView? _outputUav;
        private int _width;
        private int _height;

        // Staging: R32_Uint, CPU-readable copy of output
        private ID3D11Texture2D? _stagingTex;

        // CPU readback buffer (raw BGRA bytes)
        private byte[]? _cpuOutput;
        private readonly object _cpuOutputLock = new();

        // Intermediate Default-usage copies of the mpv Dynamic input textures.
        // Workaround: Dynamic textures cause 4-split artifacts when read by shader
        // on some GPU drivers. CopyResource to Default first.
        private ID3D11Texture2D? _inputCopyA;
        private ID3D11Texture2D? _inputCopyB;
        private ID3D11ShaderResourceView? _inputSrvA;
        private ID3D11ShaderResourceView? _inputSrvB;

        private bool _disposed;

        public byte[]? CpuOutput => _cpuOutput;
        public object CpuOutputLock => _cpuOutputLock;
        public int OutputWidth => _width;
        public int OutputHeight => _height;

        public BlendRenderer(D3D11DeviceManager deviceManager, ILogger<BlendRenderer> logger)
        {
            _deviceManager = deviceManager;
            _logger = logger;
        }

        public void Initialize(int width, int height)
        {
            var device = _deviceManager.Device;

            _csHorizontal = device.CreateComputeShader(LoadShaderBytes("blend_horizontal_cs.cso"));
            _csVertical   = device.CreateComputeShader(LoadShaderBytes("blend_vertical_cs.cso"));
            _csActive     = _csHorizontal;

            var cbSize = (uint)((Marshal.SizeOf<BlendParams>() + 15) & ~15);
            _constantBuffer = device.CreateBuffer(new BufferDescription(
                cbSize,
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

            CreateResources(width, height);
            _logger.LogInformation("BlendRenderer (CS) initialized ({W}x{H})", width, height);
        }

        public void SetMode(BlendMode mode)
        {
            _csActive = mode == BlendMode.Vertical ? _csVertical : _csHorizontal;
        }

        /// <summary>
        /// Copies input textures to intermediate Default R8G8B8A8 textures for CS reading.
        /// Handles single-video mode by duplicating the one texture to both slots.
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
        /// Runs the compute shader blend pass.
        /// Caller must hold ContextLock. Call PrepareInputs() first.
        /// </summary>
        public void Render(BlendParams p)
        {
            if (_csActive is null || _outputTex is null || _outputUav is null) return;

            var ctx = _deviceManager.Context;

            // Update constant buffer
            var mapped = ctx.Map(_constantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(p, mapped.DataPointer, false);
            ctx.Unmap(_constantBuffer!, 0);

            ctx.CSSetShader(_csActive, null, 0);
            ctx.CSSetShaderResources(0, new[] { _inputSrvA!, _inputSrvB! });
            ctx.CSSetConstantBuffers(0, new[] { _constantBuffer! });
            ctx.CSSetUnorderedAccessViews(0, new[] { _outputUav });

            // Dispatch: 16x16 thread groups, one per pixel
            int gx = (_width  + 15) / 16;
            int gy = (_height + 15) / 16;
            ctx.Dispatch((uint)gx, (uint)gy, 1);

            // Unbind CS resources
            ctx.CSSetShader(null, null, 0);
            ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
            ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        }

        /// <summary>
        /// Copies the CS output texture to staging and reads it back to CpuOutput.
        /// Caller must hold ContextLock.
        /// </summary>
        public void ReadBackOutput()
        {
            if (_outputTex is null || _stagingTex is null || _cpuOutput is null) return;

            var ctx = _deviceManager.Context;
            ctx.CopyResource(_stagingTex, _outputTex);

            var mapped = ctx.Map(_stagingTex, 0, MapMode.Read);
            try
            {
                int dstStride = _width * 4;
                lock (_cpuOutputLock)
                {
                    unsafe
                    {
                        fixed (byte* dst = _cpuOutput)
                        {
                            for (int y = 0; y < _height; y++)
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
                ctx.Unmap(_stagingTex, 0);
            }
        }

        private void CreateResources(int width, int height)
        {
            var device = _deviceManager.Device;
            _width  = width;
            _height = height;

            // Output texture: R32_Uint (guaranteed UAV support on all D3D11 hardware)
            // Packed as BGRA uint32 by the CS — matches WriteableBitmap Bgra8888 byte layout.
            _outputTex?.Dispose();
            _outputUav?.Dispose();
            var outDesc = new Texture2DDescription
            {
                Width  = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
            };
            _outputTex = device.CreateTexture2D(outDesc);
            _outputUav = device.CreateUnorderedAccessView(_outputTex, new UnorderedAccessViewDescription
            {
                Format    = Format.R32_UInt,
                ViewDimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 },
            });

            // Staging texture for GPU → CPU readback (same R32_Uint format)
            _stagingTex?.Dispose();
            var stagingDesc = new Texture2DDescription
            {
                Width  = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            _stagingTex = device.CreateTexture2D(stagingDesc);
            _cpuOutput  = new byte[height * width * 4];

            // Intermediate Default copies of the mpv input textures (RGBA format).
            // R8G8B8A8_UNorm matches both the SW render path ("rgba" format) and the
            // GL interop texture format, so CopyResource (same-format) always succeeds.
            _inputSrvA?.Dispose(); _inputCopyA?.Dispose();
            _inputSrvB?.Dispose(); _inputCopyB?.Dispose();
            var inputDesc = new Texture2DDescription
            {
                Width  = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
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
                throw new FileNotFoundException(
                    $"Compiled shader not found: {path}. Run Shaders/compile_shaders.bat first.");
            return File.ReadAllBytes(path);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _inputSrvA?.Dispose(); _inputCopyA?.Dispose();
            _inputSrvB?.Dispose(); _inputCopyB?.Dispose();
            _outputUav?.Dispose();
            _outputTex?.Dispose();
            _stagingTex?.Dispose();
            _constantBuffer?.Dispose();
            _csVertical?.Dispose();
            _csHorizontal?.Dispose();

            _logger.LogInformation("BlendRenderer disposed");
        }
    }
}
