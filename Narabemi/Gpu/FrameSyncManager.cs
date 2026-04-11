using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Coordinates frame delivery from two MpvGlRenderer instances.
    /// Fires BlendFrameReady when new frame(s) are available for display.
    ///
    /// Frame sync policy:
    ///   - Both renderers ready: fire immediately.
    ///   - Only one renderer active (single video): pass-through.
    /// </summary>
    public sealed class FrameSyncManager : IDisposable
    {
        private readonly BlendRenderer _blend;
        private readonly D3D11DeviceManager _deviceManager;
        private readonly ILogger<FrameSyncManager> _logger;

        private MpvGlRenderer? _rendererA;
        private MpvGlRenderer? _rendererB;

        private int _frameReadyA;
        private int _frameReadyB;
        private bool _hasTextureA;
        private bool _hasTextureB;

        private volatile bool _disposed;

        /// <summary>Fires on a render thread when a blended frame is ready for display.</summary>
        public event Action? BlendFrameReady;

        public FrameSyncManager(BlendRenderer blend, D3D11DeviceManager deviceManager, ILogger<FrameSyncManager> logger)
        {
            _blend = blend;
            _deviceManager = deviceManager;
            _logger = logger;
        }

        public void Initialize(int width, int height, MpvGlRenderer rendererA, MpvGlRenderer rendererB)
        {
            _rendererA = rendererA;
            _rendererB = rendererB;

            _rendererA.FrameRendered += OnFrameRenderedA;
            _rendererB.FrameRendered += OnFrameRenderedB;
        }

        private void OnFrameRenderedA()
        {
            Interlocked.Exchange(ref _frameReadyA, 1);
            _hasTextureA = true;
            TryNotify();
        }

        private void OnFrameRenderedB()
        {
            Interlocked.Exchange(ref _frameReadyB, 1);
            _hasTextureB = true;
            TryNotify();
        }

        /// <summary>
        /// Updates blend parameters from the ViewModel. Call from UI thread before blend.
        /// </summary>
        public void UpdateBlendParams(float ratio, float borderWidth, byte borderR, byte borderG, byte borderB, int blendMode)
        {
            int w = _blend.OutputWidth;
            int h = _blend.OutputHeight;
            if (w == 0 || h == 0) return;

            _blend.SetMode(blendMode == 1 ? BlendMode.Vertical : BlendMode.Horizontal);
            _currentParams = new BlendParams
            {
                WidthPx = w,
                HeightPx = h,
                Ratio = ratio,
                BorderWidth = borderWidth,
                BorderColor = new System.Numerics.Vector4(borderR / 255f, borderG / 255f, borderB / 255f, 1f),
            };
        }

        private BlendParams _currentParams = BlendParams.Default(1280, 720);

        private void TryNotify()
        {
            if (_disposed) return;

            bool readyA = Interlocked.CompareExchange(ref _frameReadyA, 0, 1) == 1;
            bool readyB = Interlocked.CompareExchange(ref _frameReadyB, 0, 1) == 1;

            bool bothLoaded = _hasTextureA && _hasTextureB;
            if (bothLoaded && !(readyA && readyB))
            {
                if (readyA) Interlocked.Exchange(ref _frameReadyA, 1);
                if (readyB) Interlocked.Exchange(ref _frameReadyB, 1);
                return;
            }

            if (!_hasTextureA && !_hasTextureB) return;

            // Run GPU blend + readback on the render thread (we're already on it)
            RunGpuBlend();

            BlendFrameReady?.Invoke();
        }

        private void RunGpuBlend()
        {
            var texA = _rendererA?.Texture?.Texture;
            var texB = _rendererB?.Texture?.Texture;
            if (texA is null && texB is null) return;

            lock (_deviceManager.ContextLock)
            {
                // CopyResource: Dynamic → Default (workaround for driver sampling issue)
                _blend.PrepareInputs(texA, texB);
                _blend.Render(_currentParams);
                _blend.ReadBackOutput();
            }
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
