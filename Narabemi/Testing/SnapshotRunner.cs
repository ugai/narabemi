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

        // 1500ms (was 800) — gives mpv's filter chain enough time to settle the seek
        // and apply video-crop. Empirically the very first run after launch can be
        // flaky at <1s if the crop set raced with the decoder warmup.
        private const int PostSeekDelayMs = 1500;
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

        private Window? _window;

        public void Start(Window window)
        {
            _window = window;
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
                // Defensive re-apply of crops right before capture. Without this, the
                // very first snapshot after launch occasionally captures a pre-crop
                // frame even with a 1.5s settle delay — likely a race with mpv's first
                // VideoReconfig event.
                _vm.UpdateCrops();

                // Give mpv one more frame interval to render the (re-)cropped output.
                DispatcherTimer.RunOnce(() =>
                {
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
                }, TimeSpan.FromMilliseconds(150));
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

            var dirOut   = Path.GetDirectoryName(Path.GetFullPath(_args.OutputPath)) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(_args.OutputPath);
            var ext      = Path.GetExtension(_args.OutputPath);

            if (HasPlayerB)
            {
                var pathA = Path.Combine(dirOut, $"{baseName}_a{ext}");
                var pathB = Path.Combine(dirOut, $"{baseName}_b{ext}");
                Capture(_vm.PlayerA.MpvPlayer, pathA);
                Capture(_vm.PlayerB.MpvPlayer, pathB);
            }
            else
            {
                Capture(_vm.PlayerA.MpvPlayer, Path.GetFullPath(_args.OutputPath));
            }

            // Always also save the rendered window. This is the actual visual output
            // (per-player screenshots only show mpv's decoded frame, not the layout).
            // The window PNG is what verifies wipe-seam alignment in the displayed UI.
            CaptureWindow(Path.Combine(dirOut, $"{baseName}_window{ext}"));
        }

        private void CaptureWindow(string path)
        {
            if (_window is null) return;
            var hwnd = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) { _logger.LogWarning("Window HWND not available for capture"); return; }

            try
            {
                var bmp = WindowCapture.CaptureWindow(hwnd);
                if (bmp is null) { _logger.LogWarning("WindowCapture returned null for {Path}", path); return; }
                bmp.Save(path);
                _logger.LogInformation("Window snapshot saved: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Window capture failed for {Path}", path);
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
