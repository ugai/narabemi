using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Renders a single MpvPlayer to a D3D11 ShaderResourceView texture using
    /// mpv's software render API (MPV_RENDER_API_TYPE_SW).
    ///
    /// Each frame: mpv writes BGRA pixels to a CPU buffer, which is then uploaded
    /// to a D3D11 Dynamic texture via Map/WriteDiscard under ContextLock.
    /// No OpenGL, no WGL, no driver-specific interop required.
    /// </summary>
    public sealed class MpvGlRenderer : IDisposable
    {
        private readonly MpvPlayer _player;
        private readonly D3D11DeviceManager _deviceManager;
        private readonly ILogger<MpvGlRenderer> _logger;

        private ID3D11Texture2D? _d3dTexture;
        private GpuTexture? _texture;

        private byte[]? _frameBuffer;
        private int _width;
        private int _height;
        private int _stride;

        private Thread? _renderThread;
        private volatile bool _disposed;
        private volatile bool _frameAvailable;
        private readonly ManualResetEventSlim _renderRequest = new(false);

        // Pinned to prevent GC while mpv holds the native callback pointer
        private MpvRenderUpdateFn? _updateCallback;
        private GCHandle _updateCallbackHandle;

        /// <summary>Fires when a new frame has been uploaded to the D3D11 texture.</summary>
        public event Action? FrameRendered;

        /// <summary>The D3D11 SRV for the latest rendered frame.</summary>
        public GpuTexture? Texture => _texture;

        public MpvGlRenderer(MpvPlayer player, D3D11DeviceManager deviceManager, ILogger<MpvGlRenderer> logger)
        {
            _player = player;
            _deviceManager = deviceManager;
            _logger = logger;
        }

        /// <summary>
        /// Initializes the SW renderer. Must be called after MpvPlayer.InitHeadless().
        /// </summary>
        public void Initialize(int width, int height)
        {
            _width  = width;
            _height = height;
            _stride = width * 4; // BGRA, 4 bytes per pixel

            // CPU frame buffer (mpv writes here)
            _frameBuffer = new byte[height * _stride];

            // D3D11 Dynamic texture — CPU writable, bindable as SRV
            var desc = new Texture2DDescription
            {
                Width             = (uint)width,
                Height            = (uint)height,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Dynamic,
                BindFlags         = BindFlags.ShaderResource,
                CPUAccessFlags    = CpuAccessFlags.Write,
            };

            _d3dTexture = _deviceManager.Device.CreateTexture2D(desc);
            var srv = _deviceManager.Device.CreateShaderResourceView(_d3dTexture);
            _texture = new GpuTexture(_d3dTexture, srv, width, height, _logger);

            // Register mpv SW render callback
            _updateCallback = OnMpvUpdate;
            _updateCallbackHandle = GCHandle.Alloc(_updateCallback);
            _player.CreateSWRenderContext(_updateCallback);

            // Start dedicated render thread
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "mpv-sw-render",
            };
            _renderThread.Start();

            _logger.LogInformation("MpvGlRenderer (SW) initialized ({W}x{H})", width, height);
        }

        private int _updateCount;

        // Called from native mpv — must never throw (would failfast the process).
        private void OnMpvUpdate(IntPtr _)
        {
            try
            {
                if (Interlocked.Increment(ref _updateCount) == 1)
                    _logger.LogInformation("mpv update callback fired (first frame signal)");
                _frameAvailable = true;
                _renderRequest.Set();
            }
            catch { /* swallow */ }
        }

        private void RenderLoop()
        {
            while (!_disposed)
            {
                _renderRequest.Wait(16);
                _renderRequest.Reset();

                if (_disposed) break;
                if (!_frameAvailable) continue;

                _frameAvailable = false;
                try
                {
                    RenderFrame();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SW RenderFrame failed");
                }
            }
        }

        private unsafe void RenderFrame()
        {
            // 1. Ask mpv to fill our CPU buffer with the current video frame (BGRA)
            fixed (byte* bufPtr = _frameBuffer!)
            {
                _player.RenderFrameSW((IntPtr)bufPtr, (long)_stride, _width, _height);
            }

            // 2. Upload CPU buffer → D3D11 Dynamic texture
            //    ContextLock serializes all D3D11 immediate-context calls across threads.
            lock (_deviceManager.ContextLock)
            {
                var ctx = _deviceManager.Context;
                var mapped = ctx.Map(_d3dTexture!, 0, MapMode.WriteDiscard);
                try
                {
                    fixed (byte* src = _frameBuffer!)
                    {
                        var rowPitch = (int)mapped.RowPitch;
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(
                                src + (long)y * _stride,
                                (void*)(mapped.DataPointer + (long)y * rowPitch),
                                _stride, _stride);
                        }
                    }
                }
                finally
                {
                    ctx.Unmap(_d3dTexture!, 0);
                }
            }

            // 3. Notify FrameSyncManager that a new frame is available
            if (_updateCount <= 2)
                _logger.LogInformation("SW frame rendered and uploaded to D3D11");
            FrameRendered?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderRequest.Set();

            _renderThread?.Join(TimeSpan.FromSeconds(2));

            if (_updateCallbackHandle.IsAllocated)
                _updateCallbackHandle.Free();

            _texture?.Dispose();
            _renderRequest.Dispose();
            _logger.LogInformation("MpvGlRenderer (SW) disposed");
        }
    }
}
