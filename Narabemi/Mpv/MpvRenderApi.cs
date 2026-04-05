using System;
using System.Runtime.InteropServices;

namespace Narabemi.Mpv
{
    /// <summary>
    /// P/Invoke bindings for the mpv render API (mpv_render_context).
    /// See https://mpv.io/manual/stable/#render-context for the C API reference.
    /// </summary>
    internal static partial class MpvRenderApi
    {
        private const string LibName = "libmpv-2";

        /// <summary>
        /// Creates a render context.
        /// Must be called after mpv_initialize() but before any video is loaded.
        /// </summary>
        [LibraryImport(LibName, EntryPoint = "mpv_render_context_create")]
        internal static partial int RenderContextCreate(out IntPtr ctx, IntPtr mpvHandle, IntPtr @params);

        /// <summary>
        /// Renders a frame into the FBO specified in the render params.
        /// Must be called from the same thread as the GL context.
        /// </summary>
        [LibraryImport(LibName, EntryPoint = "mpv_render_context_render")]
        internal static partial int RenderContextRender(IntPtr ctx, IntPtr @params);

        /// <summary>
        /// Sets a callback for when a new frame is available.
        /// The callback must be non-blocking and post rendering to a separate thread.
        /// </summary>
        [LibraryImport(LibName, EntryPoint = "mpv_render_context_set_update_callback")]
        internal static partial void RenderContextSetUpdateCallback(IntPtr ctx, MpvRenderUpdateFn callback, IntPtr callbackCtx);

        /// <summary>
        /// Reports that the caller has finished displaying the frame.
        /// Call after presenting (e.g., after SwapBuffers).
        /// </summary>
        [LibraryImport(LibName, EntryPoint = "mpv_render_context_report_swap")]
        internal static partial void RenderContextReportSwap(IntPtr ctx);

        /// <summary>
        /// Frees the render context.
        /// Must be called before mpv_terminate_destroy().
        /// </summary>
        [LibraryImport(LibName, EntryPoint = "mpv_render_context_free")]
        internal static partial void RenderContextFree(IntPtr ctx);
    }

    /// <summary>
    /// Callback invoked when a new video frame is available for rendering.
    /// Must be non-blocking; do not call mpv render API functions from within the callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MpvRenderUpdateFn(IntPtr callbackCtx);

    /// <summary>
    /// Callback used for OpenGL function pointer resolution.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr MpvOpenGlGetProcAddressFn(IntPtr ctx, IntPtr name);

    internal enum MpvRenderParamType
    {
        Invalid = 0,
        ApiType = 1,
        OpenGlInitParams = 2,
        OpenGlFbo = 3,
        FlipY = 4,
        Depth = 5,
        IccProfile = 6,
        AmbientLight = 7,
        X11Display = 8,
        WlDisplay = 9,
        AdvancedControl = 10,
        NextFrameInfo = 11,
        BlockForTargetTime = 12,
        SkipRendering = 13,
        DrmDisplay = 14,
        DrmDrawSurfaceSize = 15,
        DrmDisplayV2 = 16,
        SoftwareColorspace = 17,
        EnableDepthBuffer = 18,
    }

    /// <summary>
    /// Type-data pair passed to mpv_render_context_create / mpv_render_context_render.
    /// Array must be terminated with an entry of type=Invalid (0).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvRenderParam
    {
        public MpvRenderParamType Type;
        public IntPtr Data;

        internal MpvRenderParam(MpvRenderParamType type, IntPtr data)
        {
            Type = type;
            Data = data;
        }

        /// <summary>Terminator entry (type=Invalid).</summary>
        internal static MpvRenderParam Terminator => new(MpvRenderParamType.Invalid, IntPtr.Zero);
    }

    /// <summary>
    /// Initialization parameters for MPV_RENDER_API_TYPE_OPENGL.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvOpenGlInitParams
    {
        /// <summary>Function pointer resolver for OpenGL entrypoints.</summary>
        public IntPtr GetProcAddress;

        /// <summary>Context passed as first argument to GetProcAddress.</summary>
        public IntPtr GetProcAddressCtx;
    }

    /// <summary>
    /// OpenGL FBO descriptor passed to MPV_RENDER_PARAM_OPENGL_FBO.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvOpenGlFbo
    {
        /// <summary>OpenGL framebuffer object name (0 for the default framebuffer).</summary>
        public int Fbo;

        /// <summary>Width of the framebuffer in pixels.</summary>
        public int Width;

        /// <summary>Height of the framebuffer in pixels.</summary>
        public int Height;

        /// <summary>
        /// Texture internal format. 0 means GL_RGBA8.
        /// Use 0 unless you know what you are doing.
        /// </summary>
        public int InternalFormat;
    }
}
