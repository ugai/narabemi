using System;
using System.Runtime.InteropServices;

namespace Narabemi.Gpu
{
    /// <summary>
    /// P/Invoke bindings for WGL (Windows OpenGL) and the WGL_NV_DX_interop2 extension.
    /// This extension (despite the "NV" name) is supported on all modern Windows GPU drivers
    /// (NVIDIA, AMD, Intel) on Windows 10+ and is the standard way to share D3D11 textures with OpenGL.
    /// </summary>
    internal static partial class WglInterop
    {
        private const string Opengl32 = "opengl32.dll";

        // --- Core WGL ---

        [LibraryImport(Opengl32, EntryPoint = "wglCreateContext")]
        internal static partial IntPtr CreateContext(IntPtr hdc);

        [LibraryImport(Opengl32, EntryPoint = "wglDeleteContext")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteContext(IntPtr hglrc);

        [LibraryImport(Opengl32, EntryPoint = "wglMakeCurrent")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool MakeCurrent(IntPtr hdc, IntPtr hglrc);

        [LibraryImport(Opengl32, EntryPoint = "wglGetCurrentContext")]
        internal static partial IntPtr GetCurrentContext();

        [LibraryImport(Opengl32, EntryPoint = "wglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr GetProcAddress(string procName);

        // --- WGL_NV_DX_interop2 ---
        // These are extension functions; pointers must be obtained at runtime via wglGetProcAddress.

        internal static IntPtr WglDXOpenDeviceNV(IntPtr dxDevice) =>
            CallExt<WglDXOpenDeviceFn>("wglDXOpenDeviceNV")(dxDevice);

        internal static bool WglDXCloseDeviceNV(IntPtr hDevice) =>
            CallExt<WglDXCloseDeviceFn>("wglDXCloseDeviceNV")(hDevice) != 0;

        internal static IntPtr WglDXRegisterObjectNV(IntPtr hDevice, IntPtr dxObject, uint name, uint type, uint access) =>
            CallExt<WglDXRegisterObjectFn>("wglDXRegisterObjectNV")(hDevice, dxObject, name, type, access);

        internal static bool WglDXUnregisterObjectNV(IntPtr hDevice, IntPtr hObject) =>
            CallExt<WglDXUnregisterObjectFn>("wglDXUnregisterObjectNV")(hDevice, hObject) != 0;

        internal static unsafe bool WglDXLockObjectsNV(IntPtr hDevice, int count, IntPtr* hObjects) =>
            CallExt<WglDXLockObjectsFn>("wglDXLockObjectsNV")(hDevice, count, hObjects) != 0;

        internal static unsafe bool WglDXUnlockObjectsNV(IntPtr hDevice, int count, IntPtr* hObjects) =>
            CallExt<WglDXUnlockObjectsFn>("wglDXUnlockObjectsNV")(hDevice, count, hObjects) != 0;

        // WGL_NV_DX_interop access flags
        internal const uint WGL_ACCESS_READ_ONLY_NV = 0x0000;
        internal const uint WGL_ACCESS_READ_WRITE_NV = 0x0001;
        internal const uint WGL_ACCESS_WRITE_DISCARD_NV = 0x0002;

        // GL object types
        internal const uint GL_TEXTURE_2D = 0x0DE1;
        internal const uint GL_TEXTURE_RECTANGLE = 0x84F5;

        // --- Delegate types for extension functions ---

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WglDXOpenDeviceFn(IntPtr dxDevice);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int WglDXCloseDeviceFn(IntPtr hDevice);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WglDXRegisterObjectFn(IntPtr hDevice, IntPtr dxObject, uint name, uint type, uint access);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int WglDXUnregisterObjectFn(IntPtr hDevice, IntPtr hObject);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private unsafe delegate int WglDXLockObjectsFn(IntPtr hDevice, int count, IntPtr* hObjects);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private unsafe delegate int WglDXUnlockObjectsFn(IntPtr hDevice, int count, IntPtr* hObjects);

        private static TDelegate CallExt<TDelegate>(string name) where TDelegate : Delegate
        {
            var ptr = GetProcAddress(name);
            if (ptr == IntPtr.Zero)
                throw new EntryPointNotFoundException($"WGL extension function '{name}' not found. " +
                    "Ensure a valid WGL context is current and WGL_NV_DX_interop2 is supported.");
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(ptr);
        }
    }

    /// <summary>
    /// P/Invoke bindings for OpenGL framebuffer object functions.
    /// These are core OpenGL 3.0 functions obtained via wglGetProcAddress.
    /// </summary>
    internal static class GlFunctions
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GlGenFramebuffersFn(int n, uint[] framebuffers);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GlDeleteFramebuffersFn(int n, uint[] framebuffers);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GlBindFramebufferFn(uint target, uint framebuffer);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GlFramebufferTexture2DFn(uint target, uint attachment, uint texTarget, uint texture, int level);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint GlCheckFramebufferStatusFn(uint target);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint GlGenTexturesFn(int n, uint[] textures);

        internal const uint GL_FRAMEBUFFER = 0x8D40;
        internal const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
        internal const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
        internal const uint GL_TEXTURE_2D = 0x0DE1;

        private static T Get<T>(string name) where T : Delegate
        {
            var ptr = WglInterop.GetProcAddress(name);
            if (ptr == IntPtr.Zero)
                throw new EntryPointNotFoundException($"OpenGL function '{name}' not found.");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        internal static void GenFramebuffers(int n, uint[] fbs) =>
            Get<GlGenFramebuffersFn>("glGenFramebuffers")(n, fbs);

        internal static void DeleteFramebuffers(int n, uint[] fbs) =>
            Get<GlDeleteFramebuffersFn>("glDeleteFramebuffers")(n, fbs);

        internal static void BindFramebuffer(uint target, uint fbo) =>
            Get<GlBindFramebufferFn>("glBindFramebuffer")(target, fbo);

        internal static void FramebufferTexture2D(uint target, uint attachment, uint texTarget, uint texture, int level) =>
            Get<GlFramebufferTexture2DFn>("glFramebufferTexture2D")(target, attachment, texTarget, texture, level);

        internal static uint CheckFramebufferStatus(uint target) =>
            Get<GlCheckFramebufferStatusFn>("glCheckFramebufferStatus")(target);
    }

    /// <summary>
    /// Win32 helpers for creating a hidden window used to obtain an HDC for WGL context creation.
    /// </summary>
    internal static partial class Win32
    {
        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetDC(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        internal static partial int ChoosePixelFormat(IntPtr hdc, ref PixelFormatDescriptor ppfd);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetPixelFormat(IntPtr hdc, int iPixelFormat, ref PixelFormatDescriptor ppfd);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PixelFormatDescriptor
        {
            public ushort nSize;
            public ushort nVersion;
            public uint dwFlags;
            public byte iPixelType;
            public byte cColorBits;
            public byte cRedBits, cRedShift;
            public byte cGreenBits, cGreenShift;
            public byte cBlueBits, cBlueShift;
            public byte cAlphaBits, cAlphaShift;
            public byte cAccumBits;
            public byte cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
            public byte cDepthBits;
            public byte cStencilBits;
            public byte cAuxBuffers;
            public byte iLayerType;
            public byte bReserved;
            public uint dwLayerMask;
            public uint dwVisibleMask;
            public uint dwDamageMask;

            // PFD flags
            internal const uint PFD_DRAW_TO_WINDOW = 0x00000004;
            internal const uint PFD_SUPPORT_OPENGL = 0x00000020;
            internal const uint PFD_DOUBLEBUFFER = 0x00000001;
            internal const byte PFD_TYPE_RGBA = 0;
        }
    }
}
