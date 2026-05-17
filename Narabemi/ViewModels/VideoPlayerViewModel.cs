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
    public partial class VideoPlayerViewModel : ViewModelBase, IAppStatePlayerTarget, IDisposable
    {
        private readonly MpvPlayer _mpvPlayer;
        private readonly ILogger<VideoPlayerViewModel> _logger;
        private bool _mpvInitialized;
        private bool _disposed;
        private IntPtr _boundHwnd;

        /// <summary>Source video pixel dimensions, populated on FileLoaded.</summary>
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        private bool _sourceDimsSet;

        /// <summary>
        /// Fires once both <see cref="SourceWidth"/> and <see cref="SourceHeight"/> have
        /// been populated for the current file. Used by MainWindowViewModel to apply
        /// the wipe crop. A single event avoids the W-then-H ordering race that two
        /// observable properties would have.
        /// </summary>
        public event Action? VideoReady;

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
        private double _speed = 1.0;

        [ObservableProperty]
        private double _timeOffset = 0.0;

        [ObservableProperty]
        private string _displayInfo = string.Empty;

        private bool _isSeeking;

        public string TimeDisplay => $"{FormatTime(Position)} / {FormatTime(Duration)}";

        private static string FormatTime(double totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            var ts = TimeSpan.FromSeconds(totalSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        public MpvPlayer MpvPlayer => _mpvPlayer;

        public VideoPlayerViewModel(MpvPlayer mpvPlayer, ILogger<VideoPlayerViewModel> logger)
        {
            _mpvPlayer = mpvPlayer;
            _logger = logger;

            _mpvPlayer.PositionChanged += OnPositionChangedEvent;
            _mpvPlayer.DurationChanged += OnDurationChangedEvent;
            _mpvPlayer.PauseChanged += OnPauseChangedEvent;
            _mpvPlayer.FileLoaded += OnFileLoadedEvent;
        }

        // Named handlers so they can be unsubscribed in Dispose.
        private void OnPositionChangedEvent(double pos) =>
            Dispatcher.UIThread.Post(() => { if (!_isSeeking) Position = pos; });

        private void OnDurationChangedEvent(double dur) =>
            Dispatcher.UIThread.Post(() => Duration = dur);

        private void OnPauseChangedEvent(bool paused) =>
            Dispatcher.UIThread.Post(() => IsPaused = paused);

        private void OnFileLoadedEvent() =>
            Dispatcher.UIThread.Post(OnFileLoaded);

        private bool _pendingLoop;

        /// <summary>
        /// Initializes mpv with native D3D11 video output and full HW decoding,
        /// rendering directly into the provided child HWND.
        /// <para>
        /// Safe to call on native-control re-creation: if mpv is already bound to the
        /// same HWND the call is a no-op; if a different HWND arrives (e.g. display
        /// reconfiguration, future dock/undock) the wid property is updated in-place so
        /// mpv migrates rendering without a full context restart.
        /// </para>
        /// </summary>
        public void InitMpv(IntPtr windowHandle)
        {
            if (!_mpvInitialized)
            {
                _mpvPlayer.InitNativeD3D11(windowHandle.ToInt64());
                _boundHwnd = windowHandle;
                FinishInit();
                return;
            }

            // Native control was re-created -- detect whether the HWND actually changed.
            if (windowHandle == _boundHwnd)
                return;

            _logger.LogWarning(
                "Native control re-created with a different HWND " +
                "(old=0x{Old:X}, new=0x{New:X}); rebinding mpv wid to the new window.",
                _boundHwnd, windowHandle);
            _mpvPlayer.RebindWindow(windowHandle.ToInt64());
            _boundHwnd = windowHandle;
        }

        private void FinishInit()
        {
            _mpvInitialized = true;
            _mpvPlayer.Loop = _pendingLoop;
            if (System.Math.Abs(Speed - 1.0) > 1e-6)
                _mpvPlayer.Speed = System.Math.Clamp(Speed, 0.1, 100.0);

            if (!string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath))
                _mpvPlayer.LoadFile(VideoPath);
        }

        partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(TimeDisplay));
        partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(TimeDisplay));

        partial void OnSpeedChanged(double value)
        {
            if (_mpvInitialized)
                _mpvPlayer.Speed = System.Math.Clamp(value, 0.1, 100.0);
        }

        partial void OnVideoPathChanged(string value)
        {
            // New file → re-read dims on the next FileLoaded.
            _sourceDimsSet = false;
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

        public void SeekTo(double seconds, bool exact = false)
        {
            if (_mpvInitialized)
                _mpvPlayer.Seek(seconds, absolute: true, exact: exact);
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

            // Read raw source dims via `width`/`height` rather than `dwidth`/`dheight`:
            // dwidth reflects POST-crop display size, so once we apply video-crop the
            // second FileLoaded event would report the cropped size and overwrite our
            // SourceWidth — cascading into wrong crop math next iteration.
            // For non-anamorphic content (SAR=1) width == dwidth, which covers our
            // local mp4/mkv use case. Anamorphic handling is a future tick.
            // The _sourceDimsSet guard covers the same race even if mpv reported width
            // differently across reconfigs.
            if (_sourceDimsSet) return;
            int.TryParse(_mpvPlayer.GetPropertyStr("width"),  out var w);
            int.TryParse(_mpvPlayer.GetPropertyStr("height"), out var h);
            if (w > 0 && h > 0)
            {
                SourceWidth = w;
                SourceHeight = h;
                _sourceDimsSet = true;
                VideoReady?.Invoke();
            }
        }

        /// <summary>Applies an mpv video-crop string to this player. Empty to clear.</summary>
        public void SetCrop(string crop) => _mpvPlayer.SetVideoCrop(crop);

        [RelayCommand]
        private void OpenFile()
        {
            // File dialog will be handled by the View (code-behind) since it needs window access
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Unsubscribe from MpvPlayer events to prevent callbacks into a torn-down VM
            // after shutdown begins.
            _mpvPlayer.PositionChanged -= OnPositionChangedEvent;
            _mpvPlayer.DurationChanged -= OnDurationChangedEvent;
            _mpvPlayer.PauseChanged -= OnPauseChangedEvent;
            _mpvPlayer.FileLoaded -= OnFileLoadedEvent;
        }
    }
}
