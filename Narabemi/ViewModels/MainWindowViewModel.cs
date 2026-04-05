using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Narabemi.Gpu;
using Narabemi.Settings;

namespace Narabemi.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, IAppStateTarget
    {
        private readonly AppStatesService _appStatesService;
        private readonly BlendRenderer? _blendRenderer;
        private readonly FrameSyncManager? _frameSyncManager;
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
        private double _blendRatio = 0.5;

        [ObservableProperty]
        private int _blendMode;

        [ObservableProperty]
        private double _masterVolume = 1.0;

        [ObservableProperty]
        private bool _isMasterVolumeMuted;

        [ObservableProperty]
        private bool _isControlPanelVisible = true;

        public VideoPlayerViewModel PlayerA { get; }
        public VideoPlayerViewModel PlayerB { get; }

        // Convenience alias: the "primary" player drives the seek bar, duration, etc.
        public VideoPlayerViewModel PrimaryPlayer => MainPlayerIndex == 0 ? PlayerA : PlayerB;

        // IAppStateTarget
        IList<IAppStatePlayerTarget> IAppStateTarget.StatePlayers =>
            new IAppStatePlayerTarget[] { PlayerA, PlayerB };

        double IAppStateTarget.BlendRatio
        {
            get => BlendRatio;
            set => BlendRatio = value;
        }

        int IAppStateTarget.BlendMode
        {
            get => BlendMode;
            set => BlendMode = value;
        }

        public MainWindowViewModel(
            AppStatesService appStatesService,
            [Microsoft.Extensions.DependencyInjection.FromKeyedServices("PlayerA")] VideoPlayerViewModel playerA,
            [Microsoft.Extensions.DependencyInjection.FromKeyedServices("PlayerB")] VideoPlayerViewModel playerB,
            BlendRenderer? blendRenderer,
            FrameSyncManager? frameSyncManager,
            ILogger<MainWindowViewModel> logger)
        {
            _appStatesService = appStatesService;
            _blendRenderer = blendRenderer;
            _frameSyncManager = frameSyncManager;
            _logger = logger;
            PlayerA = playerA;
            PlayerB = playerB;

            foreach (var player in new[] { PlayerA, PlayerB })
            {
                player.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(VideoPlayerViewModel.IsPaused))
                        SyncPlaybackState();
                };
            }
        }

        private void SyncPlaybackState()
        {
            if (GlobalPlaybackState == GlobalPlaybackState.Stop)
                return;

            // Consider paused only when the primary player is paused
            GlobalPlaybackState = PrimaryPlayer.IsPaused
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
                PlayerA.Reopen();
                PlayerB.Reopen();
                GlobalPlaybackState = GlobalPlaybackState.Play;
            }
            else
            {
                PlayerA.TogglePause();
                PlayerB.TogglePause();
            }
        }

        [RelayCommand]
        private void Stop()
        {
            GlobalPlaybackState = GlobalPlaybackState.Stop;
            PlayerA.Stop();
            PlayerB.Stop();
        }

        partial void OnLoopChanged(bool value)
        {
            PlayerA.SetLoop(value);
            PlayerB.SetLoop(value);
        }

        partial void OnMasterVolumeChanged(double value)
        {
            PlayerA.UpdateActualVolume(value, IsMasterVolumeMuted);
            PlayerB.UpdateActualVolume(value, IsMasterVolumeMuted);
        }

        partial void OnIsMasterVolumeMutedChanged(bool value)
        {
            PlayerA.UpdateActualVolume(MasterVolume, value);
            PlayerB.UpdateActualVolume(MasterVolume, value);
        }

        partial void OnBlendRatioChanged(double value) => PushBlendParams();
        partial void OnBlendBorderWidthChanged(double value) => PushBlendParams();
        partial void OnBlendBorderColorChanged(ColorRgba value) => PushBlendParams();

        partial void OnBlendModeChanged(int value)
        {
            _frameSyncManager?.UpdateBlendMode((BlendMode)value);
        }

        private void PushBlendParams()
        {
            if (_frameSyncManager is null || _blendRenderer?.OutputTexture is null) return;

            var p = new BlendParams
            {
                WidthPx = _blendRenderer.OutputTexture.Width,
                HeightPx = _blendRenderer.OutputTexture.Height,
                Ratio = (float)BlendRatio,
                BorderWidth = (float)BlendBorderWidth,
                BorderColor = new System.Numerics.Vector4(
                    BlendBorderColor.R / 255f,
                    BlendBorderColor.G / 255f,
                    BlendBorderColor.B / 255f,
                    BlendBorderColor.A / 255f),
            };
            _frameSyncManager.UpdateBlendParams(p);
        }

        [RelayCommand]
        private void VolumeIconMouseDown()
        {
            IsMasterVolumeMuted = !IsMasterVolumeMuted;
        }
    }
}
