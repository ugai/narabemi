using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Renders a single MpvPlayer to a D3D11 SharedKeyedMutex texture using
    /// mpv's software render API (CPU buffer → D3D11 UpdateSubresource upload).
    ///
    /// Eliminates WGL_NV_DX_interop2 GPU-sync stalls. Both renderers run their
    /// CPU decode + software render in parallel with no shared driver mutex.
    ///
    /// Per frame: mpv SW-renders RGBA into a CPU buffer → AcquireSync(0) →
    /// UpdateSubresource + Flush → ReleaseSync(1) → FrameRendered.
    /// </summary>
    public sealed class MpvGlRenderer : IDisposable
    {
        private readonly MpvPlayer _player;
        private readonly D3D11DeviceManager _deviceManager;
        private readonly ILogger<MpvGlRenderer> _logger;

        // Cross-device texture (SharedKeyedMutex) — SW render uploads here each frame.
        // Blend reads via AcquireSync(1) / ReleaseSync(0).
        private GpuTexture? _copyTexture;

        // CPU-side frame buffer: mpv writes RGBA pixels here, we then upload to D3D11.
        private byte[]? _frameBuffer;

        private int _width;
        private int _height;

        private Thread? _renderThread;
        private volatile bool _disposed;
        private volatile bool _frameAvailable;
        private readonly ManualResetEventSlim _renderRequest = new(false);
        private readonly ManualResetEventSlim _initDone = new(false);
        private Exception? _initException;

        private MpvRenderUpdateFn? _updateCallback;
        private GCHandle _updateCallbackHandle;

        private static int _instanceCount;
        private readonly string _tag;
        private int _renderFrameCount;
        private long _lastRenderTick;

        /// <summary>Fires on the render thread when a new frame has been uploaded to the D3D11 texture.</summary>
        public event Action? FrameRendered;

        /// <summary>The D3D11 SRV for the latest rendered frame (SharedKeyedMutex, cross-device).</summary>
        public GpuTexture? Texture => _copyTexture;

        public MpvGlRenderer(MpvPlayer player, D3D11DeviceManager deviceManager, ILogger<MpvGlRenderer> logger)
        {
            _player = player;
            _deviceManager = deviceManager;
            _logger = logger;
            _tag = "R" + Interlocked.Increment(ref _instanceCount);
        }

        /// <summary>
        /// Allocates the CPU frame buffer and D3D11 texture, then starts the render thread.
        /// Blocks until the mpv SW render context is ready.
        /// Must be called after MpvPlayer.InitHeadless().
        /// </summary>
        public void Initialize(int width, int height)
        {
            _width  = width;
            _height = height;
            _frameBuffer = new byte[width * height * 4];
            _copyTexture = _deviceManager.CreateRendererTexture(width, height);

            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "mpv-sw-render",
            };
            _renderThread.Start();

            _initDone.Wait();
            if (_initException is not null)
                throw new InvalidOperationException("SW render context setup failed", _initException);

            _logger.LogInformation("MpvGlRenderer (SW) initialized ({W}x{H})", width, height);
        }

        private void OnMpvUpdate(IntPtr _)
        {
            try
            {
                _frameAvailable = true;
                _renderRequest.Set();
            }
            catch { /* swallow — called from native mpv */ }
        }

        private void RenderLoop()
        {
            try
            {
                _updateCallback = OnMpvUpdate;
                _updateCallbackHandle = GCHandle.Alloc(_updateCallback);
                _player.CreateSWRenderContext(_updateCallback);
            }
            catch (Exception ex)
            {
                _initException = ex;
                _initDone.Set();
                _logger.LogError(ex, "SW render context setup failed");
                return;
            }

            _initDone.Set();

            while (!_disposed)
            {
                long tWaitStart = Stopwatch.GetTimestamp();
                _renderRequest.Wait(16);
                long tWaitEnd = Stopwatch.GetTimestamp();
                _renderRequest.Reset();

                if (_disposed) break;
                if (!_frameAvailable) continue;

                double callbackWaitMs = (tWaitEnd - tWaitStart) * 1000.0 / Stopwatch.Frequency;
                _frameAvailable = false;
                try
                {
                    RenderFrame(callbackWaitMs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SW RenderFrame failed");
                }
            }
        }

        private unsafe void RenderFrame(double callbackWaitMs)
        {
            var sw = Stopwatch.StartNew();

            int err;
            fixed (byte* ptr = _frameBuffer)
                err = _player.RenderFrameSW((IntPtr)ptr, _width * 4, _width, _height);
            long tRender = sw.ElapsedMilliseconds;

            if (err < 0)
            {
                _logger.LogWarning("[{Tag}#{N}] RenderFrameSW error {E}; skipping", _tag, _renderFrameCount, err);
                return;
            }
            _player.ReportSwap();

            if (_copyTexture?.KeyedMutex is null) return;

            int hr = DxgiKeyedMutexHelper.AcquireSync(_copyTexture.KeyedMutex, 0, 500);
            long tAcq = sw.ElapsedMilliseconds;
            if (hr != DxgiKeyedMutexHelper.S_OK)
            {
                _logger.LogWarning("[{Tag}#{N}] AcquireSync(copyTex,0) skipped hr={Hr:X} wait={W}ms",
                    _tag, _renderFrameCount, hr, tAcq - tRender);
                return;
            }

            _deviceManager.Context.UpdateSubresource<byte>(
                _frameBuffer!, _copyTexture.Texture, 0u, (uint)(_width * 4), 0u);
            long tUpload = sw.ElapsedMilliseconds;

            _deviceManager.Context.Flush();
            long tFlush = sw.ElapsedMilliseconds;

            _copyTexture.KeyedMutex.ReleaseSync(1);
            long tRel = sw.ElapsedMilliseconds;

            long nowTick = Stopwatch.GetTimestamp();
            double intervalMs = _lastRenderTick == 0 ? 0 :
                (nowTick - _lastRenderTick) * 1000.0 / Stopwatch.Frequency;
            _lastRenderTick = nowTick;

            if (++_renderFrameCount % 5 == 0)
                _logger.LogInformation(
                    "[{Tag}#{N}] cbWait={W:F1}ms swRender={R}ms acqWait={A}ms upload={U}ms flush={F}ms | interval={I:F1}ms ({FPS:F1}fps)",
                    _tag, _renderFrameCount, callbackWaitMs, tRender, tAcq - tRender,
                    tUpload - tAcq, tFlush - tUpload,
                    intervalMs, intervalMs > 0 ? 1000.0 / intervalMs : 0);

            FrameRendered?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderRequest.Set();

            _renderThread?.Join(TimeSpan.FromSeconds(3));

            if (_updateCallbackHandle.IsAllocated) _updateCallbackHandle.Free();

            // Leave keyed mutex at key=0 so the next process can AcquireSync(0) cleanly.
            if (_copyTexture?.KeyedMutex is not null)
                try { _copyTexture.KeyedMutex.ReleaseSync(0); } catch { /* not held — fine */ }
            _copyTexture?.Dispose();
            _renderRequest.Dispose();
            _initDone.Dispose();

            _logger.LogInformation("MpvGlRenderer (SW) disposed");
        }
    }
}
