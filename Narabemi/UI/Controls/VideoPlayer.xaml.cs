using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Narabemi.Messages;
using Narabemi.Models;
using Narabemi.UI.Windows;
using Unosquare.FFME.Common;

namespace Narabemi.UI.Controls
{
    public partial class VideoPlayer : UserControl
    {
        public Unosquare.FFME.MediaElement MediaElement { get => VideoMediaElement; }
        public Grid Grid { get => VideoGrid; }

        private VideoPlayerViewModel? _viewModel;
#pragma warning disable IDE0052 // Remove unread private member
        private ILogger? _logger;
#pragma warning restore IDE0052 // Remove unread private member

        public VideoPlayer()
        {
            InitializeComponent();
        }

        public void LateInit(
            VideoPlayerViewModel viewModel,
            ILogger logger)
        {
            _viewModel = viewModel;
            _logger = logger;

            DataContext = _viewModel;
        }
    }

    [INotifyPropertyChanged]
    public partial class VideoPlayerViewModel
    {
        public int PlayerId { get; }

        [ObservableProperty]
        private string videoPath = string.Empty;
        [ObservableProperty]
        private Uri? mediaSourceUri = null;
        [ObservableProperty]
        private string? subtitlePath = null;
        [ObservableProperty]
        private List<string> subtitlePaths = new();
        [ObservableProperty]
        private TimeSpan offset = TimeSpan.Zero;
        [ObservableProperty]
        private Thickness fileDropBorderWidth = new(0.0);

        [ObservableProperty]
        private double localVolume = 1.0;
        [ObservableProperty]
        private double actualVolume = 1.0;
        [ObservableProperty]
        private bool isLocalVolumeMuted = false;
        [ObservableProperty]
        private bool isActualVolumeMuted = false;

        [ObservableProperty]
        private ScreenSize displaySize;
        [ObservableProperty]
        private AspectRatio displayAspectRatio;

        [ObservableProperty]
        private bool isControlPanelVisible = true;

        private readonly ILogger _logger;
        private readonly MainWindowViewModel _mainWindowViewModel;

        public VideoPlayerViewModel(ILogger<MainWindow> logger, MainWindowViewModel mainWindowViewModel, int playerId)
        {
            _logger = logger;
            _mainWindowViewModel = mainWindowViewModel;
            PlayerId = playerId;
        }

        partial void OnVideoPathChanged(string value)
        {
            _logger?.LogDebug("{Name}: '{Value}'", nameof(OnVideoPathChanged), value);
            var sourceUri = File.Exists(value) ? new Uri(value, UriKind.Absolute) : null;

            // Scan local subtitle files
            if (sourceUri != null)
            {
                var videoPath = sourceUri?.LocalPath;
                if (File.Exists(videoPath) && Path.GetDirectoryName(videoPath) is string videoDir)
                {
                    var name = Path.GetFileNameWithoutExtension(videoPath);
                    SubtitlePaths.Clear();
                    foreach (var path in Directory.GetFiles(videoDir))
                    {
                        if (Path.GetExtension(path).ToLower() == ".srt" &&
                            Path.GetFileNameWithoutExtension(path).StartsWith(name))
                            SubtitlePaths.Add(path);
                    }

                    SubtitlePath = SubtitlePaths.FirstOrDefault();
                }
            }

            MediaSourceUri = sourceUri;
        }

        partial void OnMediaSourceUriChanged(Uri? value)
        {
            _logger?.LogDebug("{Name}: '{Value}'", nameof(OnMediaSourceUriChanged), value);
            if (value != null)
                WeakReferenceMessenger.Default.Send(new OpenVideoFileMessage(PlayerId, value));
        }

        partial void OnSubtitlePathChanged(string? value)
        {
            _logger?.LogDebug("{Name}: '{Value}'", nameof(OnSubtitlePathChanged), value);
            if (value != null)
                WeakReferenceMessenger.Default.Send(new OpenSubtitleFileMessage(PlayerId, value));
        }

        partial void OnLocalVolumeChanged(double value) =>
            ActualVolume = _mainWindowViewModel.MasterVolume * value;

        partial void OnIsLocalVolumeMutedChanged(bool value) =>
            IsActualVolumeMuted = _mainWindowViewModel.IsMasterVolumeMuted || value;

        [RelayCommand]
        private void Open()
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog().HasValue && File.Exists(dialog.FileName))
                VideoPath = dialog.FileName;
        }

        [RelayCommand]
        private void Reopen()
        {
            if (File.Exists(VideoPath))
                WeakReferenceMessenger.Default.Send(new SimpleMessage(SimpleMessageType.Reopen, PlayerId));
        }

        [RelayCommand]
        private void ChangeSubtitle()
        {
            if (SubtitlePaths == null || SubtitlePaths.Count == 0)
                return;

            int currentIdx = 0;
            if (!string.IsNullOrEmpty(SubtitlePath))
                currentIdx = SubtitlePaths.IndexOf(SubtitlePath);

            int nextIdx = currentIdx + 1;
            if (nextIdx >= subtitlePaths.Count)
                nextIdx = 0;

            SubtitlePath = subtitlePaths[nextIdx];
        }

        [RelayCommand]
        private void MediaOpened(MediaOpenedEventArgs e)
        {
            if (e.Info.BestStreams.TryGetValue(AVMediaType.AVMEDIA_TYPE_VIDEO, out var streamInfo))
            {
                // var displayAspectRatio = streamInfo.DisplayAspectRatio; // can't get correct DAR
                var frameAspectRatio = Utils.GetAspectRatio(streamInfo.PixelWidth, streamInfo.PixelHeight);
                var pixelAspectRatio = new AspectRatio(streamInfo.SampleAspectRatio.num, streamInfo.SampleAspectRatio.den);

                DisplayAspectRatio = new(frameAspectRatio.Numerator * pixelAspectRatio.Numerator,
                                         frameAspectRatio.Denominator * pixelAspectRatio.Denominator);
                DisplaySize = GetDisplaySize(streamInfo.PixelHeight, DisplayAspectRatio);
                _mainWindowViewModel.DisplayAspectRatio = DisplayAspectRatio;

                _logger?.LogDebug("AspectRatio: FAR={FrameAspectRatio}, PAR={PixelAspectRatio}, DAR={DisplayAspectRatio}, {DisplaySize}",
                    frameAspectRatio, pixelAspectRatio, DisplayAspectRatio, DisplaySize);
            }

            if (_mainWindowViewModel.GlobalPlaybackState == GlobalPlaybackState.Play)
                WeakReferenceMessenger.Default.Send(new SimpleMessage(SimpleMessageType.Play, PlayerId));
        }

        [RelayCommand]
        private void MediaEnded(EventArgs e)
        {
            _logger.LogDebug("MediaEnded id={PlayerId}", PlayerId);
        }

        [RelayCommand]
        private void DisplayAspectRatioApply(object value)
        {
            Guard.IsOfType<AspectRatio>(value);

            var aspectRatio = (AspectRatio)value;
            _mainWindowViewModel.DisplayAspectRatio = aspectRatio;
        }

        [RelayCommand]
        private void OpenFileLocation(string filePath)
        {
            var dirPath = Path.GetDirectoryName(filePath);
            if (Directory.Exists(dirPath))
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }

        [RelayCommand]
        private void VolumeIconMouseDown(MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                IsLocalVolumeMuted = !IsLocalVolumeMuted;
                e.Handled = true;
            }
        }

        [RelayCommand]
        private void DragEnter(DragEventArgs e)
        {
            var pathList = GetDroppedFile(_logger, e);
            if (pathList.Count > 0)
            {
                e.Effects = DragDropEffects.Move;

                if (pathList.Count == 1)
                {
                    ShowDragBorder();
                }
                else
                {
                    var playerViewModels = _mainWindowViewModel.PlayerViewModels;
                    var n = Math.Min(pathList.Count, playerViewModels.Count);
                    for (int i = 0; i < n; i++)
                        playerViewModels[i].ShowDragBorder();
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
                HideDragBorder();
            }
        }

        [RelayCommand]
        private void DragLeave() =>
            _mainWindowViewModel.PlayerViewModels.ForEach(v => v.HideDragBorder());

        [RelayCommand]
        private void Drop(DragEventArgs e)
        {
            var pathList = GetDroppedFile(_logger, e);
            if (pathList.Count == 1)
            {
                VideoPath = pathList[0];
                HideDragBorder();
            }
            else if (pathList.Count > 1)
            {
                var playerViewModels = _mainWindowViewModel.PlayerViewModels;
                var n = Math.Min(pathList.Count, playerViewModels.Count);
                for (int i = 0; i < n; i++)
                {
                    playerViewModels[i].VideoPath = pathList[i];
                    playerViewModels[i].HideDragBorder();
                }
            }

        }

        [RelayCommand]
        private void FrameForward() =>
            WeakReferenceMessenger.Default.Send(new SimpleMessage(SimpleMessageType.FrameForward, PlayerId));
        [RelayCommand]
        private void FrameBackward() =>
            WeakReferenceMessenger.Default.Send(new SimpleMessage(SimpleMessageType.FrameBackward, PlayerId));

        private void ShowDragBorder() => FileDropBorderWidth = new(1.0);
        private void HideDragBorder() => FileDropBorderWidth = new(0.0);

        private static List<string> GetDroppedFile(ILogger logger, DragEventArgs e)
        {
            List<string> filePathList = new();

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var data = e.Data.GetData(DataFormats.FileDrop);
                logger.LogDebug("Drop ({Name}): {Data}", DataFormats.FileDrop, data);
                if (data is string[] sarr && sarr.Length > 0)
                    filePathList.AddRange(sarr);
            }
            else if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                var data = e.Data.GetData(DataFormats.StringFormat);
                logger.LogDebug("Drop ({Name}): {Data}", DataFormats.StringFormat, data);
                if (data is string s)
                    filePathList.Add(s);
            }

            var validAll = filePathList.All(path => !string.IsNullOrEmpty(path) && File.Exists(path));
            if (!validAll)
                filePathList.Clear();

            return filePathList;
        }

        private static ScreenSize GetDisplaySize(int pixelHeight, AspectRatio displayAspectRatio)
        {
            int displayWidth = (int)((pixelHeight / displayAspectRatio.Denominator) * displayAspectRatio.Numerator);
            return new(displayWidth, pixelHeight);
        }
    }
}
