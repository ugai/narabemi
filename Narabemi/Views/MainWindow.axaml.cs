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

            Loaded += OnLoaded;
            Closing += OnClosing;
            KeyDown += OnKeyDown;
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
