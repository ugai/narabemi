using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Narabemi.Services;
using Narabemi.Settings;
using Narabemi.UI.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Narabemi.ViewModels;

namespace Narabemi.Views
{
    public partial class MainWindow : Window
    {
        // SetCapture alone doesn't help with NativeControlHost airspace — the cursor
        // crossing into a child HWND still loses the Avalonia drag. Workaround: poll
        // the cursor position and mouse-button state directly via Win32 during drag,
        // so we don't depend on Avalonia receiving any pointer event at all.
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

        private const int VK_LBUTTON = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private System.Threading.Timer? _dragPollTimer;

        private ControlFadeAnimator? _fadeAnimator;
        private ControlFadeManager? _fadeManager;

        private readonly ILogger<MainWindow> _logger;

        // Cached once in Initialize; avoids visual-tree walks on every ApplyVideoLayout call.
        private Grid? _videoGrid;
        private Grid? _innerVideoGrid;
        private VideoPlayerControl? _playerAView;
        private VideoPlayerControl? _playerBView;
        private Border? _videoSplitter;

        // Parameterless ctor kept for the Avalonia designer; chains to the logger ctor
        // so InitializeComponent() is always called exactly once.
        public MainWindow() : this(NullLogger<MainWindow>.Instance) { }

        public MainWindow(ILogger<MainWindow> logger)
        {
            _logger = logger;
            InitializeComponent();
        }

        public void Initialize(ControlFadeManager fadeManager, AppStates? savedState = null)
        {
            _fadeManager = fadeManager;
            _fadeAnimator = new ControlFadeAnimator();

            var controlPanel = this.FindControl<Border>("ControlPanel");
            if (controlPanel is not null)
            {
                _fadeAnimator.AddTarget(controlPanel);
                _fadeManager.AddMouseHoverTarget(controlPanel);
            }

            _fadeManager.AddMouseMoveTarget(this);

            var seekBar = this.FindControl<Slider>("SeekBar");
            if (seekBar is not null)
            {
                // Tunnel to catch pointer before Slider's internal handling
                seekBar.AddHandler(PointerPressedEvent, OnSeekBarPointerPressed, RoutingStrategies.Tunnel);
                seekBar.AddHandler(PointerReleasedEvent, OnSeekBarPointerReleased, RoutingStrategies.Tunnel);
            }

            var blendModeButton = this.FindControl<Button>("BlendModeButton");
            if (blendModeButton is not null)
                blendModeButton.Click += OnBlendModeButtonClick;

            AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            AddHandler(DragDrop.DropEvent, OnDrop);

            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Closing += OnClosing;
            KeyDown += OnKeyDown;

            var splitter = this.FindControl<Border>("VideoSplitter");
            if (splitter is not null)
            {
                splitter.PointerPressed     += OnSplitterPointerPressed;
                splitter.PointerMoved       += OnSplitterPointerMoved;
                splitter.PointerReleased    += OnSplitterPointerReleased;
                splitter.PointerCaptureLost += OnSplitterPointerCaptureLost;
            }

            // Window/video-area resize must re-fit the aspect-locked inner grid.
            var videoGrid = this.FindControl<Grid>("VideoGrid");
            if (videoGrid is not null)
                videoGrid.SizeChanged += (_, _) => ApplyVideoLayout();

            // Cache control references so ApplyVideoLayout avoids repeated visual-tree walks.
            _videoGrid    = videoGrid;
            _innerVideoGrid = this.FindControl<Grid>("InnerVideoGrid");
            _playerAView  = this.FindControl<VideoPlayerControl>("PlayerAView");
            _playerBView  = this.FindControl<VideoPlayerControl>("PlayerBView");
            _videoSplitter = splitter;

            // DataContext is typically set BEFORE Initialize runs (App.axaml.cs sets it,
            // then calls Initialize). The DataContextChanged event won't fire for that
            // initial value, so wire up the subscriptions manually here.
            OnDataContextChanged(this, System.EventArgs.Empty);

            ApplyVideoLayout();

            // Restore window geometry before the window is shown so there is no
            // visible reposition flicker. Width==0 means "first run, use XAML defaults".
            if (savedState is { WindowWidth: > 0 })
            {
                Width  = savedState.WindowWidth;
                Height = savedState.WindowHeight;

                // Only restore the saved position when enough of the window rect
                // intersects the current display configuration. If the user unplugged
                // a secondary monitor between sessions the window would otherwise open
                // fully offscreen with no UI affordance to move it back.
                var savedRect = new PixelRect(savedState.WindowX, savedState.WindowY,
                    (int)savedState.WindowWidth, (int)savedState.WindowHeight);
                if (IsRectSufficientlyOnScreen(savedRect))
                    Position = new PixelPoint(savedState.WindowX, savedState.WindowY);

                if (savedState.IsWindowMaximized)
                    WindowState = WindowState.Maximized;
            }
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged -= OnVmPropertyChanged;
                vm.PropertyChanged += OnVmPropertyChanged;
                // Re-fit InnerVideoGrid once each player's source aspect is known.
                vm.PlayerA.VideoReady -= ApplyVideoLayout;
                vm.PlayerA.VideoReady += ApplyVideoLayout;
                vm.PlayerB.VideoReady -= ApplyVideoLayout;
                vm.PlayerB.VideoReady += ApplyVideoLayout;
                ApplyVideoLayout();
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.BlendMode) ||
                e.PropertyName == nameof(MainWindowViewModel.BlendRatio))
            {
                ApplyVideoLayout();
            }
        }

        // 4px wide is the smallest reliably clickable splitter at HiDPI 2.5x (≈10
        // physical px). 1px was a clean visual but couldn't actually be clicked —
        // PointerPressed never fired, so the drag never started. The Border now
        // fills the entire cell with a single semi-transparent white fill, so
        // there are no internal transparent gaps to leak the parent's black
        // background through (which was the user's earlier complaint).
        private const double SplitterPx = 4.0;
        private const double DefaultAspect = 16.0 / 9.0;  // used until a video loads
        private bool _layoutHorizontal = true;
        private bool _layoutInitialized;

        // Cached cursors — Cursor implements IDisposable; allocating one per layout
        // rebuild or per drag tick would leak the previous instance.
        private static readonly Avalonia.Input.Cursor CursorSizeWestEast =
            new(Avalonia.Input.StandardCursorType.SizeWestEast);
        private static readonly Avalonia.Input.Cursor CursorSizeNorthSouth =
            new(Avalonia.Input.StandardCursorType.SizeNorthSouth);

        /// <summary>
        /// Computes the InnerVideoGrid's pixel size (matched to source aspect) and the
        /// per-cell pixel widths/heights so the cropped video fills each cell with no
        /// internal mpv letterbox — that's what makes the wipe seam line up.
        /// </summary>
        private void ApplyVideoLayout()
        {
            var outer    = _videoGrid;
            var inner    = _innerVideoGrid;
            var a        = _playerAView;
            var b        = _playerBView;
            var splitter = _videoSplitter;
            if (outer is null || inner is null || a is null || b is null || splitter is null) return;

            var ratio = 0.5;
            var horizontal = true;
            var aspect = DefaultAspect;
            if (DataContext is MainWindowViewModel vm)
            {
                ratio = System.Math.Clamp(vm.BlendRatio, 0.0, 1.0);
                horizontal = vm.BlendMode == 0;
                // Prefer Player A's source aspect; fall back to B; else 16:9.
                if (vm.PlayerA.SourceWidth > 0 && vm.PlayerA.SourceHeight > 0)
                    aspect = (double)vm.PlayerA.SourceWidth / vm.PlayerA.SourceHeight;
                else if (vm.PlayerB.SourceWidth > 0 && vm.PlayerB.SourceHeight > 0)
                    aspect = (double)vm.PlayerB.SourceWidth / vm.PlayerB.SourceHeight;
            }

            // Outer bounds give us the available video area. SizeChanged subscription
            // re-fires this on resize.
            var availW = outer.Bounds.Width;
            var availH = outer.Bounds.Height;
            if (availW <= 0 || availH <= 0) return;

            // Fit a rectangle of the source aspect into the available area, leaving
            // room for the splitter pixel band along the split axis.
            double dispW, dispH;
            if (horizontal)
            {
                // Effective "video canvas" width must fit (W + SplitterPx) into availW.
                var fitH = availH;
                var fitW = fitH * aspect;
                if (fitW + SplitterPx > availW)
                {
                    fitW = availW - SplitterPx;
                    fitH = fitW / aspect;
                }
                dispW = fitW;
                dispH = fitH;
            }
            else
            {
                var fitW = availW;
                var fitH = fitW / aspect;
                if (fitH + SplitterPx > availH)
                {
                    fitH = availH - SplitterPx;
                    fitW = fitH * aspect;
                }
                dispW = fitW;
                dispH = fitH;
            }

            // Per-cell pixel sizes. Clamp mirrors the splitter drag clamp.
            var r = System.Math.Clamp(ratio, MainWindowViewModel.BlendRatioMin, MainWindowViewModel.BlendRatioMax);
            var cellAFirst  = horizontal
                ? System.Math.Round(dispW * r)
                : System.Math.Round(dispH * r);
            var cellAxis    = horizontal ? dispW : dispH;
            var cellBFirst  = cellAxis - cellAFirst;   // pixel-exact complement

            // Inner grid is sized to displayed-source dims plus the splitter; the outer
            // grid centers it.
            inner.Width  = horizontal ? (dispW + SplitterPx) : dispW;
            inner.Height = horizontal ? dispH : (dispH + SplitterPx);

            // Fast path: orientation unchanged → mutate existing definitions in place.
            // (Splitter drag fires high-frequency BlendRatio changes; Clear/Add of
            // ColumnDefinitions causes Avalonia to re-create cells each tick.)
            if (_layoutInitialized && _layoutHorizontal == horizontal)
            {
                if (horizontal)
                {
                    inner.ColumnDefinitions[0].Width = new GridLength(cellAFirst, GridUnitType.Pixel);
                    inner.ColumnDefinitions[2].Width = new GridLength(cellBFirst, GridUnitType.Pixel);
                }
                else
                {
                    inner.RowDefinitions[0].Height = new GridLength(cellAFirst, GridUnitType.Pixel);
                    inner.RowDefinitions[2].Height = new GridLength(cellBFirst, GridUnitType.Pixel);
                }
                return;
            }

            // Slow path: orientation changed (or first build) — full rebuild.
            inner.RowDefinitions.Clear();
            inner.ColumnDefinitions.Clear();

            if (horizontal)
            {
                inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(cellAFirst, GridUnitType.Pixel)));
                inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(SplitterPx, GridUnitType.Pixel)));
                inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(cellBFirst, GridUnitType.Pixel)));
                Grid.SetRow(a, 0);        Grid.SetColumn(a, 0);
                Grid.SetRow(splitter, 0); Grid.SetColumn(splitter, 1);
                Grid.SetRow(b, 0);        Grid.SetColumn(b, 2);
                splitter.Cursor = CursorSizeWestEast;
            }
            else
            {
                inner.RowDefinitions.Add(new RowDefinition(new GridLength(cellAFirst, GridUnitType.Pixel)));
                inner.RowDefinitions.Add(new RowDefinition(new GridLength(SplitterPx, GridUnitType.Pixel)));
                inner.RowDefinitions.Add(new RowDefinition(new GridLength(cellBFirst, GridUnitType.Pixel)));
                Grid.SetRow(a, 0);        Grid.SetColumn(a, 0);
                Grid.SetRow(splitter, 1); Grid.SetColumn(splitter, 0);
                Grid.SetRow(b, 2);        Grid.SetColumn(b, 0);
                splitter.Cursor = CursorSizeNorthSouth;
            }

            _layoutHorizontal = horizontal;
            _layoutInitialized = true;
        }

        private bool _splitterDragging;

        private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (sender is not Control splitter) return;

            // Double-click resets to 50/50 — common direct-manipulation idiom for sliders.
            if (e.ClickCount == 2)
            {
                vm.BlendRatio = 0.5;
                e.Handled = true;
                return;
            }

            _splitterDragging = true;
            e.Pointer.Capture(splitter);

            // Defensive Win32 capture so any in-Avalonia pointer events still get
            // routed correctly; the polling timer below is the real workhorse.
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero) SetCapture(hwnd);

            // System.Threading.Timer runs on the thread pool, so it fires regardless
            // of how the UI dispatcher is occupied. We marshal back to the UI thread
            // for the actual ratio update. This was switched from DispatcherTimer
            // because the latter wasn't reliably ticking once a Win32 mouse capture
            // entered modal-ish behavior on this hardware.
            _dragPollTimer?.Dispose();
            _dragPollTimer = new System.Threading.Timer(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(OnDragPollTick,
                        Avalonia.Threading.DispatcherPriority.Send),
                null,
                16, 16);

            UpdateRatioFromPointer(e);
            e.Handled = true;
        }

        private void OnDragPollTick()
        {
            if (!_splitterDragging) { _dragPollTimer?.Dispose(); _dragPollTimer = null; return; }

            // L-button released? End the drag — PointerReleased won't fire if the
            // cursor was over an mpv HWND when the button came up.
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            {
                EndSplitterDrag(null);
                return;
            }

            UpdateRatioFromCursor();
        }

        private void UpdateRatioFromCursor()
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var inner = _innerVideoGrid;
            if (inner is null) return;
            if (!GetCursorPos(out var screenPt)) return;

            // Screen (physical px) → window client (DIPs) → InnerVideoGrid local DIPs.
            var clientPoint = this.PointToClient(new PixelPoint(screenPt.X, screenPt.Y));
            var pt = this.TranslatePoint(clientPoint, inner) ?? clientPoint;

            var horizontal = vm.BlendMode == 0;
            var size = horizontal ? inner.Bounds.Width : inner.Bounds.Height;
            if (size <= 0) return;
            var coord = horizontal ? pt.X : pt.Y;

            vm.BlendRatio = System.Math.Clamp(coord / size, MainWindowViewModel.BlendRatioMin, MainWindowViewModel.BlendRatioMax);
        }

        private void OnSplitterPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_splitterDragging) return;
            UpdateRatioFromPointer(e);
        }

        private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_splitterDragging) return;
            EndSplitterDrag(e.Pointer);
            e.Handled = true;
        }

        private void OnSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            // Alt-Tab, popup, etc. can yank Avalonia's pointer capture mid-drag. Make
            // sure we also drop the Win32 capture so the cursor doesn't get stuck.
            if (_splitterDragging)
                EndSplitterDrag(e.Pointer);
        }

        private void EndSplitterDrag(IPointer? pointer)
        {
            _splitterDragging = false;
            pointer?.Capture(null);
            ReleaseCapture();
            _dragPollTimer?.Dispose();
            _dragPollTimer = null;
        }

        private void UpdateRatioFromPointer(PointerEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            // Compute the ratio relative to the InnerVideoGrid (the actual video canvas)
            // rather than the outer VideoGrid, so the seam follows the cursor exactly
            // even when the canvas is centered with letterbox padding around it.
            var inner = _innerVideoGrid;
            if (inner is null) return;

            var p = e.GetPosition(inner);
            var horizontal = vm.BlendMode == 0;
            var size = horizontal ? inner.Bounds.Width : inner.Bounds.Height;
            if (size <= 0) return;
            var coord = horizontal ? p.X : p.Y;

            // Clamp away from the very edges so the user can always grab the splitter back.
            vm.BlendRatio = System.Math.Clamp(coord / size, MainWindowViewModel.BlendRatioMin, MainWindowViewModel.BlendRatioMax);
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.LoadedCommand.Execute(null);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var ctrl  = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            switch (e.Key)
            {
                case Key.Space:
                    vm.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    vm.StopCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Left:
                    vm.SeekRelative(shift ? -30.0 : -5.0);
                    e.Handled = true;
                    break;
                case Key.Right:
                    vm.SeekRelative(shift ? 30.0 : 5.0);
                    e.Handled = true;
                    break;
                case Key.O when ctrl && !shift:
                    // Ctrl+O → open file for primary player. Fire-and-forget is safe:
                    // OpenFileAsync catches all exceptions internally and logs them.
                    _ = OpenFileAsync(vm.PrimaryPlayer);
                    e.Handled = true;
                    break;
                case Key.O when ctrl && shift:
                    // Ctrl+Shift+O → open file for secondary player. Fire-and-forget is safe:
                    // OpenFileAsync catches all exceptions internally and logs them.
                    var secondary = vm.MainPlayerIndex == 0 ? vm.PlayerB : vm.PlayerA;
                    _ = OpenFileAsync(secondary);
                    e.Handled = true;
                    break;
                case Key.H:
                    vm.ToggleBlendMode();
                    e.Handled = true;
                    break;
                case Key.R:
                    vm.ResetSplit();
                    e.Handled = true;
                    break;
                case Key.S when !ctrl && !shift:
                    SaveSnapshot(vm);
                    e.Handled = true;
                    break;
            }
        }

        private void SaveSnapshot(MainWindowViewModel vm)
        {
            // Determine output directory: prefer the folder containing PlayerA's video;
            // fall back to Pictures if no video is loaded.
            var videoPath = vm.PlayerA.VideoPath;
            var outDir = !string.IsNullOrEmpty(videoPath) && File.Exists(videoPath)
                ? Path.GetDirectoryName(videoPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var baseName = !string.IsNullOrEmpty(videoPath)
                ? Path.GetFileNameWithoutExtension(videoPath)
                : "snapshot";

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var pathA = Path.Combine(outDir, $"{baseName}_snapshot_{timestamp}_a.png");
            var pathB = Path.Combine(outDir, $"{baseName}_snapshot_{timestamp}_b.png");

            var retA = vm.PlayerA.MpvPlayer.SnapshotToFile(pathA);
            if (retA != 0)
                System.Diagnostics.Debug.WriteLine($"[Snapshot] PlayerA SnapshotToFile returned {retA} for '{pathA}'");

            var retB = vm.PlayerB.MpvPlayer.SnapshotToFile(pathB);
            if (retB != 0)
                System.Diagnostics.Debug.WriteLine($"[Snapshot] PlayerB SnapshotToFile returned {retB} for '{pathB}'");
        }

        private async System.Threading.Tasks.Task OpenFileAsync(VideoPlayerViewModel target)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open video file",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Video files")
                        {
                            Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv",
                                               "*.flv", "*.webm", "*.m4v", "*.ts", "*.mts" },
                        },
                        new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
                    },
                });

                if (files.Count > 0)
                {
                    var path = files[0].TryGetLocalPath();
                    if (path is not null)
                        target.VideoPath = path;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File picker failed");
            }
        }
        private void OnBlendModeButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.BlendMode = vm.BlendMode == 0 ? 1 : 0;
        }

        private void OnSeekBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.PrimaryPlayer.BeginSeek();
        }

        private void OnSeekBarPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var seekBar = this.FindControl<Slider>("SeekBar");
                if (seekBar is not null)
                    vm.SeekBoth(seekBar.Value);
                vm.PrimaryPlayer.EndSeek();
            }
        }

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data.Contains(DataFormats.Files))
            {
                DropBorder.BorderThickness = new Thickness(3);
                e.DragEffects = DragDropEffects.Copy;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }

        private void OnDragLeave(object? sender, DragEventArgs e)
        {
            DropBorder.BorderThickness = new Thickness(0);
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            DropBorder.BorderThickness = new Thickness(0);

            if (DataContext is not MainWindowViewModel vm)
                return;

            if (e.Data.GetFiles() is not { } files)
                return;

            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => p is not null && File.Exists(p))
                .ToList();

            if (paths.Count == 0)
                return;

            if (paths.Count >= 2)
            {
                // Two files: first → PlayerA, second → PlayerB
                vm.PlayerA.VideoPath = paths[0]!;
                vm.PlayerB.VideoPath = paths[1]!;
            }
            else
            {
                // One file: route by drop position (left half → PlayerA, right half → PlayerB)
                var dropX = e.GetPosition(this).X;
                var target = dropX < Bounds.Width / 2 ? vm.PlayerA : vm.PlayerB;
                target.VideoPath = paths[0]!;
            }
        }

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                SaveWindowState(vm.AppStates);
                vm.ClosedCommand.Execute(null);
            }

            _fadeAnimator?.Dispose();
            _fadeManager?.Dispose();
        }

        private void SaveWindowState(AppStates? states)
        {
            if (states is null) return;
            states.IsWindowMaximized = WindowState == WindowState.Maximized;
            if (!states.IsWindowMaximized)
            {
                states.WindowWidth  = Width;
                states.WindowHeight = Height;
                states.WindowX      = Position.X;
                states.WindowY      = Position.Y;
            }
        }

        /// <summary>
        /// Returns true when at least 25 % of <paramref name="rect"/> falls inside
        /// the union of all current screen working areas. Below that threshold the
        /// window is considered effectively offscreen (e.g. a secondary monitor was
        /// disconnected) and the caller should fall back to a default position.
        /// </summary>
        private bool IsRectSufficientlyOnScreen(PixelRect rect)
        {
            const double MinVisibleFraction = 0.25;

            var screens = Screens.All;
            if (screens is null || screens.Count == 0)
                return true; // cannot determine — assume on-screen

            long intersectionArea = 0;
            foreach (var screen in screens)
            {
                var wa = screen.WorkingArea;
                var ix = Math.Max(rect.X, wa.X);
                var iy = Math.Max(rect.Y, wa.Y);
                var iw = Math.Min(rect.Right, wa.Right) - ix;
                var ih = Math.Min(rect.Bottom, wa.Bottom) - iy;
                if (iw > 0 && ih > 0)
                    intersectionArea += (long)iw * ih;
            }

            var rectArea = (long)rect.Width * rect.Height;
            return rectArea > 0 && intersectionArea >= (long)(rectArea * MinVisibleFraction);
        }
    }
}
