using System;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;
using Narabemi.Settings;

namespace Narabemi.ViewModels
{
    public partial class VideoPlayerViewModel : ViewModelBase, IAppStatePlayerTarget
    {
        private readonly MpvPlayer _mpvPlayer;
        private readonly ILogger<VideoPlayerViewModel> _logger;
        private bool _mpvInitialized;
        private DispatcherTimer? _pollTimer;

        [ObservableProperty]
        private string _videoPath = string.Empty;

        [ObservableProperty]
        private double _position;

        [ObservableProperty]
        private double _duration;

        [ObservableProperty]
        private bool _isPaused = true;

        [ObservableProperty]
        private double _localVolume = 1.0;

        [ObservableProperty]
        private bool _isLocalVolumeMuted;

        [ObservableProperty]
        private bool _isControlPanelVisible = true;

        [ObservableProperty]
        private string _displayInfo = string.Empty;

        private bool _isSeeking;

        public MpvPlayer MpvPlayer => _mpvPlayer;

        public VideoPlayerViewModel(MpvPlayer mpvPlayer, ILogger<VideoPlayerViewModel> logger)
        {
            _mpvPlayer = mpvPlayer;
            _logger = logger;

            _mpvPlayer.PositionChanged += pos =>
                Dispatcher.UIThread.Post(() => { if (!_isSeeking) Position = pos; });
            _mpvPlayer.DurationChanged += dur =>
                Dispatcher.UIThread.Post(() => Duration = dur);
            _mpvPlayer.PauseChanged += paused =>
                Dispatcher.UIThread.Post(() => IsPaused = paused);
            _mpvPlayer.FileLoaded += () =>
                Dispatcher.UIThread.Post(OnFileLoaded);
        }

        private bool _pendingLoop;

        public void InitMpv(IntPtr windowHandle)
        {
            if (_mpvInitialized) return;

            _mpvPlayer.Init(windowHandle.ToInt64());
            _mpvInitialized = true;

            _mpvPlayer.Loop = _pendingLoop;

            _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnPollTimer);
            _pollTimer.Start();

            if (!string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath))
                _mpvPlayer.LoadFile(VideoPath);
        }

        private void OnPollTimer(object? sender, EventArgs e)
        {
            if (!_mpvInitialized) return;

            var dur = _mpvPlayer.Duration;
            if (dur > 0 && Math.Abs(dur - Duration) > 0.01)
                Duration = dur;

            if (!_isSeeking)
            {
                var pos = _mpvPlayer.Position;
                if (Math.Abs(pos - Position) > 0.01)
                    Position = pos;
            }

            var paused = _mpvPlayer.IsPaused;
            if (paused != IsPaused)
                IsPaused = paused;
        }

        partial void OnVideoPathChanged(string value)
        {
            if (!string.IsNullOrEmpty(value) && File.Exists(value) && _mpvInitialized)
                _mpvPlayer.LoadFile(value);
        }

        public void Play()
        {
            if (_mpvInitialized)
                _mpvPlayer.Play();
        }

        public void Pause()
        {
            if (_mpvInitialized)
                _mpvPlayer.Pause();
        }

        public void Stop()
        {
            if (_mpvInitialized)
                _mpvPlayer.Stop();
        }

        public void Reopen()
        {
            if (_mpvInitialized && !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath))
            {
                _mpvPlayer.LoadFile(VideoPath);
                _mpvPlayer.Play();
            }
        }

        public void SetLoop(bool loop)
        {
            _pendingLoop = loop;
            if (_mpvInitialized)
                _mpvPlayer.Loop = loop;
        }

        public void BeginSeek()
        {
            _isSeeking = true;
        }

        public void SeekTo(double seconds)
        {
            if (_mpvInitialized)
                _mpvPlayer.Seek(seconds);
        }

        public void EndSeek()
        {
            _isSeeking = false;
        }

        [RelayCommand]
        public void TogglePause()
        {
            if (_mpvInitialized)
                _mpvPlayer.TogglePause();
        }

        [RelayCommand]
        private void VolumeIconMouseDown()
        {
            IsLocalVolumeMuted = !IsLocalVolumeMuted;
        }

        public void UpdateActualVolume(double masterVolume, bool masterMuted)
        {
            if (!_mpvInitialized) return;

            var muted = masterMuted || IsLocalVolumeMuted;
            _mpvPlayer.IsMuted = muted;

            var volume = LocalVolume * masterVolume * 100.0;
            _mpvPlayer.Volume = Math.Clamp(volume, 0.0, 130.0);
        }

        partial void OnLocalVolumeChanged(double value)
        {
            UpdateActualVolume(1.0, false);
        }

        partial void OnIsLocalVolumeMutedChanged(bool value)
        {
            UpdateActualVolume(1.0, false);
        }

        private void OnFileLoaded()
        {
            var path = _mpvPlayer.Duration > 0
                ? $"{TimeSpan.FromSeconds(_mpvPlayer.Duration):hh\\:mm\\:ss}"
                : string.Empty;
            DisplayInfo = $"{Path.GetFileName(VideoPath)} [{path}]";
            _logger.LogInformation("File loaded: {VideoPath}", VideoPath);
        }

        [RelayCommand]
        private void OpenFile()
        {
            // File dialog will be handled by the View (code-behind) since it needs window access
        }
    }
}
