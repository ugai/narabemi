using System;
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
                // 1. D3D11 device (still needed for future GPU path; also validates hardware)
                var d3d = App.Services?.GetService<D3D11DeviceManager>();
                if (d3d is null) { _logger.LogError("D3D11DeviceManager not in DI"); return; }
                if (!d3d.IsInitialized) d3d.Initialize();

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

        private void PresentFrame()
        {
            _frameScheduled = false;
            if (!_initialized || _bitmap is null || _blendRenderer is null) return;

            try
            {
                // Push blend parameters from ViewModel to the sync manager
                if (_syncManager is not null)
                {
                    float ratio = 0.5f;
                    float borderWidth = 1.0f;
                    byte borderR = 255, borderG = 255, borderB = 255;
                    int blendMode = 0;

                    if (DataContext is MainWindowViewModel vm ||
                        (App.Services?.GetService(typeof(MainWindowViewModel)) is MainWindowViewModel svcVm && (vm = svcVm) != null))
                    {
                        ratio = (float)vm.BlendRatio;
                        borderWidth = (float)vm.BlendBorderWidth;
                        borderR = vm.BlendBorderColor.R;
                        borderG = vm.BlendBorderColor.G;
                        borderB = vm.BlendBorderColor.B;
                        blendMode = vm.BlendMode;
                    }

                    _syncManager.UpdateBlendParams(ratio, borderWidth, borderR, borderG, borderB, blendMode);
                }

                // Copy GPU blend result → WriteableBitmap
                var cpuOutput = _blendRenderer.CpuOutput;
                if (cpuOutput is null) return;

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
        }
    }
}
