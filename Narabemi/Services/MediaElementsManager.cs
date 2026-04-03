using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Narabemi.Messages;
using Narabemi.UI.Controls;
using Narabemi.UI.Windows;
using Unosquare.FFME.Common;

namespace Narabemi.Services
{
    public class MediaElementsManager
    {
        public int MainPlayerId { get; set; }
        public bool AutoSync { get; set; }
        public bool Loop { get; set; }
        public MainWindowViewModel? MainWindowViewModel { get; set; } = null;

        private readonly ConcurrentDictionary<int, Unosquare.FFME.MediaElement> _mediaElements = new();
        private readonly ConcurrentDictionary<int, VideoPlayerViewModel> _playerViewModels = new();
        private readonly ConcurrentDictionary<int, EventHandler<MediaStateChangedEventArgs>> _mediaStateChangedHandlers = new();
        private readonly ConcurrentDictionary<int, EventHandler<MediaOpeningEventArgs>> _mediaOpeningHandlers = new();
        private readonly ConcurrentDictionary<int, EventHandler<MediaOpeningEventArgs>> _mediaChangingHandlers = new();
        private readonly ConcurrentDictionary<int, EventHandler> _mediaEndedHandlers = new();
        private readonly object _loopLock = new();
        private readonly ILogger _logger;

        public MediaElementsManager(ILogger<MediaElementsManager> logger)
        {
            _logger = logger;

            WeakReferenceMessenger.Default.Register<MediaElementsManager, SimpleMessage>(this, static async (r, m) =>
            {
                try
                {
                    var playerId = m.Value.PlayerId;
                    var targetMe = (playerId.HasValue &&
                        r._mediaElements.TryGetValue(playerId.Value, out var me)) ? me : null;

                    r._logger?.LogDebug("{Name} received: type={MessageType}, playerId={PlayerId}", nameof(SimpleMessage), m.Value.MessageType, m.Value.PlayerId);

                    switch (m.Value.MessageType)
                    {
                        case SimpleMessageType.Play:
                            if (targetMe != null)
                                await targetMe.Play();
                            break;
                        case SimpleMessageType.Reopen:
                            if (targetMe != null)
                            {
                                var source = targetMe.Source;
                                var position = targetMe.Position;
                                if (await targetMe.Close() && await targetMe.Open(source))
                                {
                                    if (r.MainWindowViewModel?.GlobalPlaybackState == GlobalPlaybackState.Play)
                                    {
                                        await targetMe.Play();
                                    }
                                    targetMe.Position = position;
                                }
                            }
                            break;
                        case SimpleMessageType.FrameForward:
                            if (targetMe != null)
                            {
                                await targetMe.StepForward();
                                if (r.AutoSync)
                                    await r.SimpleSync();
                            }
                            break;
                        case SimpleMessageType.FrameBackward:
                            if (targetMe != null)
                            {
                                await targetMe.StepBackward();
                                if (r.AutoSync)
                                    await r.SimpleSync();
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    r._logger?.LogError(ex, "{Name} handler threw an unhandled exception: type={MessageType}, playerId={PlayerId}", nameof(SimpleMessage), m.Value.MessageType, m.Value.PlayerId);
                }
            });

            WeakReferenceMessenger.Default.Register<MediaElementsManager, OpenVideoFileMessage>(this, static async (r, m) =>
            {
                try
                {
                    r._logger?.LogDebug("{Name} received: playerId={PlayerId}", nameof(OpenVideoFileMessage), m.Value.PlayerId);
                    if (r._mediaElements.TryGetValue(m.Value.PlayerId, out var me))
                    {
                        await me.Close();
                        await me.Open(m.Value.Uri);
                    }
                }
                catch (Exception ex)
                {
                    r._logger?.LogError(ex, "{Name} handler threw an unhandled exception: playerId={PlayerId}", nameof(OpenVideoFileMessage), m.Value.PlayerId);
                }
            });

            WeakReferenceMessenger.Default.Register<MediaElementsManager, OpenSubtitleFileMessage>(this, static async (r, m) =>
            {
                try
                {
                    r._logger?.LogDebug("{Name} received: playerId={PlayerId}", nameof(OpenSubtitleFileMessage), m.Value.PlayerId);
                    if (r._mediaElements.TryGetValue(m.Value.PlayerId, out var me))
                        await me.ChangeMedia();
                }
                catch (Exception ex)
                {
                    r._logger?.LogError(ex, "{Name} handler threw an unhandled exception: playerId={PlayerId}", nameof(OpenSubtitleFileMessage), m.Value.PlayerId);
                }
            });
        }

        public void Register(int playerId, Unosquare.FFME.MediaElement mediaElement, VideoPlayerViewModel playerViewModel)
        {
            _mediaElements[playerId] = mediaElement;
            _playerViewModels[playerId] = playerViewModel;

            EventHandler<MediaStateChangedEventArgs> mediaStateChangedHandler = (s, e) => CorrectGlobalPlaybackState();
            EventHandler<MediaOpeningEventArgs> mediaOpeningHandler = (s, e) => e.Options.SubtitlesSource = playerViewModel.SubtitlePath;
            EventHandler<MediaOpeningEventArgs> mediaChangingHandler = (s, e) => e.Options.SubtitlesSource = playerViewModel.SubtitlePath;

            EventHandler mediaEndedHandler = async (s, e) =>
            {
                if (!Loop)
                    return;

                if (!Monitor.TryEnter(_loopLock))
                    return;

                try
                {
                    _logger.LogDebug("Loop: restarting playback (triggered by player {PlayerId})", playerId);
                    await SeekAllAsync(TimeSpan.Zero);
                    await PlayAllAsync();
                }
                finally
                {
                    Monitor.Exit(_loopLock);
                }
            };

            _mediaStateChangedHandlers[playerId] = mediaStateChangedHandler;
            _mediaOpeningHandlers[playerId] = mediaOpeningHandler;
            _mediaChangingHandlers[playerId] = mediaChangingHandler;
            _mediaEndedHandlers[playerId] = mediaEndedHandler;

            mediaElement.MediaStateChanged += mediaStateChangedHandler;
            mediaElement.MediaOpening += mediaOpeningHandler;
            mediaElement.MediaChanging += mediaChangingHandler;
            mediaElement.MediaEnded += mediaEndedHandler;
        }

        public void Unregister(int playerId)
        {
            if (_mediaElements.TryGetValue(playerId, out var mediaElement))
            {
                if (_mediaStateChangedHandlers.TryGetValue(playerId, out var mediaStateChangedHandler))
                    mediaElement.MediaStateChanged -= mediaStateChangedHandler;

                if (_mediaOpeningHandlers.TryGetValue(playerId, out var mediaOpeningHandler))
                    mediaElement.MediaOpening -= mediaOpeningHandler;

                if (_mediaChangingHandlers.TryGetValue(playerId, out var mediaChangingHandler))
                    mediaElement.MediaChanging -= mediaChangingHandler;

                if (_mediaEndedHandlers.TryGetValue(playerId, out var mediaEndedHandler))
                    mediaElement.MediaEnded -= mediaEndedHandler;
            }

            _mediaElements.TryRemove(playerId, out _);
            _playerViewModels.TryRemove(playerId, out _);
            _mediaStateChangedHandlers.TryRemove(playerId, out _);
            _mediaOpeningHandlers.TryRemove(playerId, out _);
            _mediaChangingHandlers.TryRemove(playerId, out _);
            _mediaEndedHandlers.TryRemove(playerId, out _);
        }

        public async ValueTask PlayAllAsync() =>
            await Parallel.ForEachAsync(_mediaElements.Values, async (me, _ct) => await me.Play());

        public async ValueTask PauseAllAsync() =>
            await Parallel.ForEachAsync(_mediaElements.Values, async (me, _ct) => await me.Pause());

        public async ValueTask StopAllAsync() =>
            await Parallel.ForEachAsync(_mediaElements.Values, async (me, _ct) => await me.Stop());

        public async ValueTask SeekAllAsync(TimeSpan position) =>
            await Parallel.ForEachAsync(_mediaElements.Values, async (me, _ct) => await me.Seek(position));

        public void ResetAllSpeed() { foreach (var me in _mediaElements.Values) me.SpeedRatio = 1.0; }

        public async ValueTask SimpleSync()
        {
            if (_mediaElements.TryGetValue(MainPlayerId, out var mainMe))
            {
                var targetPos = mainMe.Position - _playerViewModels[MainPlayerId].Offset;
                foreach (var subPlayerId in _mediaElements.Keys)
                {
                    if (subPlayerId == MainPlayerId)
                        continue;

                    var pos = targetPos + _playerViewModels[subPlayerId].Offset;
                    await _mediaElements[subPlayerId].Seek(pos);
                }
            }
        }

        public async ValueTask SpeedAdjustSync()
        {
            if (_mediaElements.TryGetValue(MainPlayerId, out var mainMe))
            {
                foreach (var subPlayerId in _mediaElements.Keys)
                {
                    if (subPlayerId == MainPlayerId)
                        continue;

                    var subMe = _mediaElements[subPlayerId];

                    var mainPos = mainMe.ActualPosition.GetValueOrDefault();
                    var subPos = subMe.ActualPosition.GetValueOrDefault();
                    var subDur = subMe.NaturalDuration.GetValueOrDefault();

                    if (mainPos > subDur && subDur > TimeSpan.Zero)
                        mainPos = TimeSpan.FromMilliseconds(mainPos.TotalMilliseconds % subDur.TotalMilliseconds);

                    var diff = (mainPos - _playerViewModels[MainPlayerId].Offset) - (subPos - _playerViewModels[subPlayerId].Offset);
                    var diffMsAbs = Math.Abs(diff.TotalMilliseconds);
                    if (diffMsAbs <= 1.0)
                    {
                        mainMe.SpeedRatio = 1.0;
                        subMe.SpeedRatio = 1.0;
                    }
                    else
                    {
                        _logger.LogTrace("{Name}: diff={Diff}", nameof(SpeedAdjustSync), diff);

                        if (diffMsAbs > 1500.0)
                        {
                            var pos = mainPos - _playerViewModels[MainPlayerId].Offset + _playerViewModels[subPlayerId].Offset;
                            if (pos >= TimeSpan.Zero &&
                                pos <= subDur)
                                await subMe.Seek(pos);
                        }
                        else
                        {
                            var diffMs = diff.TotalMilliseconds;

                            double speed;
                            if (diffMs > 500.0) speed = 1.5;
                            else if (diffMs > 50.0) speed = 1.1;
                            else if (diffMs > 5.0) speed = 1.01;
                            else if (diffMs > 1.0) speed = 1.001;
                            else if (diffMs > -1.0) speed = 1.0;
                            else if (diffMs > -5.0) speed = 0.999;
                            else if (diffMs > -50.0) speed = 0.99;
                            else if (diffMs > -500.0) speed = 0.9;
                            else speed = 0.75;

                            subMe.SpeedRatio = speed;
                            mainMe.SpeedRatio = 1.0;
                        }
                    }
                }
            }
        }

        private void CorrectGlobalPlaybackState()
        {
            if (MainWindowViewModel == null)
                return;

            var mediaStates = _mediaElements.Values.Select(v => v.MediaState);

            var vm = MainWindowViewModel;
            if (vm.GlobalPlaybackState != GlobalPlaybackState.Play && mediaStates.All(v => v == MediaPlaybackState.Play))
                vm.GlobalPlaybackState = GlobalPlaybackState.Play;
            else if (vm.GlobalPlaybackState != GlobalPlaybackState.Pause && mediaStates.All(v => v == MediaPlaybackState.Pause))
                vm.GlobalPlaybackState = GlobalPlaybackState.Pause;
            else if (vm.GlobalPlaybackState != GlobalPlaybackState.Stop && mediaStates.All(v => v == MediaPlaybackState.Stop))
                vm.GlobalPlaybackState = GlobalPlaybackState.Stop;
        }
    }
}
