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

        // Renderer textures opened on BlendDevice for cross-device SRV + keyed mutex access.
        private GpuTexture? _openedTexA;
        private GpuTexture? _openedTexB;

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

        // Guard for normal frame-driven blend: at most one RunGpuBlend in flight at a time.
        // Cleared by onPhase2Complete after the full blend (Phase 1 + readback) finishes.
        // Ensures renderer threads are never blocked by Blend ContextLock contention.
        private int _blendRunning;

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

            // Open each renderer texture on BlendDevice to get cross-device SRVs + keyed mutexes.
            var hA = rendererA.Texture?.SharedHandle ?? IntPtr.Zero;
            var hB = rendererB.Texture?.SharedHandle ?? IntPtr.Zero;

            if (hA != IntPtr.Zero) _openedTexA = _deviceManager.OpenSharedTexture(hA, width, height);
            if (hB != IntPtr.Zero) _openedTexB = _deviceManager.OpenSharedTexture(hB, width, height);

            // Some DXGI driver implementations implicitly acquire the keyed mutex on the opening
            // device when OpenSharedResource is called. Release key=0 immediately so the creating
            // device (renderer) can AcquireSync(0) in RenderFrame. Safe to call in a loop:
            // ReleaseSync fails with DXGI_ERROR_INVALID_CALL when not held, which we ignore.
            TryReleaseKeyedMutex(_openedTexA, 0, "openedA");
            TryReleaseKeyedMutex(_openedTexB, 0, "openedB");
        }

        private void TryReleaseKeyedMutex(GpuTexture? tex, ulong key, string name)
        {
            if (tex?.KeyedMutex == null) return;
            try
            {
                tex.KeyedMutex.ReleaseSync(key);
                _logger.LogInformation("[FSM] ReleaseSync({Name},{Key}) OK — implicit open-lock released", name, key);
            }
            catch
            {
                _logger.LogDebug("[FSM] ReleaseSync({Name},{Key}) skipped — not held", name, key);
            }
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
            // isForced=true: skip keyed mutex — renderer is idle when players are paused.
            StartReadbackThread(() => RunGpuBlend(
                onPhase2Complete: () => Interlocked.Exchange(ref _forceBlendPending, 0),
                isForced: true));
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

            bool bothLoaded = _hasTextureA && _hasTextureB;
            if (bothLoaded)
            {
                // Consume both flags only when both are ready. If only one is ready,
                // restore the consumed flag so the renderer keeps holding key=1 and
                // TryNotify can blend both frames when the other renderer also fires.
                bool readyA = Interlocked.CompareExchange(ref _frameReadyA, 0, 1) == 1;
                bool readyB = Interlocked.CompareExchange(ref _frameReadyB, 0, 1) == 1;
                if (readyA && readyB)
                {
                    // If a blend is already running, restore flags so the post-Phase-2
                    // TryNotify retry (see TriggerBlend) can pick them up. Leaving them
                    // consumed while key=1 is never acquired leads to a 500 ms deadlock.
                    if (!TriggerBlend())
                    {
                        Interlocked.Exchange(ref _frameReadyA, 1);
                        Interlocked.Exchange(ref _frameReadyB, 1);
                    }
                    return;
                }
                if (readyA) Interlocked.Exchange(ref _frameReadyA, 1);
                if (readyB) Interlocked.Exchange(ref _frameReadyB, 1);
                return;
            }

            if (!_hasTextureA && !_hasTextureB) return;

            // Single-video: blend whenever either renderer fires.
            bool anyA = Interlocked.CompareExchange(ref _frameReadyA, 0, 1) == 1;
            bool anyB = Interlocked.CompareExchange(ref _frameReadyB, 0, 1) == 1;
            if (anyA || anyB)
            {
                if (!TriggerBlend())
                {
                    if (anyA) Interlocked.Exchange(ref _frameReadyA, 1);
                    if (anyB) Interlocked.Exchange(ref _frameReadyB, 1);
                }
            }
        }

        /// <summary>
        /// Fires RunGpuBlend on a dedicated thread so renderer threads are never blocked
        /// by Blend ContextLock contention (Phase 2 Map can hold it for 12–50 ms).
        /// At most one blend is in flight at a time; when the blend completes, TryNotify
        /// is called again to pick up any frames that arrived while it was running.
        /// Returns true if the blend was started, false if one was already in flight.
        /// </summary>
        private bool TriggerBlend()
        {
            if (Interlocked.CompareExchange(ref _blendRunning, 1, 0) != 0) return false;
            StartReadbackThread(() => RunGpuBlend(
                onPhase2Complete: () =>
                {
                    Interlocked.Exchange(ref _blendRunning, 0);
                    // Retry: pick up any frames that arrived during Phase 1+2.
                    if (!_disposed) TryNotify();
                }));
            return true;
        }

        /// <summary>
        /// Two-phase GPU blend + readback.
        ///
        /// Phase 1 (outside then inside BlendDevice.ContextLock):
        ///   AcquireSync(1) on both opened textures (wait for renderer writes, outside lock)
        ///   → CS Dispatch → BeginReadBack (CopyResource to staging)
        ///   → ReleaseSync(0) on both textures (renderers may write again)
        ///
        /// Phase 2 (new AboveNormal thread, inside BlendDevice.ContextLock):
        ///   EndReadBack (Map staging → memcpy → Unmap) → BlendFrameReady.
        ///   Renderers run freely during Phase 2 (keyed mutexes already released).
        ///
        /// isForced=true (ForceBlend when paused): keyed mutex skipped — renderer is idle.
        /// </summary>
        private void RunGpuBlend(Action? onPhase2Complete = null, bool isForced = false)
        {
            // Use SRVs from opened textures (on BlendDevice) for cross-device binding.
            // Fall back to renderer's own SRV in single-video mode.
            var srvA = _openedTexA?.Srv ?? _rendererA?.Texture?.Srv;
            var srvB = _openedTexB?.Srv ?? _rendererB?.Texture?.Srv;
            if (srvA is null && srvB is null) return;

            var effectiveSrvA = srvA ?? srvB!;
            var effectiveSrvB = srvB ?? srvA!;

            // ── Keyed mutex acquire (outside ContextLock) ────────────────────────
            // Normal: Acquire key=1 (renderer wrote) with 100ms timeout.
            // Forced (paused): try key=1 then key=0, both non-blocking.
            //   key=1 → renderer wrote before pausing; key=0 → blend already read and released.
            //   If neither is available the mutex is being held; skip this pass.
            bool acqA = false, acqB = false;
            if (_openedTexA?.KeyedMutex != null && srvA == _openedTexA.Srv)
            {
                int hr = DxgiKeyedMutexHelper.AcquireSync(_openedTexA.KeyedMutex, 1, isForced ? 0 : 100);
                if (hr != DxgiKeyedMutexHelper.S_OK && isForced)
                    hr = DxgiKeyedMutexHelper.AcquireSync(_openedTexA.KeyedMutex, 0, 0);
                if (hr != DxgiKeyedMutexHelper.S_OK)
                {
                    _logger.LogDebug("[Blend] AcquireSync(A,1) skipped ({Hr:X})", hr);
                    onPhase2Complete?.Invoke();
                    return;
                }
                acqA = true;
            }
            if (_openedTexB?.KeyedMutex != null && srvB == _openedTexB.Srv)
            {
                int hr = DxgiKeyedMutexHelper.AcquireSync(_openedTexB.KeyedMutex, 1, isForced ? 0 : 100);
                if (hr != DxgiKeyedMutexHelper.S_OK && isForced)
                    hr = DxgiKeyedMutexHelper.AcquireSync(_openedTexB.KeyedMutex, 0, 0);
                if (hr != DxgiKeyedMutexHelper.S_OK)
                {
                    _logger.LogDebug("[Blend] AcquireSync(B,1) skipped ({Hr:X})", hr);
                    if (acqA) _openedTexA!.KeyedMutex!.ReleaseSync(0);
                    onPhase2Complete?.Invoke();
                    return;
                }
                acqB = true;
            }

            // ── Phase 1: inside BlendDevice.ContextLock ─────────────────────────
            ID3D11Texture2D? stagingRef = null;
            int blendN;
            var swWait = Stopwatch.StartNew();
            lock (_deviceManager.ContextLock)
            {
                long waitMs = swWait.ElapsedMilliseconds;
                var sw = Stopwatch.StartNew();

                _blendParamsProvider?.Invoke();

                long t1 = sw.ElapsedMilliseconds;
                _blend.Render(effectiveSrvA, effectiveSrvB, _currentParams);
                long t2 = sw.ElapsedMilliseconds;

                Interlocked.Increment(ref _totalBlendAttempts);
                bool doReadback = Interlocked.CompareExchange(ref _readbackPending, 1, 0) == 0;
                if (doReadback)
                    stagingRef = _blend.BeginReadBack();
                else
                    Interlocked.Increment(ref _readbackSkipped);
                long t3 = sw.ElapsedMilliseconds;

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

            // ── Release keyed mutexes (outside ContextLock) ─────────────────────
            // Renderers may now write the next frame while Phase 2 readback runs.
            if (acqA) _openedTexA!.KeyedMutex!.ReleaseSync(0);
            if (acqB) _openedTexB!.KeyedMutex!.ReleaseSync(0);

            if (stagingRef is null)
            {
                onPhase2Complete?.Invoke();
                return;
            }

            // ── Phase 2: EndReadBack + BlendFrameReady on a new AboveNormal thread ─
            StartReadbackThread(() =>
            {
                try
                {
                    var swLock = Stopwatch.StartNew();
                    long ph2LockWait;
                    lock (_deviceManager.ContextLock)
                    {
                        ph2LockWait = swLock.ElapsedMilliseconds;
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

            _openedTexA?.Dispose();
            _openedTexB?.Dispose();

            _logger.LogInformation("FrameSyncManager disposed");
        }
    }
}
