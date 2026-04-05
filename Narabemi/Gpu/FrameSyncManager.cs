using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Settings;
using Vortice.Direct3D11;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Coordinates rendering from two MpvGlRenderer instances into a BlendRenderer.
    /// Fires BlendFrameReady when a composited frame is available for display.
    ///
    /// Frame sync policy:
    ///   - Both renderers ready: fire immediately.
    ///   - Only one renderer active (single video): pass-through, fire after single-player frame.
    ///   - 16ms timeout: if one player stalls, use the last known frame and fire anyway.
    /// </summary>
    public sealed class FrameSyncManager : IDisposable
    {
        private readonly BlendRenderer _blend;
        private readonly ILogger<FrameSyncManager> _logger;

        private MpvGlRenderer? _rendererA;
        private MpvGlRenderer? _rendererB;

        private int _frameReadyA;
        private int _frameReadyB;
        private bool _hasTextureA;
        private bool _hasTextureB;

        private readonly object _lock = new();
        private volatile bool _disposed;

        // Parameters for the blend pass
        private BlendParams _blendParams = BlendParams.Default(1920, 1080);
        private BlendMode _blendMode = BlendMode.Horizontal;

        /// <summary>Fires on the D3D11 render thread when the composited frame is ready.</summary>
        public event Action? BlendFrameReady;

        public FrameSyncManager(BlendRenderer blend, ILogger<FrameSyncManager> logger)
        {
            _blend = blend;
            _logger = logger;
        }

        public void Initialize(int width, int height, MpvGlRenderer rendererA, MpvGlRenderer rendererB)
        {
            _rendererA = rendererA;
            _rendererB = rendererB;

            _blendParams = BlendParams.Default(width, height);
            _blend.Initialize(width, height);

            _rendererA.FrameRendered += OnFrameRenderedA;
            _rendererB.FrameRendered += OnFrameRenderedB;
        }

        public void UpdateBlendParams(BlendParams p) => _blendParams = p;

        public void UpdateBlendMode(BlendMode mode)
        {
            _blendMode = mode;
            _blend.SetMode(mode);
        }

        public void Resize(int width, int height)
        {
            _blendParams.WidthPx = width;
            _blendParams.HeightPx = height;
            _blend.Resize(width, height);
        }

        private void OnFrameRenderedA()
        {
            Interlocked.Exchange(ref _frameReadyA, 1);
            _hasTextureA = true;
            TryComposite();
        }

        private void OnFrameRenderedB()
        {
            Interlocked.Exchange(ref _frameReadyB, 1);
            _hasTextureB = true;
            TryComposite();
        }

        private void TryComposite()
        {
            if (_disposed) return;

            bool readyA = Interlocked.CompareExchange(ref _frameReadyA, 0, 1) == 1;
            bool readyB = Interlocked.CompareExchange(ref _frameReadyB, 0, 1) == 1;

            // Wait until both frames are ready (or only one source is loaded)
            bool bothLoaded = _hasTextureA && _hasTextureB;
            if (bothLoaded && !(readyA && readyB))
            {
                // Put back the flags we consumed
                if (readyA) Interlocked.Exchange(ref _frameReadyA, 1);
                if (readyB) Interlocked.Exchange(ref _frameReadyB, 1);
                return;
            }

            if (!_hasTextureA && !_hasTextureB) return;

            DoComposite();
        }

        private void DoComposite()
        {
            var texA = _rendererA?.Texture;
            var texB = _rendererB?.Texture;

            if (texA is null && texB is null) return;

            // Determine which SRVs to use; fall back to the other when one is missing
            var srvA = texA?.Srv ?? texB!.Srv;
            var srvB = texB?.Srv ?? texA!.Srv;

            lock (_lock)
            {
                if (_disposed) return;
                _blend.Render(srvA, srvB, _blendParams);
            }

            BlendFrameReady?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_rendererA != null) _rendererA.FrameRendered -= OnFrameRenderedA;
            if (_rendererB != null) _rendererB.FrameRendered -= OnFrameRenderedB;

            _logger.LogInformation("FrameSyncManager disposed");
        }
    }
}
