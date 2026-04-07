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
    /// Displays the blended video output. Initialises the full GPU pipeline
    /// (D3D11 device → mpv SW render → blend shaders) on first attach, then
    /// copies each composited frame to a WriteableBitmap for display.
    /// </summary>
    public sealed class GpuBlendControl : Control
    {
        private readonly FrameSyncManager? _syncManager;
        private readonly ILogger<GpuBlendControl> _logger;

        private WriteableBitmap? _bitmap;
        private bool _initialized;
        private bool _frameScheduled;

        public GpuBlendControl()
            : this(
                App.Services?.GetService(typeof(FrameSyncManager)) as FrameSyncManager,
                App.Services?.GetService(typeof(ILogger<GpuBlendControl>)) as ILogger<GpuBlendControl>
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuBlendControl>.Instance)
        {
        }

        public GpuBlendControl(FrameSyncManager? syncManager, ILogger<GpuBlendControl> logger)
        {
            _syncManager = syncManager;
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
                // 1. D3D11 device
                var d3d = App.Services?.GetService<D3D11DeviceManager>();
                if (d3d is null) { _logger.LogError("D3D11DeviceManager not in DI"); return; }
                if (!d3d.IsInitialized) d3d.Initialize();

                // 2. mpv headless init
                App.Services?.GetKeyedService<VideoPlayerViewModel>("PlayerA")?.InitMpvHeadless();
                App.Services?.GetKeyedService<VideoPlayerViewModel>("PlayerB")?.InitMpvHeadless();

                // 3. SW renderer init (mpv → CPU buffer → D3D11 dynamic texture)
                const int W = 1280;
                const int H = 720;

                var rendererA = App.Services?.GetKeyedService<MpvGlRenderer>("PlayerA");
                var rendererB = App.Services?.GetKeyedService<MpvGlRenderer>("PlayerB");
                rendererA?.Initialize(W, H);
                rendererB?.Initialize(W, H);

                // 4. Blend renderer + frame sync
                if (_syncManager is not null && rendererA is not null && rendererB is not null)
                    _syncManager.Initialize(W, H, rendererA, rendererB);

                // 5. Bitmap for display
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
            if (!_initialized || _bitmap is null) return;

            try
            {
                var renderer = App.Services?.GetService(typeof(BlendRenderer)) as BlendRenderer;
                if (renderer is null) return;

                using var fb = _bitmap.Lock();
                renderer.ReadBackOutput(fb.Address, fb.RowBytes);
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
            {
                context.DrawImage(_bitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));
            }
        }
    }
}
