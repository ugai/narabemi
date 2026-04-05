using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Bridges a single MpvPlayer (using the OpenGL render API) to a D3D11 shared texture
    /// via the WGL_NV_DX_interop2 extension.
    ///
    /// Each instance owns:
    ///   - A hidden Win32 window + HDC for WGL context creation
    ///   - A WGL OpenGL context
    ///   - A GL texture name registered against the D3D11 texture
    ///   - A GL FBO with the registered texture as color attachment
    ///   - A dedicated render thread (GL contexts are thread-local)
    /// </summary>
    public sealed partial class MpvGlRenderer : IDisposable
    {
        private readonly MpvPlayer _player;
        private readonly D3D11DeviceManager _deviceManager;
        private readonly ILogger<MpvGlRenderer> _logger;

        private GpuTexture? _texture;
        private IntPtr _hwnd;
        private IntPtr _hdc;
        private IntPtr _hglrc;
        private IntPtr _dxInteropDevice;
        private IntPtr _dxInteropObject;
        private uint _glTextureName;
        private uint _fbo;

        private Thread? _renderThread;
        private volatile bool _disposed;
        private volatile bool _frameAvailable;
        private readonly ManualResetEventSlim _renderRequest = new(false);
        private readonly SemaphoreSlim _renderComplete = new(0, 1);

        // Pinned delegate to prevent GC collection while mpv holds the callback pointer
        private MpvRenderUpdateFn? _updateCallback;
        private GCHandle _updateCallbackHandle;

        /// <summary>Fires when mpv has a new frame ready to be rendered.</summary>
        public event Action? FrameRendered;

        /// <summary>The D3D11 texture that mpv renders into.</summary>
        public GpuTexture? Texture => _texture;

        public MpvGlRenderer(MpvPlayer player, D3D11DeviceManager deviceManager, ILogger<MpvGlRenderer> logger)
        {
            _player = player;
            _deviceManager = deviceManager;
            _logger = logger;
        }

        /// <summary>
        /// Initializes the GL context, D3D11 interop, and mpv render context.
        /// Must be called once after MpvPlayer.Init() and D3D11DeviceManager.Initialize().
        /// </summary>
        public void Initialize(int width, int height)
        {
            _texture = _deviceManager.CreateSharedTexture(width, height);

            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "mpv-gl-render",
            };

            // Use a setup semaphore so we know initialization completed before returning
            using var setupDone = new SemaphoreSlim(0, 1);
            Exception? setupEx = null;
            _renderThread.Start((setupDone, (Action<Exception>)(ex => setupEx = ex)));

            setupDone.Wait();
            if (setupEx != null)
                throw new InvalidOperationException("MpvGlRenderer initialization failed.", setupEx);

            _logger.LogInformation("MpvGlRenderer initialized ({W}x{H})", width, height);
        }

        private void RenderLoop(object? state)
        {
            var (setupDone, reportError) = ((SemaphoreSlim, Action<Exception>))state!;

            try
            {
                SetupGlContext();
                SetupMpvRenderContext();
                setupDone.Release();
            }
            catch (Exception ex)
            {
                reportError(ex);
                setupDone.Release();
                return;
            }

            while (!_disposed)
            {
                _renderRequest.Wait(16); // max 16ms wait (~60fps budget)
                _renderRequest.Reset();

                if (_disposed) break;
                if (!_frameAvailable) continue;

                _frameAvailable = false;
                RenderFrame();
            }

            TeardownGlContext();
        }

        private void SetupGlContext()
        {
            // Create a hidden window to obtain an HDC for WGL
            _hwnd = Win32.CreateWindowExW(
                0, "STATIC", "MpvGlHidden", 0,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create hidden window for WGL context.");

            _hdc = Win32.GetDC(_hwnd);

            var pfd = new Win32.PixelFormatDescriptor
            {
                nSize = (ushort)Marshal.SizeOf<Win32.PixelFormatDescriptor>(),
                nVersion = 1,
                dwFlags = Win32.PixelFormatDescriptor.PFD_DRAW_TO_WINDOW
                        | Win32.PixelFormatDescriptor.PFD_SUPPORT_OPENGL
                        | Win32.PixelFormatDescriptor.PFD_DOUBLEBUFFER,
                iPixelType = Win32.PixelFormatDescriptor.PFD_TYPE_RGBA,
                cColorBits = 32,
                cDepthBits = 24,
            };

            int pf = Win32.ChoosePixelFormat(_hdc, ref pfd);
            if (pf == 0 || !Win32.SetPixelFormat(_hdc, pf, ref pfd))
                throw new InvalidOperationException("Failed to set pixel format for WGL context.");

            _hglrc = WglInterop.CreateContext(_hdc);
            if (_hglrc == IntPtr.Zero)
                throw new InvalidOperationException("wglCreateContext failed.");

            WglInterop.MakeCurrent(_hdc, _hglrc);

            // Set up WGL_NV_DX_interop2
            _dxInteropDevice = WglInterop.WglDXOpenDeviceNV(_deviceManager.Device.NativePointer);
            if (_dxInteropDevice == IntPtr.Zero)
                throw new InvalidOperationException("wglDXOpenDeviceNV failed. WGL_NV_DX_interop2 not supported.");

            // Allocate a GL texture name (no GPU memory yet — the interop extension manages that)
            var texNames = new uint[1];
            // glGenTextures is available in opengl32.dll
            GenTextures(texNames);
            _glTextureName = texNames[0];

            // Register D3D11 texture with GL
            _dxInteropObject = WglInterop.WglDXRegisterObjectNV(
                _dxInteropDevice,
                _texture!.Texture.NativePointer,
                _glTextureName,
                WglInterop.GL_TEXTURE_2D,
                WglInterop.WGL_ACCESS_WRITE_DISCARD_NV);

            if (_dxInteropObject == IntPtr.Zero)
                throw new InvalidOperationException("wglDXRegisterObjectNV failed.");

            // Create FBO
            var fbos = new uint[1];
            GlFunctions.GenFramebuffers(1, fbos);
            _fbo = fbos[0];

            // Lock and attach texture
            unsafe
            {
                var obj = _dxInteropObject;
                WglInterop.WglDXLockObjectsNV(_dxInteropDevice, 1, &obj);
            }

            GlFunctions.BindFramebuffer(GlFunctions.GL_FRAMEBUFFER, _fbo);
            GlFunctions.FramebufferTexture2D(
                GlFunctions.GL_FRAMEBUFFER,
                GlFunctions.GL_COLOR_ATTACHMENT0,
                GlFunctions.GL_TEXTURE_2D,
                _glTextureName, 0);

            var status = GlFunctions.CheckFramebufferStatus(GlFunctions.GL_FRAMEBUFFER);
            if (status != GlFunctions.GL_FRAMEBUFFER_COMPLETE)
                throw new InvalidOperationException($"GL FBO incomplete: {status:X}");

            GlFunctions.BindFramebuffer(GlFunctions.GL_FRAMEBUFFER, 0);

            unsafe
            {
                var obj = _dxInteropObject;
                WglInterop.WglDXUnlockObjectsNV(_dxInteropDevice, 1, &obj);
            }
        }

        private void SetupMpvRenderContext()
        {
            // Get proc address via wglGetProcAddress (context must be current)
            MpvOpenGlGetProcAddressFn getProcAddr = (_, namePtr) =>
            {
                var name = Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
                var ptr = WglInterop.GetProcAddress(name);
                if (ptr == IntPtr.Zero)
                {
                    // Some core functions are in opengl32.dll, not the ICD
                    ptr = NativeLibrary.GetExport(NativeLibrary.Load("opengl32.dll"), name) == IntPtr.Zero
                        ? IntPtr.Zero
                        : NativeLibrary.GetExport(NativeLibrary.Load("opengl32.dll"), name);
                }
                return ptr;
            };

            var getProcAddrHandle = GCHandle.Alloc(getProcAddr);
            var initParamsHandle = default(GCHandle);
            var paramArrayHandle = default(GCHandle);

            try
            {
                var initParams = new MpvOpenGlInitParams
                {
                    GetProcAddress = Marshal.GetFunctionPointerForDelegate(getProcAddr),
                    GetProcAddressCtx = IntPtr.Zero,
                };

                initParamsHandle = GCHandle.Alloc(initParams, GCHandleType.Pinned);

                _updateCallback = OnMpvUpdate;
                _updateCallbackHandle = GCHandle.Alloc(_updateCallback);

                // Build param array: [ApiType="opengl", OpenGlInitParams, Terminator]
                var apiTypeStr = Marshal.StringToCoTaskMemAnsi("opengl");
                try
                {
                    var paramArray = new MpvRenderParam[]
                    {
                        new(MpvRenderParamType.ApiType, apiTypeStr),
                        new(MpvRenderParamType.OpenGlInitParams, initParamsHandle.AddrOfPinnedObject()),
                        MpvRenderParam.Terminator,
                    };

                    paramArrayHandle = GCHandle.Alloc(paramArray, GCHandleType.Pinned);
                    _player.CreateRenderContext(paramArrayHandle.AddrOfPinnedObject(), _updateCallback);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(apiTypeStr);
                }
            }
            finally
            {
                getProcAddrHandle.Free();
                if (initParamsHandle.IsAllocated) initParamsHandle.Free();
                if (paramArrayHandle.IsAllocated) paramArrayHandle.Free();
            }
        }

        private void OnMpvUpdate(IntPtr _)
        {
            _frameAvailable = true;
            _renderRequest.Set();
        }

        private void RenderFrame()
        {
            WglInterop.MakeCurrent(_hdc, _hglrc);

            unsafe
            {
                var obj = _dxInteropObject;
                WglInterop.WglDXLockObjectsNV(_dxInteropDevice, 1, &obj);
            }

            try
            {
                _player.RenderFrame((int)_fbo, _texture!.Width, _texture!.Height);
            }
            finally
            {
                unsafe
                {
                    var obj = _dxInteropObject;
                    WglInterop.WglDXUnlockObjectsNV(_dxInteropDevice, 1, &obj);
                }
            }

            _player.ReportSwap();
            FrameRendered?.Invoke();
        }

        private void TeardownGlContext()
        {
            WglInterop.MakeCurrent(_hdc, _hglrc);

            if (_fbo != 0)
            {
                GlFunctions.DeleteFramebuffers(1, new[] { _fbo });
                _fbo = 0;
            }

            if (_dxInteropObject != IntPtr.Zero)
            {
                WglInterop.WglDXUnregisterObjectNV(_dxInteropDevice, _dxInteropObject);
                _dxInteropObject = IntPtr.Zero;
            }

            if (_glTextureName != 0)
            {
                DeleteTextures(new[] { _glTextureName });
                _glTextureName = 0;
            }

            if (_dxInteropDevice != IntPtr.Zero)
            {
                WglInterop.WglDXCloseDeviceNV(_dxInteropDevice);
                _dxInteropDevice = IntPtr.Zero;
            }

            WglInterop.MakeCurrent(IntPtr.Zero, IntPtr.Zero);
            WglInterop.DeleteContext(_hglrc);
            _hglrc = IntPtr.Zero;

            Win32.ReleaseDC(_hwnd, _hdc);
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        // --- GL texture helpers (opengl32.dll core) ---

        [LibraryImport("opengl32.dll", EntryPoint = "glGenTextures")]
        private static partial void GenTextures(int n, uint[] textures);

        private static void GenTextures(uint[] names) => GenTextures(names.Length, names);

        [LibraryImport("opengl32.dll", EntryPoint = "glDeleteTextures")]
        private static partial void DeleteTextures(int n, uint[] textures);

        private static void DeleteTextures(uint[] names) => DeleteTextures(names.Length, names);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderRequest.Set(); // wake up render thread to exit

            _renderThread?.Join(TimeSpan.FromSeconds(2));

            if (_updateCallbackHandle.IsAllocated)
                _updateCallbackHandle.Free();

            _texture?.Dispose();
            _renderRequest.Dispose();
            _logger.LogInformation("MpvGlRenderer disposed");
        }
    }
}
