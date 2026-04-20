using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Narabemi.Gpu;
using Narabemi.ViewModels;

namespace Narabemi.UI.Controls
{
    /// <summary>
    /// Displays the blended video output. Initializes the GPU pipeline on first attach:
    /// mpv GL render → D3D11 texture (WGL_NV_DX_interop2) → CS blend → CPU readback → WriteableBitmap.
    /// </summary>
    public sealed class GpuBlendControl : Control
    {
        private readonly FrameSyncManager? _syncManager;
        private readonly BlendRenderer? _blendRenderer;
        private readonly ILogger<GpuBlendControl> _logger;

        private WriteableBitmap? _bitmap;
        private int _texWidth;
        private int _texHeight;
        private bool _initialized;
        private bool _frameScheduled;

        // Cached ViewModel reference for reading blend params on the render thread.
        private MainWindowViewModel? _vm;

        // Timing instrumentation
        private int _presentFrameCount;
        private long _lastPresentTick;
        private int _renderCallCount;
        private long _lastRenderTick;

        public GpuBlendControl()
            : this(
                App.Services?.GetService(typeof(FrameSyncManager)) as FrameSyncManager,
                App.Services?.GetService(typeof(BlendRenderer)) as BlendRenderer,
                App.Services?.GetService(typeof(ILogger<GpuBlendControl>)) as ILogger<GpuBlendControl>
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuBlendControl>.Instance)
        {
        }

        public GpuBlendControl(FrameSyncManager? syncManager, BlendRenderer? blendRenderer, ILogger<GpuBlendControl> logger)
        {
            _syncManager = syncManager;
            _blendRenderer = blendRenderer;
            _logger = logger;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            InitializePipeline();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_syncManager != null)
                _syncManager.BlendFrameReady -= OnBlendFrameReady;
        }

        private void InitializePipeline()
        {
            try
            {
                // 1. D3D11 devices — R1/R2 for each renderer, Blend for CS dispatch.
                var r1 = App.Services?.GetKeyedService<D3D11DeviceManager>("R1");
                var r2 = App.Services?.GetKeyedService<D3D11DeviceManager>("R2");
                var blendDev = App.Services?.GetKeyedService<D3D11DeviceManager>("Blend");
                if (r1 is null || r2 is null || blendDev is null)
                {
                    _logger.LogError("D3D11DeviceManagers not in DI");
                    return;
                }
                if (!r1.IsInitialized) r1.Initialize();
                if (!r2.IsInitialized) r2.Initialize();
                if (!blendDev.IsInitialized) blendDev.Initialize();

                // 2. mpv headless init
                App.Services?.GetKeyedService<VideoPlayerViewModel>("PlayerA")?.InitMpvHeadless();
                App.Services?.GetKeyedService<VideoPlayerViewModel>("PlayerB")?.InitMpvHeadless();

                // 3. GL renderer init (mpv GL → D3D11 R8G8B8A8 texture via WGL_NV_DX_interop2)
                const int W = 1280;
                const int H = 720;

                var rendererA = App.Services?.GetKeyedService<MpvGlRenderer>("PlayerA");
                var rendererB = App.Services?.GetKeyedService<MpvGlRenderer>("PlayerB");
                rendererA?.Initialize(W, H);
                rendererB?.Initialize(W, H);

                // 4. D3D11 blend shader pipeline
                _blendRenderer?.Initialize(W, H);

                // 5. Frame sync (coordinates dual-player frame delivery + GPU blend)
                if (_syncManager is not null && rendererA is not null && rendererB is not null)
                    _syncManager.Initialize(W, H, rendererA, rendererB);

                // 5b. Register blend params provider so RunGpuBlend always uses the latest
                //     ViewModel values (eliminates 1-frame lag and enables paused-state updates).
                _vm = App.Services?.GetService<MainWindowViewModel>();
                if (_syncManager != null && _vm != null)
                    _syncManager.SetBlendParamsProvider(ReadAndApplyBlendParams);

                // 6. Bitmap for display
                _texWidth = W;
                _texHeight = H;
                _bitmap = new WriteableBitmap(
                    new PixelSize(W, H),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                _initialized = true;
                if (_syncManager != null)
                    _syncManager.BlendFrameReady += OnBlendFrameReady;

                _logger.LogInformation("GpuBlendControl initialized ({W}x{H})", W, H);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GpuBlendControl initialization failed");
            }
        }

        private void OnBlendFrameReady()
        {
            if (_frameScheduled) return;
            _frameScheduled = true;
            Dispatcher.UIThread.Post(PresentFrame, DispatcherPriority.Render);
        }

        /// <summary>
        /// Reads the current blend parameters from the cached ViewModel and pushes them to
        /// FrameSyncManager. Called by FrameSyncManager on the render thread just before each
        /// CS dispatch, so every rendered frame uses the most up-to-date parameters.
        /// Reading plain value-type properties (double, int, byte) from another thread is safe.
        /// </summary>
        private void ReadAndApplyBlendParams()
        {
            if (_syncManager is null || _vm is null) return;
            _syncManager.UpdateBlendParams(
                (float)_vm.BlendRatio,
                (float)_vm.BlendBorderWidth,
                _vm.BlendBorderColor.R,
                _vm.BlendBorderColor.G,
                _vm.BlendBorderColor.B,
                _vm.BlendMode);
        }

        private void PresentFrame()
        {
            _frameScheduled = false;
            if (!_initialized || _bitmap is null || _blendRenderer is null) return;

            try
            {
                // Blend parameters are now applied in RunGpuBlend via ReadAndApplyBlendParams.
                // PresentFrame only needs to copy the already-blended CPU buffer to the bitmap.

                // Copy GPU blend result → WriteableBitmap
                var cpuOutput = _blendRenderer.CpuOutput;
                if (cpuOutput is null) return;

                var sw = Stopwatch.StartNew();
                unsafe
                {
                    using var fb = _bitmap.Lock();
                    lock (_blendRenderer.CpuOutputLock)
                    {
                        fixed (byte* src = cpuOutput)
                        {
                            CpuBlender.CopyFrame(
                                (byte*)fb.Address, fb.RowBytes,
                                src, _texWidth * 4,
                                _texWidth, _texHeight);
                        }
                    }
                }
                long copyMs = sw.ElapsedMilliseconds;

                long nowTick = Stopwatch.GetTimestamp();
                double intervalMs = _lastPresentTick == 0 ? 0 :
                    (nowTick - _lastPresentTick) * 1000.0 / Stopwatch.Frequency;
                _lastPresentTick = nowTick;

                if (++_presentFrameCount % 5 == 0)
                    _logger.LogDebug(
                        "[Present#{N}] copy={C}ms | interval={I:F1}ms ({FPS:F1}fps)",
                        _presentFrameCount, copyMs,
                        intervalMs, intervalMs > 0 ? 1000.0 / intervalMs : 0);

                InvalidateVisual();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to present frame");
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_bitmap is not null)
                context.DrawImage(_bitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));

            long nowTick = Stopwatch.GetTimestamp();
            double renderIntervalMs = _lastRenderTick == 0 ? 0 :
                (nowTick - _lastRenderTick) * 1000.0 / Stopwatch.Frequency;
            _lastRenderTick = nowTick;
            if (++_renderCallCount % 5 == 0)
                _logger.LogDebug(
                    "[Render#{N}] interval={I:F1}ms ({FPS:F1}fps)",
                    _renderCallCount, renderIntervalMs,
                    renderIntervalMs > 0 ? 1000.0 / renderIntervalMs : 0);
        }
    }
}
