using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;

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

        // Callback set by GpuBlendControl to supply current blend params from the ViewModel.
        // Invoked inside ContextLock in RunGpuBlend, just before each CS dispatch.
        private volatile Action? _blendParamsProvider;

        // Guard for ForceBlend: prevents queuing more than one concurrent forced pass.
        private int _forceBlendPending;

        // Guard for Phase 2 readback: only one Map task in flight at a time.
        // Prevents multiple concurrent Map calls on the same staging texture.
        // Set to 1 before BeginReadBack; reset to 0 after EndReadBack completes.
        private int _readbackPending;

        // Timing instrumentation
        private int _blendFrameCount;
        private long _lastBlendTick;

        // Readback skip tracking
        private long _totalBlendAttempts;
        private long _readbackSkipped;
        private long _lastSkipLogTick;

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
        /// Registers a callback that reads current blend parameters from the ViewModel and
        /// applies them via UpdateBlendParams. Invoked just before each CS dispatch so that
        /// the rendered frame always uses up-to-date parameters (eliminates 1-frame lag).
        /// </summary>
        public void SetBlendParamsProvider(Action provider)
        {
            _blendParamsProvider = provider;
        }

        /// <summary>
        /// Forces a blend pass outside the normal frame-driven cycle.
        /// Call when blend parameters change while both players are paused — in that state
        /// mpv does not fire GL update callbacks so the display would otherwise not refresh.
        /// Safe to call from any thread. Coalesces concurrent calls (at most one pending).
        /// </summary>
        public void ForceBlend()
        {
            if (!_hasTextureA && !_hasTextureB) return;
            // Drop extra calls while one is already in flight.
            if (Interlocked.CompareExchange(ref _forceBlendPending, 1, 0) != 0) return;
            StartReadbackThread(() => RunGpuBlend(onPhase2Complete: () =>
                Interlocked.Exchange(ref _forceBlendPending, 0)));
        }

        private static void StartReadbackThread(Action work)
        {
            var t = new Thread(() => work()) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            t.Start();
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

            // Phase 1 runs on the render thread (we're already on it).
            // Phase 2 (EndReadBack + BlendFrameReady) runs on a new AboveNormal thread.
            RunGpuBlend();
        }

        /// <summary>
        /// Two-phase GPU blend + readback.
        ///
        /// Phase 1 (inside ContextLock on calling thread, ~0 ms GPU stall):
        ///   PrepareInputs → CS Dispatch → BeginReadBack (CopyResource to staging back buffer).
        ///   BeginReadBack is guarded by _readbackPending — skipped when Phase 2 is in flight,
        ///   which prevents concurrent Map calls on the same staging texture.
        ///
        /// Phase 2 (new AboveNormal thread, inside ContextLock, ~5-30 ms GPU stall):
        ///   EndReadBack (Map staging front → memcpy → Unmap) → BlendFrameReady.
        ///   The staging texture reference is captured in Phase 1 before the lock is released,
        ///   so it is race-free regardless of subsequent Phase 1 swaps.
        /// </summary>
        private void RunGpuBlend(Action? onPhase2Complete = null)
        {
            var srvA = _rendererA?.Texture?.Srv;
            var srvB = _rendererB?.Texture?.Srv;
            if (srvA is null && srvB is null) return;

            // Single-video mode: duplicate the one available SRV to both slots.
            var effectiveSrvA = srvA ?? srvB!;
            var effectiveSrvB = srvB ?? srvA!;

            // ── Phase 1: inside ContextLock ──────────────────────────────────────
            ID3D11Texture2D? stagingRef = null;
            int blendN;
            var swWait = Stopwatch.StartNew();
            lock (_deviceManager.ContextLock)
            {
                long waitMs = swWait.ElapsedMilliseconds;
                var sw = Stopwatch.StartNew();

                // Pull the latest blend params from the ViewModel before each CS dispatch.
                _blendParamsProvider?.Invoke();

                // Bind mpv SRVs directly: no intermediate CopyResource needed.
                // WGL interop unlock occurred in RenderFrame before ContextLock was released.
                long t1 = sw.ElapsedMilliseconds;
                _blend.Render(effectiveSrvA, effectiveSrvB, _currentParams);
                long t2 = sw.ElapsedMilliseconds;

                // BeginReadBack only when no Phase 2 task is in flight.
                // CAS must precede BeginReadBack to prevent cycle N+2 from writing to
                // the same staging texture that cycle N's Phase 2 is still mapping.
                Interlocked.Increment(ref _totalBlendAttempts);
                bool doReadback = Interlocked.CompareExchange(ref _readbackPending, 1, 0) == 0;
                if (doReadback)
                    stagingRef = _blend.BeginReadBack();
                else
                    Interlocked.Increment(ref _readbackSkipped);
                long t3 = sw.ElapsedMilliseconds;

                // Periodic skip rate summary (every 5 seconds)
                long skipNow = Stopwatch.GetTimestamp();
                if (skipNow - _lastSkipLogTick > Stopwatch.Frequency * 5)
                {
                    _lastSkipLogTick = skipNow;
                    _logger.LogDebug("[Blend] skip={S}/{T}",
                        Interlocked.Read(ref _readbackSkipped),
                        Interlocked.Read(ref _totalBlendAttempts));
                }

                long nowTick = Stopwatch.GetTimestamp();
                double intervalMs = _lastBlendTick == 0 ? 0 :
                    (nowTick - _lastBlendTick) * 1000.0 / Stopwatch.Frequency;
                _lastBlendTick = nowTick;

                blendN = ++_blendFrameCount;
                if (blendN % 5 == 0)
                    _logger.LogDebug(
                        "[Blend#{N}] lockWait={W}ms prepare={P}ms cs={C}ms beginRB={B}ms{Skip} | interval={I:F1}ms ({FPS:F1}fps)",
                        blendN, waitMs, t1, t2 - t1, t3 - t2,
                        stagingRef is null ? "(skip)" : "",
                        intervalMs, intervalMs > 0 ? 1000.0 / intervalMs : 0);
            }

            if (stagingRef is null)
            {
                // Phase 2 skipped (readback in flight or BeginReadBack returned null).
                // Invoke completion callback immediately so ForceBlend can coalesce correctly.
                onPhase2Complete?.Invoke();
                return;
            }

            // ── Phase 2: EndReadBack + BlendFrameReady on a new AboveNormal thread ─────
            StartReadbackThread(() =>
            {
                try
                {
                    var swLock = Stopwatch.StartNew();
                    lock (_deviceManager.ContextLock)
                    {
                        long ph2LockWait = swLock.ElapsedMilliseconds;
                        _blend.EndReadBack(stagingRef, out long mapMs, out long memcpyMs);
                        if (blendN % 5 == 0)
                            _logger.LogDebug("[Blend#{N}] ph2 lockWait={L}ms map={M}ms memcpy={C}ms",
                                blendN, ph2LockWait, mapMs, memcpyMs);
                    }
                    if (!_disposed) BlendFrameReady?.Invoke();
                }
                finally
                {
                    Interlocked.Exchange(ref _readbackPending, 0);
                    onPhase2Complete?.Invoke();
                }
            });
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
