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

            AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            AddHandler(DragDrop.DropEvent, OnDrop);

            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.LoadedCommand.Execute(null);
        }

        private void OnSeekBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.PlayerViewModel.BeginSeek();
        }

        private void OnSeekBarPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var seekBar = this.FindControl<Slider>("SeekBar");
                if (seekBar is not null)
                    vm.PlayerViewModel.SeekTo(seekBar.Value);
                vm.PlayerViewModel.EndSeek();
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

            if (e.Data.GetFiles() is { } files)
            {
                var filePath = files
                    .Select(f => f.TryGetLocalPath())
                    .FirstOrDefault(p => p is not null && File.Exists(p));

                if (filePath is not null)
                    vm.PlayerViewModel.VideoPath = filePath;
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
