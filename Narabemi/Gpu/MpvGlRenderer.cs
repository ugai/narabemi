using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Renders a single MpvPlayer to a D3D11 ShaderResourceView texture using
    /// mpv's OpenGL render API with WGL_NV_DX_interop2 D3D11↔GL texture sharing.
    ///
    /// Each frame: mpv renders directly into a D3D11 texture (via a GL FBO backed
    /// by the shared texture). No CPU round-trip, no Dynamic texture upload.
    ///
    /// Render thread lifecycle:
    ///   Initialize() → creates D3D11 texture → starts render thread
    ///   Render thread → SetupGlContext() → waits for mpv updates → RenderFrame() loop
    ///   Dispose() → signals render thread → TeardownGlContext()
    /// </summary>
    public sealed class MpvGlRenderer : IDisposable
    {
        private readonly MpvPlayer _player;
        private readonly D3D11DeviceManager _deviceManager;
        private readonly ILogger<MpvGlRenderer> _logger;

        // WGL render target (plain Shared — WGL's key=0 binary protocol is incompatible with SharedKeyedMutex)
        private GpuTexture? _wglTexture;

        // Cross-device texture (SharedKeyedMutex) — copy destination from _wglTexture after each frame.
        // Blend reads from this via AcquireSync(1) / ReleaseSync(0).
        private GpuTexture? _copyTexture;

        private int _width;
        private int _height;

        // Render thread state
        private Thread? _renderThread;
        private volatile bool _disposed;
        private volatile bool _frameAvailable;
        private readonly ManualResetEventSlim _renderRequest = new(false);
        private readonly ManualResetEventSlim _glInitDone = new(false);
        private Exception? _glInitException;

        // WGL / DX-interop handles (all accessed only from the render thread)
        private IntPtr _hWnd;
        private IntPtr _hDC;
        private IntPtr _hGLRC;
        private IntPtr _wglDevice;   // HANDLE from wglDXOpenDeviceNV
        private IntPtr _wglObject;   // HANDLE from wglDXRegisterObjectNV
        private uint   _glTexture;   // GL texture name (backed by D3D11 texture)
        private uint   _glFbo;       // GL framebuffer object

        // Pinned callbacks — must outlive the mpv render context
        private MpvRenderUpdateFn?          _updateCallback;
        private MpvOpenGlGetProcAddressFn?  _getProcAddressCallback;
        private GCHandle                    _updateCallbackHandle;
        private GCHandle                    _getProcAddressHandle;

        // NativeLibrary handle for fallback core GL function lookup
        private static readonly IntPtr _opengl32 = NativeLibrary.Load("opengl32.dll");

        // Timing instrumentation
        private static int _instanceCount;
        private readonly string _tag;
        private int _renderFrameCount;
        private long _lastRenderTick;

        /// <summary>Fires on the render thread when a new frame has been rendered to the D3D11 texture.</summary>
        public event Action? FrameRendered;

        /// <summary>The D3D11 SRV for the latest rendered frame (SharedKeyedMutex, cross-device).</summary>
        public GpuTexture? Texture => _copyTexture;

        public MpvGlRenderer(MpvPlayer player, D3D11DeviceManager deviceManager, ILogger<MpvGlRenderer> logger)
        {
            _player = player;
            _deviceManager = deviceManager;
            _logger = logger;
            _tag = "R" + Interlocked.Increment(ref _instanceCount);
        }

        /// <summary>
        /// Creates the D3D11 texture and starts the GL render thread.
        /// Blocks until the WGL context and mpv GL render context are ready.
        /// Must be called after MpvPlayer.InitHeadless().
        /// </summary>
        public void Initialize(int width, int height)
        {
            _width  = width;
            _height = height;

            // Two textures per renderer:
            //   _wglTexture  — plain Shared, WGL render target. WGL uses key=0 binary lock;
            //                  SharedKeyedMutex would conflict, so we use plain Shared here.
            //   _copyTexture — SharedKeyedMutex, cross-device. After each frame: renderer
            //                  acquires key=0, copies _wglTexture→_copyTexture, releases key=1
            //                  so Blend can AcquireSync(1) and read on its own device.
            _wglTexture  = _deviceManager.CreateWglTexture(width, height);
            _copyTexture = _deviceManager.CreateRendererTexture(width, height);

            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "mpv-gl-render",
            };
            _renderThread.Start();

            // Wait for GL setup (SetupGlContext signals _glInitDone when done or on error)
            _glInitDone.Wait();

            if (_glInitException is not null)
                throw new InvalidOperationException("GL render context setup failed", _glInitException);

            _logger.LogInformation("MpvGlRenderer (GL) initialized ({W}x{H})", width, height);
        }

        // Called from native mpv — must never throw.
        private void OnMpvUpdate(IntPtr _)
        {
            try
            {
                _frameAvailable = true;
                _renderRequest.Set();
            }
            catch { /* swallow */ }
        }

        private void RenderLoop()
        {
            try
            {
                SetupGlContext();
            }
            catch (Exception ex)
            {
                _glInitException = ex;
                _glInitDone.Set();
                _logger.LogError(ex, "GL context setup failed");
                return;
            }

            _glInitDone.Set();

            while (!_disposed)
            {
                _renderRequest.Wait(16);
                _renderRequest.Reset();

                if (_disposed) break;
                if (!_frameAvailable) continue;

                _frameAvailable = false;
                try
                {
                    RenderFrame();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GL RenderFrame failed");
                }
            }

            TeardownGlContext();
        }

        private void SetupGlContext()
        {
            // 1. Create a 1×1 hidden window to obtain an HDC for WGL context creation.
            _hWnd = Win32.CreateWindowExW(
                0, "STATIC", "mpv-gl-offscreen", 0,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hWnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowExW failed for WGL offscreen window.");

            _hDC = Win32.GetDC(_hWnd);
            if (_hDC == IntPtr.Zero)
                throw new InvalidOperationException("GetDC failed.");

            // 2. Set a pixel format that supports OpenGL rendering.
            var pfd = new Win32.PixelFormatDescriptor
            {
                nSize        = (ushort)Marshal.SizeOf<Win32.PixelFormatDescriptor>(),
                nVersion     = 1,
                dwFlags      = Win32.PixelFormatDescriptor.PFD_DRAW_TO_WINDOW
                             | Win32.PixelFormatDescriptor.PFD_SUPPORT_OPENGL
                             | Win32.PixelFormatDescriptor.PFD_DOUBLEBUFFER,
                iPixelType   = Win32.PixelFormatDescriptor.PFD_TYPE_RGBA,
                cColorBits   = 32,
                cDepthBits   = 24,
                cStencilBits = 8,
            };
            int pf = Win32.ChoosePixelFormat(_hDC, ref pfd);
            if (pf == 0)
                throw new InvalidOperationException("ChoosePixelFormat failed.");
            if (!Win32.SetPixelFormat(_hDC, pf, ref pfd))
                throw new InvalidOperationException("SetPixelFormat failed.");

            // 3. Create and activate the WGL context.
            _hGLRC = WglInterop.CreateContext(_hDC);
            if (_hGLRC == IntPtr.Zero)
                throw new InvalidOperationException("wglCreateContext failed.");
            if (!WglInterop.MakeCurrent(_hDC, _hGLRC))
                throw new InvalidOperationException("wglMakeCurrent failed.");

            // 4. Open D3D11 device for WGL_NV_DX_interop2.
            _wglDevice = WglInterop.WglDXOpenDeviceNV(_deviceManager.Device.NativePointer);
            if (_wglDevice == IntPtr.Zero)
                throw new InvalidOperationException(
                    "wglDXOpenDeviceNV failed. WGL_NV_DX_interop2 may not be supported on this driver.");

            // 5. Allocate a GL texture name and register the D3D11 texture as a GL object.
            //    WGL_ACCESS_WRITE_DISCARD_NV: GL writes, D3D11 reads (our use case).
            var textures = new uint[1];
            WglInterop.GenTextures(1, textures);
            _glTexture = textures[0];

            _wglObject = WglInterop.WglDXRegisterObjectNV(
                _wglDevice,
                _wglTexture!.Texture.NativePointer,
                _glTexture,
                WglInterop.GL_TEXTURE_2D,
                WglInterop.WGL_ACCESS_WRITE_DISCARD_NV);
            if (_wglObject == IntPtr.Zero)
                throw new InvalidOperationException("wglDXRegisterObjectNV failed.");

            // 6. Create a GL framebuffer and attach the registered texture.
            var fbos = new uint[1];
            GlFunctions.GenFramebuffers(1, fbos);
            _glFbo = fbos[0];
            GlFunctions.BindFramebuffer(GlFunctions.GL_FRAMEBUFFER, _glFbo);
            GlFunctions.FramebufferTexture2D(
                GlFunctions.GL_FRAMEBUFFER,
                GlFunctions.GL_COLOR_ATTACHMENT0,
                GlFunctions.GL_TEXTURE_2D,
                _glTexture,
                0);

            var status = GlFunctions.CheckFramebufferStatus(GlFunctions.GL_FRAMEBUFFER);
            if (status != GlFunctions.GL_FRAMEBUFFER_COMPLETE)
                throw new InvalidOperationException($"GL FBO is incomplete: status=0x{status:X}");

            GlFunctions.BindFramebuffer(GlFunctions.GL_FRAMEBUFFER, 0);

            // 7. Create mpv GL render context.
            //    The proc address callback must remain alive for the lifetime of the render context.
            _getProcAddressCallback = ResolveGlProcAddress;
            _getProcAddressHandle   = GCHandle.Alloc(_getProcAddressCallback);
            _updateCallback         = OnMpvUpdate;
            _updateCallbackHandle   = GCHandle.Alloc(_updateCallback);

            var initParams = new MpvOpenGlInitParams
            {
                GetProcAddress    = Marshal.GetFunctionPointerForDelegate(_getProcAddressCallback),
                GetProcAddressCtx = IntPtr.Zero,
            };
            var initParamsHandle = GCHandle.Alloc(initParams, GCHandleType.Pinned);

            var apiTypeStr  = Marshal.StringToCoTaskMemAnsi("opengl");
            GCHandle paramsHandle = default;
            try
            {
                var paramArray = new MpvRenderParam[]
                {
                    new(MpvRenderParamType.ApiType,         apiTypeStr),
                    new(MpvRenderParamType.OpenGlInitParams, initParamsHandle.AddrOfPinnedObject()),
                    MpvRenderParam.Terminator,
                };
                paramsHandle = GCHandle.Alloc(paramArray, GCHandleType.Pinned);
                _player.CreateRenderContext(paramsHandle.AddrOfPinnedObject(), _updateCallback);
            }
            finally
            {
                if (paramsHandle.IsAllocated) paramsHandle.Free();
                initParamsHandle.Free();
                Marshal.FreeCoTaskMem(apiTypeStr);
            }

            _logger.LogInformation("WGL GL context ready (FBO={Fbo}, glTex={Tex})", _glFbo, _glTexture);
        }

        /// <summary>
        /// Callback for mpv: resolves OpenGL function pointers.
        /// Tries wglGetProcAddress first (extensions), then falls back to opengl32.dll (core GL 1.x).
        /// </summary>
        private IntPtr ResolveGlProcAddress(IntPtr ctx, IntPtr namePtr)
        {
            try
            {
                var name = Marshal.PtrToStringAnsi(namePtr);
                if (name is null) return IntPtr.Zero;

                var ptr = WglInterop.GetProcAddress(name);
                if (ptr != IntPtr.Zero) return ptr;

                // Fallback: core GL 1.x functions live in opengl32.dll directly
                if (NativeLibrary.TryGetExport(_opengl32, name, out var export))
                    return export;

                return IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        private unsafe void RenderFrame()
        {
            // WGL uses a binary key=0 protocol: wglDXLock = D3D releases(0)/GL acquires(0),
            // wglDXUnlock = GL releases(0)/D3D acquires(0). Key never becomes 1.
            // After wglDXUnlock we manually copy _wglTexture → _copyTexture (SharedKeyedMutex)
            // with AcquireSync(0)→CopyResource→Flush→ReleaseSync(1) so Blend can AcquireSync(1).
            var sw = Stopwatch.StartNew();
            var obj = _wglObject;
            if (!WglInterop.WglDXLockObjectsNV(_wglDevice, 1, &obj))
            {
                _logger.LogWarning("WglDXLockObjectsNV failed; skipping frame");
                return;
            }
            long t1 = sw.ElapsedMilliseconds;

            try
            {
                _player.RenderFrame((int)_glFbo, _width, _height);
                long t2 = sw.ElapsedMilliseconds;
                _player.ReportSwap();
                long t3 = sw.ElapsedMilliseconds;

                long nowTick = Stopwatch.GetTimestamp();
                double intervalMs = _lastRenderTick == 0 ? 0 :
                    (nowTick - _lastRenderTick) * 1000.0 / Stopwatch.Frequency;
                _lastRenderTick = nowTick;

                if (++_renderFrameCount % 5 == 0)
                    _logger.LogDebug(
                        "[{Tag}#{N}] wglLock={L}ms render={R}ms swap={S}ms | interval={I:F1}ms ({FPS:F1}fps)",
                        _tag, _renderFrameCount, t1, t2 - t1, t3 - t2,
                        intervalMs, intervalMs > 0 ? 1000.0 / intervalMs : 0);
            }
            finally
            {
                WglInterop.WglDXUnlockObjectsNV(_wglDevice, 1, &obj);
            }

            // Copy _wglTexture → _copyTexture with keyed mutex sync.
            // AcquireSync(0): initial state after creation, and after Blend releases(0).
            if (_copyTexture?.KeyedMutex != null)
            {
                var swCopy = Stopwatch.StartNew();
                int hr = DxgiKeyedMutexHelper.AcquireSync(_copyTexture.KeyedMutex, 0, 500);
                long tAcq = swCopy.ElapsedMilliseconds;
                if (hr == DxgiKeyedMutexHelper.S_OK)
                {
                    _deviceManager.Context.CopyResource(_copyTexture.Texture, _wglTexture!.Texture);
                    long tCopy = swCopy.ElapsedMilliseconds;
                    _deviceManager.Context.Flush();
                    long tFlush = swCopy.ElapsedMilliseconds;
                    _copyTexture.KeyedMutex.ReleaseSync(1);
                    long tRel = swCopy.ElapsedMilliseconds;
                    if (_renderFrameCount % 5 == 0)
                        _logger.LogDebug(
                            "[{Tag}#{N}] copy: acq={A}ms copy={C}ms flush={F}ms rel={R}ms",
                            _tag, _renderFrameCount, tAcq, tCopy - tAcq, tFlush - tCopy, tRel - tFlush);
                }
                else
                {
                    _logger.LogDebug("[{Tag}] AcquireSync(copyTex,0) skipped hr={Hr:X} acqWait={A}ms", _tag, hr, tAcq);
                    return;
                }
            }

            FrameRendered?.Invoke();
        }

        private void TeardownGlContext()
        {
            // Unregister GL object before deleting resources
            if (_wglObject != IntPtr.Zero)
            {
                WglInterop.WglDXUnregisterObjectNV(_wglDevice, _wglObject);
                _wglObject = IntPtr.Zero;
            }

            // Delete GL FBO
            if (_glFbo != 0)
            {
                GlFunctions.DeleteFramebuffers(1, new[] { _glFbo });
                _glFbo = 0;
            }

            // Delete GL texture name (the underlying D3D11 texture is managed separately)
            if (_glTexture != 0)
            {
                WglInterop.DeleteTextures(1, new[] { _glTexture });
                _glTexture = 0;
            }

            // Close DX-interop device handle
            if (_wglDevice != IntPtr.Zero)
            {
                WglInterop.WglDXCloseDeviceNV(_wglDevice);
                _wglDevice = IntPtr.Zero;
            }

            // Detach and destroy the WGL context
            WglInterop.MakeCurrent(IntPtr.Zero, IntPtr.Zero);
            if (_hGLRC != IntPtr.Zero)
            {
                WglInterop.DeleteContext(_hGLRC);
                _hGLRC = IntPtr.Zero;
            }

            // Release DC and destroy the offscreen window
            if (_hDC != IntPtr.Zero && _hWnd != IntPtr.Zero)
            {
                Win32.ReleaseDC(_hWnd, _hDC);
                _hDC = IntPtr.Zero;
            }
            if (_hWnd != IntPtr.Zero)
            {
                Win32.DestroyWindow(_hWnd);
                _hWnd = IntPtr.Zero;
            }

            _logger.LogInformation("WGL GL context torn down");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderRequest.Set();

            _renderThread?.Join(TimeSpan.FromSeconds(3));

            if (_updateCallbackHandle.IsAllocated)    _updateCallbackHandle.Free();
            if (_getProcAddressHandle.IsAllocated)    _getProcAddressHandle.Free();

            _wglTexture?.Dispose();
            _copyTexture?.Dispose();
            _renderRequest.Dispose();
            _glInitDone.Dispose();

            _logger.LogInformation("MpvGlRenderer (GL) disposed");
        }
    }
}
