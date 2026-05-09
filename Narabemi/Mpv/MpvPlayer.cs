using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Narabemi.Mpv
{
    /// <summary>
    /// High-level wrapper around a single mpv instance.
    /// Provides play/pause/seek/volume and property observation via a background event loop.
    /// </summary>
    public sealed class MpvPlayer : IDisposable
    {
        private IntPtr _ctx;
        private IntPtr _renderCtx;
        private Thread? _eventThread;
        private volatile bool _disposed;
        private readonly ILogger _logger;

        // Pre-allocated resources for RenderFrameSW — eliminates per-frame heap alloc + GCHandle.Alloc.
        private IntPtr _swFormatStr = IntPtr.Zero;
        private int[] _swSizeArr = Array.Empty<int>();
        private GCHandle _swSizeHandle;

        // Observed property reply IDs
        private const ulong ReplyPosition = 1;
        private const ulong ReplyDuration = 2;
        private const ulong ReplyPause = 3;
        private const ulong ReplyEofReached = 4;

        public event Action<double>? PositionChanged;
        public event Action<double>? DurationChanged;
        public event Action<bool>? PauseChanged;
        public event Action? FileLoaded;
        public event Action? EndOfFile;

        public IntPtr Handle => _ctx;
        public bool IsInitialized => _ctx != IntPtr.Zero;
        public bool HasRenderContext => _renderCtx != IntPtr.Zero;

        public MpvPlayer(ILogger<MpvPlayer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Initializes mpv with native window embedding (legacy --wid mode).
        /// windowId=0 means no window (for render API use after calling InitRenderApi).
        /// </summary>
        public void Init(long windowId = 0)
        {
            _ctx = MpvApi.Create();
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("mpv_create failed");

            if (windowId != 0)
                CheckError(MpvApi.SetOptionString(_ctx, "wid", windowId.ToString()));

            ApplyCommonOptions();
            CheckError(MpvApi.Initialize(_ctx));
            StartEventLoop();
            _logger.LogInformation("mpv initialized (wid={WindowId})", windowId);
        }

        /// <summary>
        /// Initializes mpv with native D3D11 video output and full HW decoding (no CPU roundtrip).
        /// Used by the probe / new dual-HWND architecture.
        /// </summary>
        public void InitNativeD3D11(long windowId)
        {
            _ctx = MpvApi.Create();
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("mpv_create failed");

            CheckError(MpvApi.SetOptionString(_ctx, "wid", windowId.ToString()));
            CheckError(MpvApi.SetOptionString(_ctx, "vo", "gpu"));
            CheckError(MpvApi.SetOptionString(_ctx, "gpu-api", "d3d11"));

            CheckError(MpvApi.SetOptionString(_ctx, "keep-open", "yes"));
            CheckError(MpvApi.SetOptionString(_ctx, "keep-open-pause", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "idle", "yes"));
            CheckError(MpvApi.SetOptionString(_ctx, "input-default-bindings", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "input-vo-keyboard", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "osc", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "osd-level", "0"));

            // True HW decode: GPU-decoded frames stay on the GPU as D3D11 textures,
            // no CPU readback (unlike dxva2-copy which forces sysmem roundtrip).
            CheckError(MpvApi.SetOptionString(_ctx, "hwdec", "d3d11va"));
            CheckError(MpvApi.SetOptionString(_ctx, "vd-lavc-fast", "yes"));

            CheckError(MpvApi.Initialize(_ctx));
            StartEventLoop();
            _logger.LogInformation("mpv initialized (native d3d11, wid={WindowId})", windowId);
        }

        /// <summary>Returns the value of an mpv property as a string, or null if unavailable.</summary>
        public string? GetPropertyStr(string name)
        {
            EnsureInit();
            return MpvApi.GetPropertyStringManaged(_ctx, name);
        }

        /// <summary>
        /// Initializes mpv for use with the render API (no native window).
        /// Call CreateRenderContext() after this to set up OpenGL rendering.
        /// </summary>
        public void InitHeadless()
        {
            _ctx = MpvApi.Create();
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("mpv_create failed");

            // Force libmpv video output (no native window embedding)
            CheckError(MpvApi.SetOptionString(_ctx, "vo", "libmpv"));
            ApplyCommonOptions();
            CheckError(MpvApi.Initialize(_ctx));
            StartEventLoop();
            _logger.LogInformation("mpv initialized (headless / render API mode)");
        }

        /// <summary>
        /// Creates the mpv render context for OpenGL rendering.
        /// Must be called from the thread that owns the GL context (the render thread).
        /// params is a pinned MpvRenderParam[] terminated by MpvRenderParam.Terminator.
        /// </summary>
        public void CreateRenderContext(IntPtr @params, MpvRenderUpdateFn updateCallback)
        {
            EnsureInit();
            CheckError(MpvRenderApi.RenderContextCreate(out _renderCtx, _ctx, @params));
            MpvRenderApi.RenderContextSetUpdateCallback(_renderCtx, updateCallback, IntPtr.Zero);
        }

        /// <summary>
        /// Creates the mpv render context for software (CPU) rendering.
        /// No GL context or window handle required.
        /// </summary>
        public void CreateSWRenderContext(MpvRenderUpdateFn updateCallback)
        {
            EnsureInit();

            var apiTypeStr = Marshal.StringToCoTaskMemAnsi("sw");
            try
            {
                var paramArray = new MpvRenderParam[]
                {
                    new(MpvRenderParamType.ApiType, apiTypeStr),
                    MpvRenderParam.Terminator,
                };

                var handle = GCHandle.Alloc(paramArray, GCHandleType.Pinned);
                try
                {
                    CheckError(MpvRenderApi.RenderContextCreate(out _renderCtx, _ctx, handle.AddrOfPinnedObject()));
                }
                finally { handle.Free(); }
            }
            finally { Marshal.FreeCoTaskMem(apiTypeStr); }

            MpvRenderApi.RenderContextSetUpdateCallback(_renderCtx, updateCallback, IntPtr.Zero);
        }

        /// <summary>
        /// Renders the current video frame into a CPU-side pixel buffer (SW mode).
        /// </summary>
        public unsafe int RenderFrameSW(IntPtr bufferPtr, long strideBytes, int width, int height)
        {
            if (_renderCtx == IntPtr.Zero) return -1;

            // Lazy-init pre-allocated resources (width/height are fixed after first call).
            if (_swFormatStr == IntPtr.Zero)
            {
                _swFormatStr = Marshal.StringToCoTaskMemAnsi("rgba");
                _swSizeArr   = new int[] { width, height };
                _swSizeHandle = GCHandle.Alloc(_swSizeArr, GCHandleType.Pinned);
            }
            _swSizeArr[0] = width;
            _swSizeArr[1] = height;

            var stride = strideBytes;
            var paramArray = new MpvRenderParam[]
            {
                new(MpvRenderParamType.SwSize,    _swSizeHandle.AddrOfPinnedObject()),
                new(MpvRenderParamType.SwFormat,  _swFormatStr),
                new(MpvRenderParamType.SwStride,  (IntPtr)(&stride)),
                new(MpvRenderParamType.SwPointer, bufferPtr),
                MpvRenderParam.Terminator,
            };

            fixed (MpvRenderParam* ptr = paramArray)
                return MpvRenderApi.RenderContextRender(_renderCtx, (IntPtr)ptr);
        }

        /// <summary>
        /// Renders the current video frame into the specified FBO.
        /// Must be called from the thread that owns the GL context.
        /// </summary>
        public unsafe void RenderFrame(int fbo, int width, int height)
        {
            if (_renderCtx == IntPtr.Zero) return;

            var fboDesc = new MpvOpenGlFbo { Fbo = fbo, Width = width, Height = height };
            // flip_y=0: mpv renders top-down (D3D/screen convention).
            // WGL interop shares memory between GL FBO and D3D11 texture;
            // since D3D11 row-0 = top, we want mpv to also place row-0 at the top.
            // flip_y=1 means "video image is inverted" (OpenGL bottom-up convention),
            // which causes double-flip and an upside-down result in D3D11.
            var flipY = 0;

            var fboHandle = GCHandle.Alloc(fboDesc, GCHandleType.Pinned);
            var flipHandle = GCHandle.Alloc(flipY, GCHandleType.Pinned);
            try
            {
                var paramArray = new MpvRenderParam[]
                {
                    new(MpvRenderParamType.OpenGlFbo, fboHandle.AddrOfPinnedObject()),
                    new(MpvRenderParamType.FlipY, flipHandle.AddrOfPinnedObject()),
                    MpvRenderParam.Terminator,
                };

                fixed (MpvRenderParam* ptr = paramArray)
                    MpvRenderApi.RenderContextRender(_renderCtx, (IntPtr)ptr);
            }
            finally
            {
                fboHandle.Free();
                flipHandle.Free();
            }
        }

        /// <summary>
        /// Reports to mpv that the rendered frame has been displayed.
        /// Call after presenting (e.g., after GPU composite).
        /// </summary>
        public void ReportSwap()
        {
            if (_renderCtx != IntPtr.Zero)
                MpvRenderApi.RenderContextReportSwap(_renderCtx);
        }

        private void ApplyCommonOptions()
        {
            CheckError(MpvApi.SetOptionString(_ctx, "keep-open", "yes"));
            CheckError(MpvApi.SetOptionString(_ctx, "keep-open-pause", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "idle", "yes"));
            CheckError(MpvApi.SetOptionString(_ctx, "input-default-bindings", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "input-vo-keyboard", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "osc", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "osd-level", "0"));
            // d3d11va-copy caused D3D11 GPU contention with Blend device (Map p95 12→22ms).
            // dxva2-copy uses the D3D9 driver stack, avoiding that contention while
            // keeping full HW decode quality (bit-exact output, no scaler degradation).
            CheckError(MpvApi.SetOptionString(_ctx, "hwdec", "dxva2-copy"));
            CheckError(MpvApi.SetOptionString(_ctx, "vd-lavc-fast", "yes"));
            // 2 renderers × auto = 16-24 threads total on a 4C/8T system — oversubscribed.
            // 2 threads per renderer caps total decoder threads at 4, leaving headroom.
            CheckError(MpvApi.SetOptionString(_ctx, "vd-lavc-threads", "2"));
        }

        private void StartEventLoop()
        {
            MpvApi.ObserveProperty(_ctx, ReplyPosition, "time-pos", MpvFormat.Double);
            MpvApi.ObserveProperty(_ctx, ReplyDuration, "duration", MpvFormat.Double);
            MpvApi.ObserveProperty(_ctx, ReplyPause, "pause", MpvFormat.Flag);
            MpvApi.ObserveProperty(_ctx, ReplyEofReached, "eof-reached", MpvFormat.Flag);

            _eventThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "mpv-event-loop",
            };
            _eventThread.Start();
        }

        public void LoadFile(string path)
        {
            EnsureInit();
            var err = MpvApi.CommandArgs(_ctx, "loadfile", path, "replace");
            CheckError(err);
            _logger.LogInformation("Loading file: {Path}", path);
        }

        public void Play()
        {
            EnsureInit();
            SetPause(false);
        }

        public void Pause()
        {
            EnsureInit();
            SetPause(true);
        }

        public void TogglePause()
        {
            EnsureInit();
            MpvApi.CommandArgs(_ctx, "cycle", "pause");
        }

        public void Stop()
        {
            EnsureInit();
            MpvApi.CommandArgs(_ctx, "stop");
        }

        public bool Loop
        {
            set
            {
                EnsureInit();
                MpvApi.SetPropertyString(_ctx, "loop-file", value ? "inf" : "no");
            }
        }

        public bool IsPaused
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetPropertyFlag(_ctx, "pause", MpvFormat.Flag, out var val) == 0)
                    return val != 0;
                return true;
            }
        }

        public void Seek(double seconds, bool absolute = true)
        {
            EnsureInit();
            var mode = absolute ? "absolute" : "relative";
            MpvApi.CommandArgs(_ctx, "seek", seconds.ToString("F3"), mode);
        }

        public double Volume
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "volume", MpvFormat.Double, out var val) == 0)
                    return val;
                return 100.0;
            }
            set
            {
                EnsureInit();
                var v = value;
                MpvApi.SetProperty(_ctx, "volume", MpvFormat.Double, ref v);
            }
        }

        public bool IsMuted
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetPropertyFlag(_ctx, "mute", MpvFormat.Flag, out var val) == 0)
                    return val != 0;
                return false;
            }
            set
            {
                EnsureInit();
                var v = value ? 1 : 0;
                MpvApi.SetPropertyFlag(_ctx, "mute", MpvFormat.Flag, ref v);
            }
        }

        public double Position
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "time-pos", MpvFormat.Double, out var val) == 0)
                    return val;
                return 0.0;
            }
        }

        public double Duration
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "duration", MpvFormat.Double, out var val) == 0)
                    return val;
                return 0.0;
            }
        }

        public double Speed
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "speed", MpvFormat.Double, out var val) == 0)
                    return val;
                return 1.0;
            }
            set
            {
                EnsureInit();
                var v = value;
                MpvApi.SetProperty(_ctx, "speed", MpvFormat.Double, ref v);
            }
        }

        private void SetPause(bool paused)
        {
            var val = paused ? 1 : 0;
            MpvApi.SetPropertyFlag(_ctx, "pause", MpvFormat.Flag, ref val);
        }

        private void EventLoop()
        {
            while (!_disposed)
            {
                var evtPtr = MpvApi.WaitEvent(_ctx, 0.5);
                if (evtPtr == IntPtr.Zero) continue;

                var evt = Marshal.PtrToStructure<MpvEvent>(evtPtr);

                switch (evt.EventId)
                {
                    case MpvEventId.None:
                        break;
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.FileLoaded:
                        LogHwdec();
                        FileLoaded?.Invoke();
                        break;
                    case MpvEventId.EndFile:
                        EndOfFile?.Invoke();
                        break;
                    case MpvEventId.PropertyChange:
                        HandlePropertyChange(evt);
                        break;
                }
            }
        }

        private void HandlePropertyChange(MpvEvent evt)
        {
            if (evt.Data == IntPtr.Zero) return;

            var prop = Marshal.PtrToStructure<MpvEventProperty>(evt.Data);
            if (prop.Data == IntPtr.Zero) return;

            switch (evt.ReplyUserdata)
            {
                case ReplyPosition when prop.Format == MpvFormat.Double:
                    PositionChanged?.Invoke(Marshal.PtrToStructure<double>(prop.Data));
                    break;
                case ReplyDuration when prop.Format == MpvFormat.Double:
                    DurationChanged?.Invoke(Marshal.PtrToStructure<double>(prop.Data));
                    break;
                case ReplyPause when prop.Format == MpvFormat.Flag:
                    PauseChanged?.Invoke(Marshal.PtrToStructure<int>(prop.Data) != 0);
                    break;
                case ReplyEofReached when prop.Format == MpvFormat.Flag:
                    if (Marshal.PtrToStructure<int>(prop.Data) != 0)
                        EndOfFile?.Invoke();
                    break;
            }
        }

        private void LogHwdec()
        {
            var hwdec = MpvApi.GetPropertyStringManaged(_ctx, "hwdec-current") ?? "none";
            var codec = MpvApi.GetPropertyStringManaged(_ctx, "video-codec") ?? "?";
            _logger.LogInformation("hwdec-current={Hwdec} video-codec={Codec}", hwdec, codec);
        }

        private void EnsureInit()
        {
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("MpvPlayer is not initialized. Call Init() first.");
        }

        private void CheckError(int errorCode)
        {
            if (errorCode < 0)
            {
                var msg = MpvApi.GetErrorMessage(errorCode) ?? $"mpv error {errorCode}";
                _logger.LogError("mpv error: {Error}", msg);
                throw new InvalidOperationException($"mpv error: {msg}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Render context must be freed before TerminateDestroy
            if (_renderCtx != IntPtr.Zero)
            {
                MpvRenderApi.RenderContextFree(_renderCtx);
                _renderCtx = IntPtr.Zero;
            }

            if (_ctx != IntPtr.Zero)
            {
                MpvApi.TerminateDestroy(_ctx);
                _ctx = IntPtr.Zero;
            }

            _eventThread?.Join(TimeSpan.FromSeconds(2));

            if (_swFormatStr != IntPtr.Zero) { Marshal.FreeCoTaskMem(_swFormatStr); _swFormatStr = IntPtr.Zero; }
            if (_swSizeHandle.IsAllocated) _swSizeHandle.Free();

            _logger.LogInformation("mpv disposed");
        }
    }
}
