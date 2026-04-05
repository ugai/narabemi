using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Gpu;

namespace Narabemi.UI.Controls
{
    /// <summary>
    /// Avalonia control that displays the output of a <see cref="FrameSyncManager"/>
    /// via the Avalonia Composition API (CompositionDrawingSurface + ICompositionGpuInterop).
    ///
    /// When a new blended frame is ready (fired from the D3D11 render thread), this control
    /// imports the D3D11 shared texture into Avalonia's composition tree and presents it.
    /// Zero CPU readback: the texture stays on the GPU throughout.
    /// </summary>
    public sealed class GpuBlendControl : Control
    {
        private readonly FrameSyncManager? _syncManager;
        private readonly ILogger<GpuBlendControl> _logger;

        private Compositor? _compositor;
        private CompositionDrawingSurface? _surface;
        private CompositionSurfaceVisual? _surfaceVisual;
        private ICompositionGpuInterop? _gpuInterop;
        private ICompositionImportedGpuImage? _currentImportedImage;

        private bool _initialized;
        private bool _frameScheduled;

        /// <summary>
        /// Parameterless constructor for XAML instantiation. Resolves dependencies from App.Services.
        /// Falls back to no-ops when App.Services is not available (e.g., in unit tests).
        /// </summary>
        public GpuBlendControl()
            : this(
                App.Services?.GetService(typeof(FrameSyncManager)) as FrameSyncManager,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuBlendControl>.Instance)
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
            _ = InitializeCompositionAsync();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_syncManager != null)
                _syncManager.BlendFrameReady -= OnBlendFrameReady;

            _currentImportedImage = null;
            ElementComposition.SetElementChildVisual(this, null);
        }

        private async Task InitializeCompositionAsync()
        {
            var elementVisual = ElementComposition.GetElementVisual(this);
            if (elementVisual is null) return;

            _compositor = elementVisual.Compositor;

            // Request the GPU interop interface — may return null if the backend doesn't support it
            _gpuInterop = await _compositor.TryGetCompositionGpuInterop();

            if (_gpuInterop is null)
            {
                _logger.LogError("ICompositionGpuInterop not available. " +
                    "D3D11 composition requires the GPU-backed Avalonia renderer.");
                return;
            }

            // Check that D3D11 global shared handle import is supported
            if (!_gpuInterop.SupportedImageHandleTypes.Contains(
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle))
            {
                _logger.LogError("D3D11TextureGlobalSharedHandle import not supported by this Avalonia backend.");
                return;
            }

            // Create composition surface and surface visual
            _surface = _compositor.CreateDrawingSurface();
            _surfaceVisual = _compositor.CreateSurfaceVisual();
            _surfaceVisual.Surface = _surface;
            _surfaceVisual.Size = new System.Numerics.Vector2((float)Bounds.Width, (float)Bounds.Height);

            // Attach to the Avalonia visual tree
            ElementComposition.SetElementChildVisual(this, _surfaceVisual);

            _initialized = true;
            if (_syncManager != null)
                _syncManager.BlendFrameReady += OnBlendFrameReady;

            _logger.LogInformation("GpuBlendControl composition initialized");
        }

        private void OnBlendFrameReady()
        {
            // Fires on D3D11 render thread — schedule UI-thread update
            if (_frameScheduled) return;
            _frameScheduled = true;

            Dispatcher.UIThread.Post(PresentFrame, DispatcherPriority.Render);
        }

        private async void PresentFrame()
        {
            _frameScheduled = false;

            if (!_initialized || _gpuInterop is null || _surface is null) return;

            var outputTexture = GetOutputTexture();
            if (outputTexture is null || outputTexture.SharedHandle == IntPtr.Zero) return;

            try
            {
                _currentImportedImage = null;

                var props = new PlatformGraphicsExternalImageProperties
                {
                    Width = outputTexture.Width,
                    Height = outputTexture.Height,
                    Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                };

                var handle = new PlatformHandle(
                    outputTexture.SharedHandle,
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle);

                _currentImportedImage = _gpuInterop.ImportImage(handle, props);
                await _surface.UpdateAsync(_currentImportedImage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to present GPU frame");
            }
        }

        private GpuTexture? GetOutputTexture() =>
            App.Services?.GetService(typeof(BlendRenderer)) is BlendRenderer renderer
                ? renderer.OutputTexture
                : null;

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);

            if (_surfaceVisual is not null)
                _surfaceVisual.Size = new System.Numerics.Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        }
    }

}
