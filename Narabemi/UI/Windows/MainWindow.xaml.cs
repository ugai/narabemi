using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Narabemi.Models;
using Narabemi.Services;
using Narabemi.UI.Controls;

namespace Narabemi.UI.Windows
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly VideoPlayer[] _videoPlayers;

        public MainWindow(
            MainWindowViewModel viewModel,
            MediaElementsManager mediaElementsManager,
            ControlFadeManager controlFadeManager,
            ILogger<MainWindow> logger)
        {
            InitializeComponent();

            _viewModel = viewModel;
            _videoPlayers = MultiVideoGrid.Children.OfType<VideoPlayer>().ToArray();

            mediaElementsManager.MainWindowViewModel = viewModel;

            controlFadeManager.AddAnimationTarget(ControlsGrid);
            controlFadeManager.AddMouseMoveTarget(MultiVideoGrid);
            controlFadeManager.AddMouseMoveTarget(BlendVideoGrid);
            controlFadeManager.AddMouseHoverTarget(ControlsGrid);

            DataContext = _viewModel;
            Title = App.ProductName;

            for (int i = 0; i < _videoPlayers.Length; i++)
            {
                var videoPlayerVM = new VideoPlayerViewModel(logger, _viewModel, i);
                var videoPlayer = _videoPlayers[i];
                videoPlayer.LateInit(videoPlayerVM, logger);
                _viewModel.PlayerViewModels.Add(videoPlayerVM);
                _viewModel.PlayerNames.Add($"Player {(i == 0 ? "Left" : "Right")}");

                mediaElementsManager.Register(i, videoPlayer.MediaElement, videoPlayerVM);

                controlFadeManager.AddAnimationTarget(videoPlayer.ControlsGrid);
                controlFadeManager.AddMouseHoverTarget(videoPlayer.ControlsGrid);
            }
        }

        private void CloseCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
        private void CloseCommandBinding_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
    }

    public partial class MainWindowViewModel : ObservableValidator
    {
        [ObservableProperty]
        private GlobalPlaybackState globalPlaybackState = GlobalPlaybackState.Init;
        [ObservableProperty]
        private bool loop; // TODO: loop is hard.
        [ObservableProperty]
        private bool autoSync;
        [ObservableProperty]
        private int mainPlayerIndex = 0;

        [ObservableProperty]
        private double blendHorizontal = 0.5;
        [ObservableProperty]
        private double blendBorderWidth = 1.0;
        [ObservableProperty]
        private Color blendBorderColor = Colors.White;

        [ObservableProperty]
        [CustomValidation(typeof(MainWindowViewModel), nameof(ValidateDisplayAspectRatio))]
        private AspectRatio displayAspectRatio = AspectRatios.Ratio_16_9;

        [ObservableProperty]
        private double masterVolume = 1.0;
        [ObservableProperty]
        private bool isMasterVolumeMuted = false;

        [ObservableProperty]
        private string shaderFilePath = string.Empty;
        [ObservableProperty]
        private GridLength videoPlayerAColumnWidth = new(1.0, GridUnitType.Star);
        [ObservableProperty]
        private GridLength videoPlayerBColumnWidth = new(1.0, GridUnitType.Star);

        [ObservableProperty]
        private GridLength sideBySideViewGridHeight = new(1.0, GridUnitType.Star);
        [ObservableProperty]
        private GridLength sliderComparisonViewGridHeight = new(2.0, GridUnitType.Star);

        [ObservableProperty]
        private bool isControlPanelVisible = true;

        public List<VideoPlayerViewModel> PlayerViewModels { get; } = new();
        public ObservableCollection<string> PlayerNames { get; } = new();
        public ObservableCollection<AspectRatio> AspectRatioPresets { get; } = new(AspectRatios.All);

        private static readonly AspectRatioToStringConverter _aspectRatioToStringConverter = new();
        private readonly ILogger<MainWindowViewModel> _logger;
        private readonly Settings.AppSettings _appSettings;
        private readonly Settings.AppStatesService _appState;
        private readonly MediaElementsManager _mediaElementsManager;
        private readonly ControlFadeManager _controlFadeManager;
        private readonly object _lockObj = new();
        private readonly DispatcherTimer _autoSyncTimer;

        public MainWindowViewModel(
            ILogger<MainWindowViewModel> logger,
            IConfiguration conf,
            Settings.AppStatesService appState,
            MediaElementsManager mediaElementsManager,
            ControlFadeManager controlFadeManager)
        {
            _logger = logger;
            _appSettings = conf.Get<Settings.AppSettings>();
            _appState = appState;
            _mediaElementsManager = mediaElementsManager;
            _controlFadeManager = controlFadeManager;

            MainPlayerIndex = 0;

            _autoSyncTimer = new()
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(_appSettings.SyncTimerMs, 100))
            };
            _autoSyncTimer.Tick += async (s, e) =>
            {
                if (AutoSync && Monitor.TryEnter(_lockObj))
                {
                    try
                    {
                        await _mediaElementsManager.SpeedAdjustSync();
                    }
                    finally
                    {
                        Monitor.Exit(_lockObj);
                    }
                }
            };
        }

        partial void OnMainPlayerIndexChanged(int value) =>
            _mediaElementsManager.MainPlayerId = value;

        partial void OnGlobalPlaybackStateChanged(GlobalPlaybackState value)
        {
            switch (value)
            {
                case GlobalPlaybackState.Play: _mediaElementsManager?.PlayAllAsync(); break;
                case GlobalPlaybackState.Pause: _mediaElementsManager?.PauseAllAsync(); break;
                case GlobalPlaybackState.Stop: _mediaElementsManager?.StopAllAsync(); break;
            }

            if (AutoSync && value == GlobalPlaybackState.Pause)
                _mediaElementsManager?.SimpleSync();
        }

        partial void OnAutoSyncChanged(bool value)
        {
            _mediaElementsManager.AutoSync = value;

            if (value)
            {
                _autoSyncTimer.Start();
            }
            else
            {
                _autoSyncTimer.Stop();
                if (GlobalPlaybackState == GlobalPlaybackState.Pause)
                    _mediaElementsManager?.SimpleSync();

                _mediaElementsManager?.ResetAllSpeed();
            }
        }

        partial void OnMasterVolumeChanged(double value)
        {
            foreach (var player in PlayerViewModels)
                player.ActualVolume = value * player.LocalVolume;
        }

        partial void OnIsMasterVolumeMutedChanged(bool value)
        {
            foreach (var player in PlayerViewModels)
                player.IsActualVolumeMuted = value || player.IsLocalVolumeMuted;
        }

        [RelayCommand]
        private void Loaded()
        {
            ShaderFilePath = _appSettings.ShaderPath;
            _appState.ApplyTo(this);
        }

        [RelayCommand] private void Closed() => _appState.ApplyFrom(this);
        [RelayCommand] private void PlayPause() => GlobalPlaybackState = GlobalPlaybackState.TogglePlayPause();
        [RelayCommand] private void Stop() => GlobalPlaybackState = GlobalPlaybackState.Stop;
        [RelayCommand] private async Task Sync() => await _mediaElementsManager.SimpleSync();

        [RelayCommand]
        private void TwinPlayerSplitterDoubleClick(MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                VideoPlayerAColumnWidth = new(1.0, GridUnitType.Star);
                VideoPlayerBColumnWidth = new(1.0, GridUnitType.Star);
            }
        }

        [RelayCommand]
        private void VideoMouseDownOrMove(MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && e.Source is FrameworkElement elem)
            {
                var pos = e.GetPosition(elem);
                _logger.LogTrace("MouseMove: [{X}/{ActualWidth}, {Y}/{ActualHeight}]", (int)pos.X, elem.ActualWidth, (int)pos.Y, elem.ActualHeight);
                BlendHorizontal = Math.Clamp(pos.X / elem.ActualWidth, 0.0, 1.0);

                Keyboard.ClearFocus();
            }
        }

        [RelayCommand]
        private void VolumeIconMouseDown(MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                IsMasterVolumeMuted = !IsMasterVolumeMuted;
                e.Handled = true;
            }
        }

        [RelayCommand] private void SetLayoutBothViewt() => SetLayout(1.0, 2.0);
        [RelayCommand] private void SetLayouSideBySideView() => SetLayout(1.0, 0.0);
        [RelayCommand] private void SetLayoutComparisonSliderView() => SetLayout(0.0, 1.0);

        [RelayCommand]
        private static void ShowVersion()
        {
            var mainWindow = App.Services?.GetRequiredService<MainWindow>();
            var versionWindow = App.Services?.GetRequiredService<VersionWindow>();
            if (versionWindow != null)
            {
                versionWindow.Owner = mainWindow;
                versionWindow.ShowDialog();
            }
        }

        private void SetLayout(double sideBySideViewHeight, double sliderComparisonViewHeight)
        {
            SideBySideViewGridHeight = new(sideBySideViewHeight, GridUnitType.Star);
            SliderComparisonViewGridHeight = new(sliderComparisonViewHeight, GridUnitType.Star);
        }

        public static ValidationResult? ValidateDisplayAspectRatio(string value)
        {
            if (_aspectRatioToStringConverter.TryConvertBack(value, out var _))
                return ValidationResult.Success;

            return new("Invalid aspect ratio.");
        }
    }
}
