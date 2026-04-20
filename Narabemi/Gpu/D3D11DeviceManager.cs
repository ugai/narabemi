using System;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Manages a shared D3D11 device and immediate context for the application lifetime.
    /// Provides factory methods for creating GPU resources.
    /// </summary>
    public sealed class D3D11DeviceManager : IDisposable
    {
        private readonly ILogger<D3D11DeviceManager> _logger;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private bool _disposed;

        public ID3D11Device Device => _device ?? throw new InvalidOperationException("D3D11 device not initialized.");
        public ID3D11DeviceContext Context => _context ?? throw new InvalidOperationException("D3D11 context not initialized.");
        public bool IsInitialized => _device != null;

        /// <summary>
        /// Must be held whenever calling methods on Context from multiple threads.
        /// D3D11 immediate context is not thread-safe.
        /// </summary>
        public readonly object ContextLock = new();

        public D3D11DeviceManager(ILogger<D3D11DeviceManager> logger)
        {
            _logger = logger;
        }

        public void Initialize()
        {
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
            };

            var flags = DeviceCreationFlags.BgraSupport; // Required for D2D/DXGI interop
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif

            var result = D3D11.D3D11CreateDevice(
                adapter: null,
                DriverType.Hardware,
                flags,
                featureLevels,
                out _device,
                out _context);

            if (result.Failure || _device is null)
                throw new InvalidOperationException($"D3D11CreateDevice failed: {result}");

            _logger.LogInformation("D3D11 device created (FeatureLevel={Level})", _device.FeatureLevel);
        }

        /// <summary>
        /// Creates a Texture2D with BIND_RENDER_TARGET | BIND_SHADER_RESOURCE and MiscFlags.Shared,
        /// suitable for WGL_NV_DX_interop2 registration and Avalonia GPU interop.
        /// </summary>
        public GpuTexture CreateSharedTexture(int width, int height)
        {
            if (_device is null) throw new InvalidOperationException("D3D11 device not initialized.");

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, // BGRA matches mpv OpenGL default output
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.Shared,
            };

            var texture = _device.CreateTexture2D(desc);
            var srv = _device.CreateShaderResourceView(texture);
            return new GpuTexture(texture, srv, width, height, _logger);
        }

        /// <summary>
        /// Creates a Texture2D suitable for blend shader output and Avalonia presentation.
        /// Same flags as CreateSharedTexture.
        /// </summary>
        public GpuTexture CreateOutputTexture(int width, int height) =>
            CreateSharedTexture(width, height);

        /// <summary>
        /// Creates a per-renderer texture with SharedKeyedMutex for cross-device GL→Blend sync.
        /// The caller (MpvGlRenderer) acquires key=0, renders, releases key=1.
        /// BlendRenderer acquires key=1, reads, releases key=0.
        /// </summary>
        public GpuTexture CreateRendererTexture(int width, int height)
        {
            if (_device is null) throw new InvalidOperationException("D3D11 device not initialized.");

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm, // RGBA matches GL's output via WGL_NV_DX_interop2
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.SharedKeyedMutex, // implies Shared; do not combine
            };

            var texture = _device.CreateTexture2D(desc);
            var srv = _device.CreateShaderResourceView(texture);
            return new GpuTexture(texture, srv, width, height, _logger);
        }

        /// <summary>
        /// Creates a Texture2D suitable for WGL_NV_DX_interop2 registration (GL render target).
        /// Uses plain Shared (not SharedKeyedMutex) — WGL requires MISC_SHARED and the keyed
        /// mutex protocol conflicts with WGL's internal key=0 binary lock.
        /// </summary>
        public GpuTexture CreateWglTexture(int width, int height)
        {
            if (_device is null) throw new InvalidOperationException("D3D11 device not initialized.");

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.Shared,
            };

            var texture = _device.CreateTexture2D(desc);
            var srv = _device.CreateShaderResourceView(texture);
            return new GpuTexture(texture, srv, width, height, _logger);
        }

        /// <summary>
        /// Opens a renderer texture (created with CreateRendererTexture on another device)
        /// on this device using its legacy DXGI shared handle.
        /// The returned GpuTexture holds both the SRV and the KeyedMutex on this device.
        /// </summary>
        public GpuTexture OpenSharedTexture(IntPtr sharedHandle, int width, int height)
        {
            if (_device is null) throw new InvalidOperationException("D3D11 device not initialized.");

            var texture = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
            var srv = _device.CreateShaderResourceView(texture);
            return new GpuTexture(texture, srv, width, height, _logger);
        }

        /// <summary>
        /// Checks whether the D3D11 device has been lost.
        /// Call periodically to detect GPU resets.
        /// </summary>
        public bool IsDeviceLost()
        {
            if (_device is null) return false;
            var reason = _device.DeviceRemovedReason;
            return reason.Failure;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _context?.Dispose();
            _device?.Dispose();
            _logger.LogInformation("D3D11 device disposed");
        }
    }
}
