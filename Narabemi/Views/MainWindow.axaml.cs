using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Narabemi.Services;
using Narabemi.UI.Controls;
using Narabemi.ViewModels;

namespace Narabemi.Views
{
    public partial class MainWindow : Window
    {
        private ControlFadeAnimator? _fadeAnimator;
        private ControlFadeManager? _fadeManager;

        public MainWindow()
        {
            InitializeComponent();
        }

        public void Initialize(ControlFadeManager fadeManager)
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
                splitter.PointerPressed  += OnSplitterPointerPressed;
                splitter.PointerMoved    += OnSplitterPointerMoved;
                splitter.PointerReleased += OnSplitterPointerReleased;
            }

            ApplyVideoLayout();
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged -= OnVmPropertyChanged;
                vm.PropertyChanged += OnVmPropertyChanged;
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

        private const double SplitterPx = 6.0;
        private bool _layoutHorizontal = true;
        private bool _layoutInitialized;

        private void ApplyVideoLayout()
        {
            var grid     = this.FindControl<Grid>("VideoGrid");
            var a        = this.FindControl<VideoPlayerControl>("PlayerAView");
            var b        = this.FindControl<VideoPlayerControl>("PlayerBView");
            var splitter = this.FindControl<Border>("VideoSplitter");
            if (grid is null || a is null || b is null || splitter is null) return;

            var ratio = 0.5;
            var horizontal = true;
            if (DataContext is MainWindowViewModel vm)
            {
                ratio = System.Math.Clamp(vm.BlendRatio, 0.0, 1.0);
                horizontal = vm.BlendMode == 0;
            }

            // Avoid 0-star (collapsed) cells when ratio is at the extremes — keep a sliver
            // so the underlying mpv HWND retains a positive size and continues rendering.
            const double minStar = 0.0001;
            var first  = System.Math.Max(ratio,         minStar);
            var second = System.Math.Max(1.0 - ratio,   minStar);

            // Fast path: if the orientation hasn't changed, just update the existing
            // star widths in-place. Splitter drag fires PointerMoved at high frequency,
            // and Clear/Add of definitions causes Avalonia to re-create the cells each
            // time — measurable jank during a fast drag.
            if (_layoutInitialized && _layoutHorizontal == horizontal)
            {
                if (horizontal)
                {
                    grid.ColumnDefinitions[0].Width = new GridLength(first,  GridUnitType.Star);
                    grid.ColumnDefinitions[2].Width = new GridLength(second, GridUnitType.Star);
                }
                else
                {
                    grid.RowDefinitions[0].Height = new GridLength(first,  GridUnitType.Star);
                    grid.RowDefinitions[2].Height = new GridLength(second, GridUnitType.Star);
                }
                return;
            }

            // Slow path: orientation changed (or first build) — full rebuild.
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();

            if (horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(first,  GridUnitType.Star)));
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(SplitterPx, GridUnitType.Pixel)));
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(second, GridUnitType.Star)));
                Grid.SetRow(a, 0);        Grid.SetColumn(a, 0);
                Grid.SetRow(splitter, 0); Grid.SetColumn(splitter, 1);
                Grid.SetRow(b, 0);        Grid.SetColumn(b, 2);
                splitter.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition(new GridLength(first,  GridUnitType.Star)));
                grid.RowDefinitions.Add(new RowDefinition(new GridLength(SplitterPx, GridUnitType.Pixel)));
                grid.RowDefinitions.Add(new RowDefinition(new GridLength(second, GridUnitType.Star)));
                Grid.SetRow(a, 0);        Grid.SetColumn(a, 0);
                Grid.SetRow(splitter, 1); Grid.SetColumn(splitter, 0);
                Grid.SetRow(b, 2);        Grid.SetColumn(b, 0);
                splitter.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
            }

            _layoutHorizontal = horizontal;
            _layoutInitialized = true;
        }

        private bool _splitterDragging;

        private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (sender is not Border splitter) return;

            // Double-click resets to 50/50 — common direct-manipulation idiom for sliders.
            if (e.ClickCount == 2)
            {
                vm.BlendRatio = 0.5;
                e.Handled = true;
                return;
            }

            _splitterDragging = true;
            e.Pointer.Capture(splitter);
            UpdateRatioFromPointer(e);
            e.Handled = true;
        }

        private void OnSplitterPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_splitterDragging) return;
            UpdateRatioFromPointer(e);
        }

        private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_splitterDragging) return;
            _splitterDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void UpdateRatioFromPointer(PointerEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var grid = this.FindControl<Grid>("VideoGrid");
            if (grid is null) return;

            var p = e.GetPosition(grid);
            var horizontal = vm.BlendMode == 0;
            var size = horizontal ? grid.Bounds.Width : grid.Bounds.Height;
            if (size <= 0) return;
            var coord = horizontal ? p.X : p.Y;

            // Clamp away from the very edges so the user can always grab the splitter back.
            const double minRatio = 0.02;
            const double maxRatio = 0.98;
            vm.BlendRatio = System.Math.Clamp(coord / size, minRatio, maxRatio);
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
                    // Ctrl+O → open file for primary player
                    _ = OpenFileAsync(vm.PrimaryPlayer);
                    e.Handled = true;
                    break;
                case Key.O when ctrl && shift:
                    // Ctrl+Shift+O → open file for secondary player
                    var secondary = vm.MainPlayerIndex == 0 ? vm.PlayerB : vm.PlayerA;
                    _ = OpenFileAsync(secondary);
                    e.Handled = true;
                    break;
            }
        }

        private async System.Threading.Tasks.Task OpenFileAsync(VideoPlayerViewModel target)
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
                var overlay = this.FindControl<Border>("DropOverlay");
                if (overlay is not null)
                    overlay.BorderThickness = new Thickness(3);
                e.DragEffects = DragDropEffects.Copy;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }

        private void OnDragLeave(object? sender, DragEventArgs e)
        {
            var overlay = this.FindControl<Border>("DropOverlay");
            if (overlay is not null)
                overlay.BorderThickness = new Thickness(0);
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            var overlay = this.FindControl<Border>("DropOverlay");
            if (overlay is not null)
                overlay.BorderThickness = new Thickness(0);

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
                vm.ClosedCommand.Execute(null);

            _fadeAnimator?.Dispose();
            _fadeManager?.Dispose();
        }
    }
}
