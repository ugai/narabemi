using System;
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

        public string WindowTitle
        {
            get
            {
                var a = System.IO.Path.GetFileName(PlayerA.VideoPath);
                var b = System.IO.Path.GetFileName(PlayerB.VideoPath);
                return (a, b) switch
                {
                    ({ Length: > 0 }, { Length: > 0 }) => $"Narabemi — {a} | {b}",
                    ({ Length: > 0 }, _) => $"Narabemi — {a}",
                    (_, { Length: > 0 }) => $"Narabemi — {b}",
                    _ => "Narabemi",
                };
            }
        }

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
            ILogger<MainWindowViewModel> logger)
        {
            _appStatesService = appStatesService;
            _logger = logger;
            PlayerA = playerA;
            PlayerB = playerB;

            foreach (var player in new[] { PlayerA, PlayerB })
            {
                player.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(VideoPlayerViewModel.IsPaused))
                        SyncPlaybackState();
                    if (e.PropertyName == nameof(VideoPlayerViewModel.VideoPath))
                        OnPropertyChanged(nameof(WindowTitle));
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
            if (IsSnapshotMode) return;
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

        /// <summary>
        /// When true, suppresses appstates.json write on window close.
        /// Set by App in snapshot/bench mode to avoid overwriting user state.
        /// </summary>
        public bool IsSnapshotMode { get; set; }

        /// <summary>
        /// When true, benchmark mode is active. Tracked independently of IsSnapshotMode
        /// so the two test runners can be distinguished if they ever need to behave differently.
        /// </summary>
        public bool IsBenchMode { get; set; }

        public string BlendModeLabel => BlendMode == 0 ? "Horizontal" : "Vertical";

        partial void OnBlendModeChanged(int value) => OnPropertyChanged(nameof(BlendModeLabel));

        /// <summary>
        /// Seeks the primary player to <paramref name="seconds"/>.
        /// When AutoSync is on, also seeks the secondary player to the same position.
        /// </summary>
        public void SeekBoth(double seconds)
        {
            PrimaryPlayer.SeekTo(seconds);
            if (AutoSync)
            {
                var secondary = MainPlayerIndex == 0 ? PlayerB : PlayerA;
                secondary.SeekTo(seconds);
            }
        }

        /// <summary>
        /// Seeks both players by a relative offset. Used by keyboard shortcuts.
        /// </summary>
        public void SeekRelative(double deltaSeconds)
        {
            var pos = PrimaryPlayer.Position + deltaSeconds;
            pos = Math.Clamp(pos, 0, PrimaryPlayer.Duration > 0 ? PrimaryPlayer.Duration : pos);
            SeekBoth(pos);
        }

        [RelayCommand]
        private void VolumeIconMouseDown()
        {
            IsMasterVolumeMuted = !IsMasterVolumeMuted;
        }
    }
}
