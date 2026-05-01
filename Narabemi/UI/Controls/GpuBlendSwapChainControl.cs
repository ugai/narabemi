using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Narabemi.Gpu;
using Narabemi.Testing;
using Narabemi.ViewModels;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Narabemi.UI.Controls
{
    /// <summary>
    /// Displays the blended video output via a DXGI swap chain bound directly to the native HWND.
    /// Bypasses Avalonia compositor, Skia, and ANGLE — CS blend result is CopyResource'd to the
    /// swap chain backbuffer and Present'd without any CPU round-trip.
    /// </summary>
    public sealed class GpuBlendSwapChainControl : NativeControlHost
    {
        private readonly FrameSyncManager? _syncManager;
        private readonly BlendRenderer? _blendRenderer;
        private readonly D3D11DeviceManager? _blendDevice;
        private readonly ILogger<GpuBlendSwapChainControl> _logger;

        private IDXGISwapChain1? _swapChain;
        private IntPtr _hwnd;
        private MainWindowViewModel? _vm;

        private bool _initialized;
        private bool _frameScheduled;

        private int _presentFrameCount;
        private long _lastPresentTick;
        private int _frameDropCount;

        public GpuBlendSwapChainControl()
            : this(
                App.Services?.GetService(typeof(FrameSyncManager)) as FrameSyncManager,
                App.Services?.GetService(typeof(BlendRenderer)) as BlendRenderer,
                App.Services?.GetKeyedService<D3D11DeviceManager>("Blend"),
                App.Services?.GetService(typeof(ILogger<GpuBlendSwapChainControl>)) as ILogger<GpuBlendSwapChainControl>
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuBlendSwapChainControl>.Instance)
        {
        }

        public GpuBlendSwapChainControl(
            FrameSyncManager? syncManager,
            BlendRenderer? blendRenderer,
            D3D11DeviceManager? blendDevice,
            ILogger<GpuBlendSwapChainControl> logger)
        {
            _syncManager = syncManager;
            _blendRenderer = blendRenderer;
            _blendDevice = blendDevice;
            _logger = logger;

            // Initialize GPU pipeline in the constructor so devices are always ready before
            // CreateNativeControlCore fires. On some Avalonia rendering paths CreateNativeControlCore
            // can be called before OnAttachedToVisualTree, so initialization must not depend on
            // visual-tree lifecycle ordering.
            try
            {
                InitializePipeline();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GpuBlendSwapChainControl pipeline initialization failed");
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!_initialized) return;

            // Set CPU readback mode here — IsSnapshotMode / IsBenchMode are set in App.axaml.cs
            // after MainWindow is created but before OnFrameworkInitializationCompleted triggers
            // this handler, so the flags are reliably correct at this point.
            if (_syncManager != null)
            {
                bool needsReadback = _vm?.IsSnapshotMode == true && _vm?.IsBenchMode != true;
                _syncManager.SetCpuReadbackEnabled(needsReadback);
            }

            _syncManager!.BlendFrameReady += OnBlendFrameReady;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_syncManager != null)
                _syncManager.BlendFrameReady -= OnBlendFrameReady;
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var handle = base.CreateNativeControlCore(parent);
            _hwnd = handle.Handle;
            try
            {
                CreateSwapChain(_hwnd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GpuBlendSwapChainControl swap chain creation failed");
            }
            return handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            _swapChain?.Dispose();
            _swapChain = null;
            _hwnd = IntPtr.Zero;
            base.DestroyNativeControlCore(control);
        }

        private void InitializePipeline()
        {
            var r1 = App.Services?.GetKeyedService<D3D11DeviceManager>("R1");
            var r2 = App.Services?.GetKeyedService<D3D11DeviceManager>("R2");
            var blendDev = _blendDevice;
            if (r1 is null || r2 is null || blendDev is null)
            {
                _logger.LogError("D3D11DeviceManagers not in DI");
                return;
            }
            if (!r1.IsInitialized) r1.Initialize();
            if (!r2.IsInitialized) r2.Initialize();
            if (!blendDev.IsInitialized) blendDev.Initialize();

            App.Services?.GetKeyedService<VideoPlayerViewModel>("PlayerA")?.InitMpvHeadless();
            App.Services?.GetKeyedService<VideoPlayerViewModel>("PlayerB")?.InitMpvHeadless();

            const int W = 1280;
            const int H = 720;

            var rendererA = App.Services?.GetKeyedService<MpvGlRenderer>("PlayerA");
            var rendererB = App.Services?.GetKeyedService<MpvGlRenderer>("PlayerB");
            rendererA?.Initialize(W, H);
            rendererB?.Initialize(W, H);

            _blendRenderer?.Initialize(W, H);

            if (_syncManager is not null && rendererA is not null && rendererB is not null)
                _syncManager.Initialize(W, H, rendererA, rendererB);

            _vm = App.Services?.GetService<MainWindowViewModel>();
            if (_syncManager != null && _vm != null)
                _syncManager.SetBlendParamsProvider(ReadAndApplyBlendParams);

            // CPU readback mode is set in OnAttachedToVisualTree where IsSnapshotMode /
            // IsBenchMode are already configured. Default in FrameSyncManager is readback
            // enabled, which is correct for snapshot mode.

            _initialized = true;
            _logger.LogInformation("GpuBlendSwapChainControl pipeline initialized ({W}x{H})", W, H);
        }

        private void CreateSwapChain(IntPtr hwnd)
        {
            if (_blendDevice is null || !_blendDevice.IsInitialized || _blendDevice.DxgiFactory is null)
            {
                _logger.LogError("Cannot create swap chain: BlendDevice or DxgiFactory not available");
                return;
            }

            // Fix swap chain dimensions to match the blend output texture (not the HWND size).
            // CopyResource requires identical dimensions between source (OutputTex) and destination
            // (swap chain backbuffer). Scaling.Stretch lets DXGI scale the fixed-size surface to
            // fill the HWND without needing ResizeBuffers on every window resize.
            int w = _blendRenderer?.OutputWidth ?? 1280;
            int h = _blendRenderer?.OutputHeight ?? 720;

            var desc = new SwapChainDescription1
            {
                Width  = (uint)Math.Max(1, w),
                Height = (uint)Math.Max(1, h),
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling    = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode  = AlphaMode.Ignore,
                Flags      = SwapChainFlags.None,
            };

            _swapChain = _blendDevice.DxgiFactory.CreateSwapChainForHwnd(
                _blendDevice.Device, hwnd, desc);
            _logger.LogInformation("DXGI swap chain created ({W}x{H})", desc.Width, desc.Height);
        }

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

        private void OnBlendFrameReady()
        {
            if (_frameScheduled)
            {
                Interlocked.Increment(ref _frameDropCount);
                return;
            }
            _frameScheduled = true;
            Dispatcher.UIThread.Post(PresentFrame, DispatcherPriority.Render);
        }

        private void PresentFrame()
        {
            _frameScheduled = false;
            if (!_initialized || _blendRenderer is null || _blendDevice is null) return;

            // Lazy swap chain creation: handles the case where CreateNativeControlCore fired
            // before devices were ready (swap chain creation failed, _swapChain = null).
            if (_swapChain is null && _hwnd != IntPtr.Zero)
            {
                try { CreateSwapChain(_hwnd); }
                catch (Exception ex) { _logger.LogError(ex, "Swap chain lazy creation failed"); }
            }
            if (_swapChain is null) return;

            try
            {
                lock (_blendDevice.ContextLock)
                {
                    using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                    _blendDevice.Context.CopyResource(backBuffer, _blendRenderer.OutputTex!);
                }

                _swapChain.Present(1, PresentFlags.None);

                long nowTick = Stopwatch.GetTimestamp();
                double intervalMs = _lastPresentTick == 0 ? 0 :
                    (nowTick - _lastPresentTick) * 1000.0 / Stopwatch.Frequency;
                _lastPresentTick = nowTick;

                ++_presentFrameCount;
                _logger.LogInformation(
                    "[Present#{N}] drops={D} | interval={I:F1}ms ({FPS:F1}fps)",
                    _presentFrameCount, _frameDropCount,
                    intervalMs, intervalMs > 0 ? 1000.0 / intervalMs : 0);

                Interlocked.Increment(ref BenchCounters.Presents);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to present frame");
            }
        }
    }
}
