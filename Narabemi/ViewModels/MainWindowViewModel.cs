using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Narabemi.Settings;

namespace Narabemi.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, IAppStateTarget
    {
        private readonly AppStatesService _appStatesService;
        private readonly ILogger<MainWindowViewModel> _logger;

        [ObservableProperty]
        private GlobalPlaybackState _globalPlaybackState = GlobalPlaybackState.Init;

        [ObservableProperty]
        private bool _loop;

        [ObservableProperty]
        private bool _autoSync = true;

        [ObservableProperty]
        private int _mainPlayerIndex;

        [ObservableProperty]
        private double _blendBorderWidth = 1.0;

        [ObservableProperty]
        private ColorRgba _blendBorderColor = ColorRgba.White;

        [ObservableProperty]
        private double _masterVolume = 1.0;

        [ObservableProperty]
        private bool _isMasterVolumeMuted;

        [ObservableProperty]
        private bool _isControlPanelVisible = true;

        public VideoPlayerViewModel PlayerViewModel { get; }

        // IAppStateTarget
        IList<IAppStatePlayerTarget> IAppStateTarget.StatePlayers => new IAppStatePlayerTarget[] { PlayerViewModel };

        public MainWindowViewModel(
            AppStatesService appStatesService,
            VideoPlayerViewModel playerViewModel,
            ILogger<MainWindowViewModel> logger)
        {
            _appStatesService = appStatesService;
            _logger = logger;
            PlayerViewModel = playerViewModel;

            PlayerViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VideoPlayerViewModel.IsPaused))
                    SyncPlaybackState();
            };
        }

        private void SyncPlaybackState()
        {
            if (GlobalPlaybackState == GlobalPlaybackState.Stop)
                return;

            GlobalPlaybackState = PlayerViewModel.IsPaused
                ? GlobalPlaybackState.Pause
                : GlobalPlaybackState.Play;
        }

        [RelayCommand]
        private void Loaded()
        {
            _appStatesService.ApplyTo(this);
            _logger.LogInformation("State restored from appstates.json");
        }

        [RelayCommand]
        private void Closed()
        {
            _appStatesService.ApplyFrom(this);
            _appStatesService.SaveFile();
            _logger.LogInformation("State saved to appstates.json");
        }

        [RelayCommand]
        private void PlayPause()
        {
            if (GlobalPlaybackState == GlobalPlaybackState.Stop)
            {
                PlayerViewModel.Reopen();
                GlobalPlaybackState = GlobalPlaybackState.Play;
            }
            else
            {
                PlayerViewModel.TogglePause();
            }
        }

        [RelayCommand]
        private void Stop()
        {
            GlobalPlaybackState = GlobalPlaybackState.Stop;
            PlayerViewModel.Stop();
        }

        partial void OnLoopChanged(bool value)
        {
            PlayerViewModel.SetLoop(value);
        }

        partial void OnMasterVolumeChanged(double value)
        {
            PlayerViewModel.UpdateActualVolume(value, IsMasterVolumeMuted);
        }

        partial void OnIsMasterVolumeMutedChanged(bool value)
        {
            PlayerViewModel.UpdateActualVolume(MasterVolume, value);
        }

        [RelayCommand]
        private void VolumeIconMouseDown()
        {
            IsMasterVolumeMuted = !IsMasterVolumeMuted;
        }
    }
}
