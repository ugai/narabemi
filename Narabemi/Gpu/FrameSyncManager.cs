using System;
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
        private readonly ILogger<FrameSyncManager> _logger;

        private MpvGlRenderer? _rendererA;
        private MpvGlRenderer? _rendererB;

        private int _frameReadyA;
        private int _frameReadyB;
        private bool _hasTextureA;
        private bool _hasTextureB;

        private volatile bool _disposed;

        /// <summary>Fires on a render thread when frame(s) are ready for CPU blend + display.</summary>
        public event Action? BlendFrameReady;

        public FrameSyncManager(BlendRenderer blend, ILogger<FrameSyncManager> logger)
        {
            // BlendRenderer kept in DI signature for compatibility; not used in CPU blend path.
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
