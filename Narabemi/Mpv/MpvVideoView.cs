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

        /// <summary>
        /// Raised each time Avalonia creates (or re-creates) the native control.
        /// Callers must handle re-creation: a second fire with a different handle
        /// means the previous HWND is already destroyed.
        /// </summary>
        public event Action<IntPtr>? HandleReady;

        /// <summary>
        /// Raised just before Avalonia destroys the native control, carrying the
        /// handle that is about to be invalidated. Fires before the base destroy
        /// call so the HWND is still technically alive when handlers run.
        /// </summary>
        public event Action<IntPtr>? HandleDestroyed;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _handle = base.CreateNativeControlCore(parent);
            HandleReady?.Invoke(_handle.Handle);
            return _handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            var dying = _handle;
            _handle = null;
            if (dying is not null)
                HandleDestroyed?.Invoke(dying.Handle);
            base.DestroyNativeControlCore(control);
        }
    }
}
