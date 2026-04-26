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

        // WGL render targets — double-buffered so the GPU can drain one while we render to the other.
        // Plain Shared (not SharedKeyedMutex) because WGL's key=0 binary protocol conflicts with that.
        // After rendering to _wglTextures[_wglFront], flip _wglFront so the next frame uses the other.
        private readonly GpuTexture?[] _wglTextures = new GpuTexture?[2];
        private int _wglFront;  // render-thread-only; no sync needed

        // Cross-device texture (SharedKeyedMutex) — copy destination from _wglTextures[_wglFront] each frame.
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
        private IntPtr   _hWnd;
        private IntPtr   _hDC;
        private IntPtr   _hGLRC;
        private IntPtr   _wglDevice;          // HANDLE from wglDXOpenDeviceNV
        private IntPtr[] _wglObjects  = new IntPtr[2];  // HANDLEs from wglDXRegisterObjectNV
        private uint[]   _glTextures  = new uint[2];    // GL texture names (backed by D3D11)
        private uint[]   _glFbos      = new uint[2];    // GL FBOs, one per WGL texture

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

            // Double-buffered WGL render targets + one cross-device copy texture:
            //   _wglTextures[0/1] — plain Shared, alternating WGL render targets.
            //                       GPU drains buffer N while we render to buffer N^1,
            //                       eliminating the wglDXLockObjectsNV GPU-drain stall.
            //   _copyTexture      — SharedKeyedMutex, cross-device. After each frame:
            //                       renderer copies _wglTextures[_wglFront]→_copyTexture,
            //                       Blend reads via AcquireSync(1)/ReleaseSync(0).
            _wglTextures[0] = _deviceManager.CreateWglTexture(width, height);
            _wglTextures[1] = _deviceManager.CreateWglTexture(width, height);
            _copyTexture    = _deviceManager.CreateRendererTexture(width, height);

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
            WglInterop.GenTextures(2, _glTextures);

            for (int i = 0; i < 2; i++)
            {
                _wglObjects[i] = WglInterop.WglDXRegisterObjectNV(
                    _wglDevice,
                    _wglTextures[i]!.Texture.NativePointer,
                    _glTextures[i],
                    WglInterop.GL_TEXTURE_2D,
                    WglInterop.WGL_ACCESS_WRITE_DISCARD_NV);
                if (_wglObjects[i] == IntPtr.Zero)
                    throw new InvalidOperationException($"wglDXRegisterObjectNV failed for buffer {i}.");
            }

            // 6. Create two GL framebuffers, each pre-attached to one of the double-buffer textures.
            GlFunctions.GenFramebuffers(2, _glFbos);
            for (int i = 0; i < 2; i++)
            {
                GlFunctions.BindFramebuffer(GlFunctions.GL_FRAMEBUFFER, _glFbos[i]);
                GlFunctions.FramebufferTexture2D(
                    GlFunctions.GL_FRAMEBUFFER,
                    GlFunctions.GL_COLOR_ATTACHMENT0,
                    GlFunctions.GL_TEXTURE_2D,
                    _glTextures[i],
                    0);
                var fboStatus = GlFunctions.CheckFramebufferStatus(GlFunctions.GL_FRAMEBUFFER);
                if (fboStatus != GlFunctions.GL_FRAMEBUFFER_COMPLETE)
                    throw new InvalidOperationException($"GL FBO[{i}] is incomplete: status=0x{fboStatus:X}");
            }
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

            _logger.LogInformation("WGL GL context ready (FBO={Fbo0}/{Fbo1}, glTex={Tex0}/{Tex1})",
                _glFbos[0], _glFbos[1], _glTextures[0], _glTextures[1]);
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
            // Double-buffer: lock _wglObjects[_wglFront], render, unlock. Then copy the front
            // buffer to _copyTexture and flip _wglFront. The GPU drains the previous front buffer
            // while we render to the new front, eliminating the wglDXLock GPU-drain stall.
            int front = _wglFront;
            var sw = Stopwatch.StartNew();
            var obj = _wglObjects[front];
            if (!WglInterop.WglDXLockObjectsNV(_wglDevice, 1, &obj))
            {
                _logger.LogWarning("WglDXLockObjectsNV failed; skipping frame");
                return;
            }
            long t1 = sw.ElapsedMilliseconds;

            try
            {
                _player.RenderFrame((int)_glFbos[front], _width, _height);
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

            // Copy _wglTextures[front] → _copyTexture with keyed mutex sync.
            // AcquireSync(0): initial state after creation, and after Blend releases(0).
            if (_copyTexture?.KeyedMutex != null)
            {
                var swCopy = Stopwatch.StartNew();
                int hr = DxgiKeyedMutexHelper.AcquireSync(_copyTexture.KeyedMutex, 0, 500);
                long tAcq = swCopy.ElapsedMilliseconds;
                if (hr == DxgiKeyedMutexHelper.S_OK)
                {
                    _deviceManager.Context.CopyResource(_copyTexture.Texture, _wglTextures[front]!.Texture);
                    long tCopy = swCopy.ElapsedMilliseconds;
                    _deviceManager.Context.Flush();
                    long tFlush = swCopy.ElapsedMilliseconds;
                    _copyTexture.KeyedMutex.ReleaseSync(1);
                    long tRel = swCopy.ElapsedMilliseconds;
                    _wglFront ^= 1;  // flip: next frame renders to the other buffer
                    if (_renderFrameCount % 5 == 0)
                        _logger.LogInformation(
                            "[{Tag}#{N}] copyAcqWait={A}ms copy={C}ms flush={F}ms rel={R}ms",
                            _tag, _renderFrameCount, tAcq, tCopy - tAcq, tFlush - tCopy, tRel - tFlush);
                }
                else
                {
                    _logger.LogWarning("[{Tag}#{N}] AcquireSync(copyTex,0) skipped hr={Hr:X} acqWait={A}ms", _tag, _renderFrameCount, hr, tAcq);
                    return;
                }
            }

            FrameRendered?.Invoke();
        }

        private void TeardownGlContext()
        {
            // Unregister both GL objects before deleting resources
            for (int i = 0; i < 2; i++)
            {
                if (_wglObjects[i] != IntPtr.Zero)
                {
                    WglInterop.WglDXUnregisterObjectNV(_wglDevice, _wglObjects[i]);
                    _wglObjects[i] = IntPtr.Zero;
                }
            }

            // Delete GL FBOs
            var activeFbos = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Where(_glFbos, f => f != 0));
            if (activeFbos.Length > 0) GlFunctions.DeleteFramebuffers(activeFbos.Length, activeFbos);
            _glFbos[0] = _glFbos[1] = 0;

            // Delete GL texture names (underlying D3D11 textures are managed separately)
            var activeTex = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Where(_glTextures, t => t != 0));
            if (activeTex.Length > 0) WglInterop.DeleteTextures(activeTex.Length, activeTex);
            _glTextures[0] = _glTextures[1] = 0;

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

            _wglTextures[0]?.Dispose();
            _wglTextures[1]?.Dispose();
            // Leave keyed mutex at key=0 so the next process can AcquireSync(0) cleanly.
            // The renderer normally ends in key=1 (after ReleaseSync(1) in RenderFrame).
            if (_copyTexture?.KeyedMutex is not null)
            {
                try { _copyTexture.KeyedMutex.ReleaseSync(0); } catch { /* not held — fine */ }
            }
            _copyTexture?.Dispose();
            _renderRequest.Dispose();
            _glInitDone.Dispose();

            _logger.LogInformation("MpvGlRenderer (GL) disposed");
        }
    }
}
