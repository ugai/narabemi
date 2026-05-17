using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Narabemi.ViewModels;

namespace Narabemi.Services
{
    /// <summary>
    /// Owns the Win32 drag-poll loop that keeps splitter dragging responsive even
    /// when the cursor crosses into a child mpv HWND (NativeControlHost airspace).
    ///
    /// SetCapture alone doesn't help in that scenario — the cursor entering a child
    /// HWND silently drops Avalonia's pointer events. This controller polls cursor
    /// position and left-button state via Win32 on a 16 ms timer and fires
    /// <see cref="RatioChanged"/> whenever the ratio needs updating, or
    /// <see cref="DragEnded"/> when the button is released outside Avalonia's view.
    /// </summary>
    public sealed partial class SplitterDragController : IDisposable
    {
        // ── Win32 P/Invoke ──────────────────────────────────────────────────────

        [LibraryImport("user32.dll")]
        private static partial IntPtr SetCapture(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReleaseCapture();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT point);

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        private const int VkLButton = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        // ── State ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired (on the UI thread) whenever the drag produces a new ratio value.
        /// The value is already clamped to [<see cref="MainWindowViewModel.BlendRatioMin"/>,
        /// <see cref="MainWindowViewModel.BlendRatioMax"/>].
        /// </summary>
        public event Action<double>? RatioChanged;

        /// <summary>
        /// Fired (on the UI thread) when the controller detects that the left mouse
        /// button was released outside Avalonia's pointer tracking — the caller
        /// should release Avalonia pointer capture and call <see cref="EndDrag"/>.
        /// </summary>
        public event Action? DragEnded;

        private readonly Func<IntPtr> _getWindowHandle;
        private readonly Func<MainWindowViewModel?> _getViewModel;
        private readonly Func<Visual?> _getWindowVisual;
        private readonly Func<Grid?> _getInnerGrid;

        private bool _dragging;
        private System.Threading.Timer? _pollTimer;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <param name="getWindowHandle">Returns the Win32 HWND for Win32 SetCapture.</param>
        /// <param name="getViewModel">Returns the current <see cref="MainWindowViewModel"/>.</param>
        /// <param name="getWindowVisual">Returns the window Visual used for screen→client coordinate conversion.</param>
        /// <param name="getInnerGrid">Returns the InnerVideoGrid used for client→local coordinate conversion.</param>
        public SplitterDragController(
            Func<IntPtr> getWindowHandle,
            Func<MainWindowViewModel?> getViewModel,
            Func<Visual?> getWindowVisual,
            Func<Grid?> getInnerGrid)
        {
            _getWindowHandle = getWindowHandle;
            _getViewModel    = getViewModel;
            _getWindowVisual = getWindowVisual;
            _getInnerGrid    = getInnerGrid;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the Win32 capture and the polling timer.
        /// Call this from the splitter's PointerPressed handler (after Avalonia
        /// pointer capture has been taken).
        /// </summary>
        public void BeginDrag()
        {
            _dragging = true;

            var hwnd = _getWindowHandle();
            if (hwnd != IntPtr.Zero)
                SetCapture(hwnd);

            // System.Threading.Timer runs on the thread pool, so it fires regardless
            // of how the UI dispatcher is occupied. We marshal back to the UI thread
            // for the actual ratio update. This was switched from DispatcherTimer
            // because the latter wasn't reliably ticking once a Win32 mouse capture
            // entered modal-ish behaviour on this hardware.
            _pollTimer?.Dispose();
            _pollTimer = new System.Threading.Timer(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(OnPollTick,
                        Avalonia.Threading.DispatcherPriority.Send),
                null,
                dueTime: 16,
                period: 16);
        }

        /// <summary>
        /// Releases Win32 capture and stops the polling timer.
        /// Call this from PointerReleased / PointerCaptureLost, and also when
        /// <see cref="DragEnded"/> fires.
        /// </summary>
        public void EndDrag()
        {
            _dragging = false;
            ReleaseCapture();
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        /// <summary>
        /// Computes the blend ratio from an Avalonia pointer event position and
        /// fires <see cref="RatioChanged"/>. Use this for both PointerPressed (always)
        /// and PointerMoved (only while dragging — checked internally).
        /// </summary>
        /// <param name="e">The pointer event.</param>
        /// <param name="requireDragging">
        /// When <see langword="true"/> the method is a no-op unless a drag is in progress.
        /// Pass <see langword="false"/> on PointerPressed so the seam responds immediately on click.
        /// </param>
        public void UpdateFromPointer(PointerEventArgs e, bool requireDragging = false)
        {
            if (requireDragging && !_dragging) return;
            var ratio = ComputeRatioFromPointer(e);
            if (ratio.HasValue)
                RatioChanged?.Invoke(ratio.Value);
        }

        public void Dispose() => EndDrag();

        // ── Private ─────────────────────────────────────────────────────────────

        private void OnPollTick()
        {
            if (!_dragging)
            {
                _pollTimer?.Dispose();
                _pollTimer = null;
                return;
            }

            // L-button released? End the drag — PointerReleased won't fire if the
            // cursor was over an mpv HWND when the button came up.
            if ((GetAsyncKeyState(VkLButton) & 0x8000) == 0)
            {
                DragEnded?.Invoke();
                return;
            }

            UpdateFromWin32Cursor();
        }

        private void UpdateFromWin32Cursor()
        {
            var vm    = _getViewModel();
            var inner = _getInnerGrid();
            var win   = _getWindowVisual();
            if (vm is null || inner is null || win is null) return;
            if (!GetCursorPos(out var screenPt)) return;

            // Screen (physical px) → window client (DIPs) → InnerVideoGrid local DIPs.
            var clientPoint = (win as Window)?.PointToClient(new PixelPoint(screenPt.X, screenPt.Y))
                              ?? new Point(screenPt.X, screenPt.Y);
            var pt = win.TranslatePoint(clientPoint, inner) ?? clientPoint;

            var ratio = ClampedRatio(vm, inner, pt);
            if (ratio.HasValue)
                RatioChanged?.Invoke(ratio.Value);
        }

        private double? ComputeRatioFromPointer(PointerEventArgs e)
        {
            var vm    = _getViewModel();
            var inner = _getInnerGrid();
            if (vm is null || inner is null) return null;

            // Compute the ratio relative to InnerVideoGrid (the actual video canvas)
            // rather than the outer VideoGrid, so the seam follows the cursor exactly
            // even when the canvas is centred with letterbox padding around it.
            var pt = e.GetPosition(inner);
            return ClampedRatio(vm, inner, pt);
        }

        private static double? ClampedRatio(MainWindowViewModel vm, Grid inner, Point pt)
        {
            var horizontal = vm.BlendMode == 0;
            var size = horizontal ? inner.Bounds.Width : inner.Bounds.Height;
            if (size <= 0) return null;
            var coord = horizontal ? pt.X : pt.Y;

            // Clamp away from the very edges so the user can always grab the splitter back.
            return System.Math.Clamp(coord / size, MainWindowViewModel.BlendRatioMin, MainWindowViewModel.BlendRatioMax);
        }
    }
}
