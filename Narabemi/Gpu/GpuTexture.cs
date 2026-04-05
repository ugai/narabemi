using System;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Wraps a D3D11 Texture2D with its SRV and the shared DXGI handle
    /// used for WGL_NV_DX_interop2 and Avalonia GPU interop.
    /// </summary>
    public sealed class GpuTexture : IDisposable
    {
        private readonly ILogger _logger;
        private bool _disposed;

        public ID3D11Texture2D Texture { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// Legacy shared handle (HANDLE) from IDXGIResource.GetSharedHandle().
        /// Used by WGL_NV_DX_interop2 to register the texture as a GL object.
        /// </summary>
        public IntPtr SharedHandle { get; }

        public GpuTexture(ID3D11Texture2D texture, ID3D11ShaderResourceView srv, int width, int height, ILogger logger)
        {
            Texture = texture;
            Srv = srv;
            Width = width;
            Height = height;
            _logger = logger;

            using var dxgiResource = texture.QueryInterface<IDXGIResource>();
            SharedHandle = dxgiResource.SharedHandle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Srv.Dispose();
            Texture.Dispose();
        }
    }
}
