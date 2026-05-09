using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;
using Narabemi.ViewModels;

namespace Narabemi.Testing
{
    /// <summary>
    /// Snapshot capture for the dual-HWND native pipeline.
    /// State machine: WaitFirstLoad → Seek → WaitSettle → CapturePerPlayer → Done.
    /// Uses mpv's screenshot-to-file command per player (bit-exact, GPU-decoded frame
    /// without compositor or display-pipeline interference).
    ///
    /// Output:
    ///   single video:  &lt;OutputPath&gt;
    ///   dual video:    &lt;OutputPath:base&gt;_a.&lt;ext&gt; and &lt;OutputPath:base&gt;_b.&lt;ext&gt;
    /// </summary>
    public sealed class SnapshotRunner
    {
        private enum State { WaitFirstLoad, Seeking, WaitSettle, Capturing, Done }

        private readonly SnapshotArgs _args;
        private readonly MainWindowViewModel _vm;
        private readonly ILogger<SnapshotRunner> _logger;

        private State _state = State.WaitFirstLoad;
        private int _filesLoaded;
        private DispatcherTimer? _timeout;

        private const int PostSeekDelayMs = 800;
        private const int TimeoutSeconds  = 30;

        public SnapshotRunner(
            SnapshotArgs args,
            MainWindowViewModel vm,
            ILogger<SnapshotRunner> logger)
        {
            _args = args;
            _vm = vm;
            _logger = logger;
        }

        public void Start(Window _window)
        {
            // Listen for file-loaded events on both players to know when we can seek.
            _vm.PlayerA.MpvPlayer.FileLoaded += OnFileLoaded;
            if (HasPlayerB)
                _vm.PlayerB.MpvPlayer.FileLoaded += OnFileLoaded;

            _timeout = new DispatcherTimer(
                TimeSpan.FromSeconds(TimeoutSeconds),
                DispatcherPriority.Normal,
                OnTimeout);
            _timeout.Start();

            _logger.LogInformation(
                "Snapshot runner started (seek={Seek}s, output={Output}, timeout={Timeout}s)",
                _args.SeekSeconds, _args.OutputPath, TimeoutSeconds);
        }

        private bool HasPlayerB => !string.IsNullOrEmpty(_vm.PlayerB.VideoPath);
        private int  ExpectedLoads => HasPlayerB ? 2 : 1;

        private void OnFileLoaded()
        {
            // Marshal to UI thread so the state machine and timer interactions stay single-threaded.
            Dispatcher.UIThread.Post(() =>
            {
                if (_state != State.WaitFirstLoad) return;
                if (System.Threading.Interlocked.Increment(ref _filesLoaded) < ExpectedLoads) return;

                _state = State.Seeking;
                DoSeek();
            });
        }

        private void DoSeek()
        {
            _vm.PlayerA.SeekTo(_args.SeekSeconds);
            if (HasPlayerB)
                _vm.PlayerB.SeekTo(_args.SeekSeconds);
            _logger.LogInformation("Seeked to {Seek}s, settling…", _args.SeekSeconds);

            _state = State.WaitSettle;
            DispatcherTimer.RunOnce(BeginCapture, TimeSpan.FromMilliseconds(PostSeekDelayMs));
        }

        private void BeginCapture()
        {
            if (_state != State.WaitSettle) return;
            _state = State.Capturing;
            try
            {
                CaptureAll();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot capture failed");
                Shutdown(1);
            }
        }

        private void CaptureAll()
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_args.OutputPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (HasPlayerB)
            {
                var dirOut  = Path.GetDirectoryName(Path.GetFullPath(_args.OutputPath)) ?? string.Empty;
                var baseName = Path.GetFileNameWithoutExtension(_args.OutputPath);
                var ext      = Path.GetExtension(_args.OutputPath);
                var pathA    = Path.Combine(dirOut, $"{baseName}_a{ext}");
                var pathB    = Path.Combine(dirOut, $"{baseName}_b{ext}");
                Capture(_vm.PlayerA.MpvPlayer, pathA);
                Capture(_vm.PlayerB.MpvPlayer, pathB);
            }
            else
            {
                Capture(_vm.PlayerA.MpvPlayer, Path.GetFullPath(_args.OutputPath));
            }
        }

        private void Capture(MpvPlayer player, string fullPath)
        {
            // mpv writes the screenshot synchronously when invoked from a video-loaded
            // state. "video" flag captures the raw video frame (no OSD), bit-exact.
            var rc = player.SnapshotToFile(fullPath);
            if (rc < 0)
            {
                _logger.LogError("screenshot-to-file failed (rc={Rc}) for {Path}", rc, fullPath);
                throw new InvalidOperationException($"screenshot-to-file failed: {fullPath}");
            }
            _logger.LogInformation("Snapshot saved: {Path}", fullPath);
        }

        private void OnTimeout(object? sender, EventArgs e)
        {
            _timeout?.Stop();
            if (_state == State.Done) return;

            _logger.LogError("Snapshot timed out after {Timeout}s (state={State}, loads={Loads}/{Expected})",
                TimeoutSeconds, _state, _filesLoaded, ExpectedLoads);
            Shutdown(2);
        }

        private void Shutdown(int code)
        {
            _state = State.Done;
            _timeout?.Stop();
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                d.Shutdown(code);
        }
    }
}
