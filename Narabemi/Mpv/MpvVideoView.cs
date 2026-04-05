using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Narabemi.Mpv
{
    /// <summary>
    /// Avalonia <see cref="NativeControlHost"/> that provides a native window handle
    /// for mpv to render into via the <c>--wid</c> option.
    /// </summary>
    public class MpvVideoView : NativeControlHost
    {
        private IPlatformHandle? _handle;

        public IntPtr NativeHandle => _handle?.Handle ?? IntPtr.Zero;

        public event Action<IntPtr>? HandleReady;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _handle = base.CreateNativeControlCore(parent);
            HandleReady?.Invoke(_handle.Handle);
            return _handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            _handle = null;
            base.DestroyNativeControlCore(control);
        }
    }
}
