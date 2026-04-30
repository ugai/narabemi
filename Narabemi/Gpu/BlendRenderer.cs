using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Narabemi.Gpu
{
    /// <summary>
    /// D3D11 Compute Shader pipeline for blending two video textures.
    /// Uses CS 5.0 with Texture2D.Load() — no vertex shader, no rasterizer,
    /// no pixel shader, no sampler. Avoids Dynamic-texture sampling driver issues.
    /// Output is B8G8R8A8_UNorm for swap chain CopyResource and CPU readback.
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

        // Output: B8G8R8A8_UNorm texture + UAV (CS writes here)
        private ID3D11Texture2D? _outputTex;
        private ID3D11UnorderedAccessView? _outputUav;
        private int _width;
        private int _height;

        // Staging: double-buffered B8G8R8A8_UNorm textures for async GPU→CPU readback.
        // BeginReadBack() copies output → _stagingBack, then swaps references.
        // EndReadBack(stagingRef) maps the returned reference — race-free because
        // the caller captures the reference before releasing ContextLock.
        private ID3D11Texture2D? _stagingTex;     // front (ready to Map)
        private ID3D11Texture2D? _stagingTexBack; // back  (GPU is writing here)

        // CPU readback buffer (raw BGRA bytes)
        private byte[]? _cpuOutput;
        private readonly object _cpuOutputLock = new();

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
        /// Runs the compute shader blend pass with the provided SRVs.
        /// srvA and srvB are the mpv output texture SRVs (R8G8B8A8_UNorm, Default usage).
        /// WGL interop unlock must have occurred before this call (satisfied by ContextLock ordering).
        /// Caller must hold ContextLock.
        /// </summary>
        public void Render(ID3D11ShaderResourceView srvA, ID3D11ShaderResourceView srvB, BlendParams p)
        {
            if (_csActive is null || _outputTex is null || _outputUav is null) return;

            var ctx = _deviceManager.Context;

            // Update constant buffer
            var mapped = ctx.Map(_constantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(p, mapped.DataPointer, false);
            ctx.Unmap(_constantBuffer!, 0);

            ctx.CSSetShader(_csActive, null, 0);
            ctx.CSSetShaderResources(0, new[] { srvA, srvB });
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
        /// Phase 1 of double-buffered readback (call inside ContextLock).
        /// Copies CS output → staging back buffer, then swaps front/back references.
        /// Returns the captured texture reference — pass this to EndReadBack(), do NOT
        /// read the _stagingTex field after releasing ContextLock.
        /// </summary>
        public ID3D11Texture2D? BeginReadBack()
        {
            if (_outputTex is null || _stagingTexBack is null) return null;
            var ctx = _deviceManager.Context;
            ctx.CopyResource(_stagingTexBack, _outputTex);
            (_stagingTex, _stagingTexBack) = (_stagingTexBack, _stagingTex);
            return _stagingTex;
        }

        /// <summary>
        /// Phase 2 of double-buffered readback. Maps the texture reference captured by
        /// BeginReadBack() and copies to CpuOutput.
        /// Caller must hold ContextLock to satisfy Map's thread-safety requirement.
        /// mapMs: time for Map (GPU fence wait). memcpyMs: time for CPU memcpy.
        /// </summary>
        public void EndReadBack(ID3D11Texture2D staging, out long mapMs, out long memcpyMs)
        {
            mapMs = 0;
            memcpyMs = 0;
            if (_cpuOutput is null) return;
            var ctx = _deviceManager.Context;
            var sw = Stopwatch.StartNew();
            var mapped = ctx.Map(staging, 0, MapMode.Read);
            mapMs = sw.ElapsedMilliseconds;
            sw.Restart();
            try
            {
                CopyStagingToCpu(mapped);
                memcpyMs = sw.ElapsedMilliseconds;
            }
            finally
            {
                ctx.Unmap(staging, 0);
            }
        }

        /// <summary>
        /// Split-lock Phase 2 — step 1: Map staging for CPU read.
        /// Caller must hold ContextLock. Returns the mapped pointer (valid until EndStagingRead).
        /// </summary>
        public MappedSubresource BeginStagingRead(ID3D11Texture2D staging)
            => _deviceManager.Context.Map(staging, 0, MapMode.Read);

        /// <summary>
        /// Split-lock Phase 2 — step 2: copy mapped pointer to CpuOutput.
        /// No D3D11 context calls — safe to call WITHOUT ContextLock.
        /// Call between BeginStagingRead and EndStagingRead.
        /// </summary>
        public void CopyStagingToCpu(MappedSubresource mapped)
        {
            if (_cpuOutput is null) return;
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

        /// <summary>
        /// Split-lock Phase 2 — step 3: unmap staging texture.
        /// Caller must hold ContextLock.
        /// </summary>
        public void EndStagingRead(ID3D11Texture2D staging)
            => _deviceManager.Context.Unmap(staging, 0);

        /// <summary>
        /// Non-blocking readback attempt. Returns true if the Map succeeded (GPU done) and
        /// data was copied to CpuOutput; returns false if the GPU is still writing
        /// (DXGI_ERROR_WAS_STILL_DRAWING). Caller must hold ContextLock.
        /// </summary>
        public bool TryEndReadBack(ID3D11Texture2D staging)
        {
            if (_cpuOutput is null) return false;
            var ctx = _deviceManager.Context;
            Result r = ctx.Map(staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped);
            if (r.Code == unchecked((int)0x887A000A)) return false; // WAS_STILL_DRAWING
            r.CheckError(); // throws on other errors
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
                ctx.Unmap(staging, 0);
            }
            return true;
        }

        /// <summary>
        /// Single-call readback for callers that hold ContextLock for the full duration.
        /// Caller must hold ContextLock.
        /// </summary>
        public void ReadBackOutput()
        {
            var staging = BeginReadBack();
            if (staging is null) return;
            EndReadBack(staging, out _, out _);
        }

        private void CreateResources(int width, int height)
        {
            var device = _deviceManager.Device;
            _width  = width;
            _height = height;

            // Output texture: B8G8R8A8_UNorm for swap-chain CopyResource compatibility.
            // CS writes RGBA; D3D11 UAV auto-swizzles to BGRA memory layout,
            // matching WriteableBitmap Bgra8888 byte order for CPU readback.
            _outputTex?.Dispose();
            _outputUav?.Dispose();
            var outDesc = new Texture2DDescription
            {
                Width  = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
            };
            _outputTex = device.CreateTexture2D(outDesc);
            _outputUav = device.CreateUnorderedAccessView(_outputTex, new UnorderedAccessViewDescription
            {
                Format    = Format.B8G8R8A8_UNorm,
                ViewDimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 },
            });

            // Staging textures for GPU → CPU readback (double-buffered, same B8G8R8A8_UNorm format).
            // front (_stagingTex) is mapped by EndReadBack; back (_stagingTexBack) receives
            // CopyResource from BeginReadBack. References are swapped after each copy.
            _stagingTex?.Dispose();
            _stagingTexBack?.Dispose();
            var stagingDesc = new Texture2DDescription
            {
                Width  = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            _stagingTex     = device.CreateTexture2D(stagingDesc);
            _stagingTexBack = device.CreateTexture2D(stagingDesc);
            _cpuOutput      = new byte[height * width * 4];

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

            _outputUav?.Dispose();
            _outputTex?.Dispose();
            _stagingTex?.Dispose();
            _stagingTexBack?.Dispose();
            _constantBuffer?.Dispose();
            _csVertical?.Dispose();
            _csHorizontal?.Dispose();

            _logger.LogInformation("BlendRenderer disposed");
        }
    }
}
