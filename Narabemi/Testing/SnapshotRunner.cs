using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Gpu;
using Narabemi.ViewModels;

namespace Narabemi.Testing
{
    /// <summary>
    /// Orchestrates snapshot capture in --snapshot mode.
    /// State machine: WaitFirstFrame → Seek → WaitPostSeekFrames → Capture → Done.
    /// </summary>
    public sealed class SnapshotRunner
    {
        private enum State { WaitFirstFrame, Seeking, WaitPostSeekFrames, Capturing, Done }

        private readonly SnapshotArgs _args;
        private readonly MainWindowViewModel _vm;
        private readonly FrameSyncManager _syncManager;
        private readonly ILogger<SnapshotRunner> _logger;

        private Window? _window;
        private State _state = State.WaitFirstFrame;
        private int _frameCount;
        private DispatcherTimer? _timeout;

        private const int FramesBeforeSeek = 2;
        private const int FramesAfterSeek = 3;
        private const int PostSeekDelayMs = 500;
        private const int TimeoutSeconds = 30;

        public SnapshotRunner(
            SnapshotArgs args,
            MainWindowViewModel vm,
            FrameSyncManager syncManager,
            ILogger<SnapshotRunner> logger)
        {
            _args = args;
            _vm = vm;
            _syncManager = syncManager;
            _logger = logger;
        }

        public void Start(Window window)
        {
            _window = window;
            _syncManager.BlendFrameReady += OnFrameReady;

            _timeout = new DispatcherTimer(
                TimeSpan.FromSeconds(TimeoutSeconds),
                DispatcherPriority.Normal,
                OnTimeout);
            _timeout.Start();

            _logger.LogInformation(
                "Snapshot runner started (seek={Seek}s, output={Output}, timeout={Timeout}s)",
                _args.SeekSeconds, _args.OutputPath, TimeoutSeconds);
        }

        private void OnFrameReady()
        {
            var count = Interlocked.Increment(ref _frameCount);

            switch (_state)
            {
                case State.WaitFirstFrame when count >= FramesBeforeSeek:
                    _state = State.Seeking;
                    Interlocked.Exchange(ref _frameCount, 0);
                    Dispatcher.UIThread.Post(DoSeek);
                    break;

                case State.WaitPostSeekFrames when count >= FramesAfterSeek:
                    _state = State.Capturing;
                    Dispatcher.UIThread.Post(ScheduleCapture);
                    break;
            }
        }

        private void DoSeek()
        {
            _vm.PlayerA.SeekTo(_args.SeekSeconds);
            _vm.PlayerB.SeekTo(_args.SeekSeconds);
            // Do not pause — mpv stops firing GL update callbacks when paused,
            // which would prevent post-seek frames from arriving.
            _logger.LogInformation("Seeked to {Seek}s, waiting for post-seek frames", _args.SeekSeconds);

            // Short delay before counting post-seek frames to let mpv process the seek
            var seekDelay = new DispatcherTimer(
                TimeSpan.FromMilliseconds(PostSeekDelayMs),
                DispatcherPriority.Normal,
                (_, _) => { });
            seekDelay.Tick += (_, _) =>
            {
                seekDelay.Stop();
                _state = State.WaitPostSeekFrames;
                Interlocked.Exchange(ref _frameCount, 0);
            };
            seekDelay.Start();
        }

        private void ScheduleCapture()
        {
            // Give Avalonia one more render cycle to paint the latest frame
            Dispatcher.UIThread.Post(CaptureAndExit, DispatcherPriority.Render);
        }

        private void CaptureAndExit()
        {
            if (_state == State.Done) return;
            _state = State.Done;
            _syncManager.BlendFrameReady -= OnFrameReady;
            _timeout?.Stop();

            try
            {
                var handle = _window?.TryGetPlatformHandle();
                if (handle is null)
                {
                    _logger.LogError("Failed to get platform window handle");
                    Shutdown(1);
                    return;
                }

                var bitmap = WindowCapture.CaptureWindow(handle.Handle);
                if (bitmap is null)
                {
                    _logger.LogError("PrintWindow capture returned null");
                    Shutdown(1);
                    return;
                }

                var dir = Path.GetDirectoryName(Path.GetFullPath(_args.OutputPath));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                bitmap.Save(_args.OutputPath);
                _logger.LogInformation("Snapshot saved: {Path} ({W}x{H})",
                    Path.GetFullPath(_args.OutputPath), bitmap.PixelSize.Width, bitmap.PixelSize.Height);

                Shutdown(0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot capture failed");
                Shutdown(1);
            }
        }

        private void OnTimeout(object? sender, EventArgs e)
        {
            _timeout?.Stop();
            if (_state == State.Done) return;

            _logger.LogError("Snapshot timed out after {Timeout}s (state={State}, frames={Frames})",
                TimeoutSeconds, _state, _frameCount);
            _state = State.Done;
            _syncManager.BlendFrameReady -= OnFrameReady;
            Shutdown(2);
        }

        private static void Shutdown(int exitCode)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(exitCode);
            }
        }
    }
}
