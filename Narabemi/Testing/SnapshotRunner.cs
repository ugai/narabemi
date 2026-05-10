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

        // Retry harness — verify per-player PNG dims against expected and retry if
        // they don't match (mpv occasionally captures a pre-crop frame on first run).
        private const int MaxCaptureAttempts = 5;
        private const int RetryDelayMs = 400;
        private int _captureAttempt;

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
            // Pause both players BEFORE seeking — independent playback would otherwise
            // let A and B drift to different frames between seek and capture, putting
            // their seam columns one or two source frames apart. Exact seek + paused
            // freezes them on identical frames.
            _vm.PlayerA.Pause();
            if (HasPlayerB) _vm.PlayerB.Pause();

            _vm.PlayerA.SeekTo(_args.SeekSeconds, exact: true);
            if (HasPlayerB) _vm.PlayerB.SeekTo(_args.SeekSeconds, exact: true);
            _logger.LogInformation("Paused + seeked exact to {Seek}s, settling…", _args.SeekSeconds);

            _state = State.WaitSettle;
            DispatcherTimer.RunOnce(BeginCapture, TimeSpan.FromMilliseconds(PostSeekDelayMs));
        }

        private void BeginCapture()
        {
            if (_state != State.WaitSettle) return;
            _state = State.Capturing;
            _captureAttempt = 0;
            AttemptCapture();
        }

        private void AttemptCapture()
        {
            _captureAttempt++;
            try
            {
                // Re-apply crops right before each attempt — covers both the first-run
                // race (pre-crop frame from decoder warmup) and any seek-induced filter
                // chain reset that may have dropped the previous video-crop.
                _vm.UpdateCrops();

                // Give mpv one frame interval to render the (re-)cropped output.
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        CaptureAll();
                        var (ok, detail) = VerifyCaptureDims();
                        if (ok || _captureAttempt >= MaxCaptureAttempts)
                        {
                            if (!ok)
                                _logger.LogError("Snapshot crop verification failed after {N} attempts: {D}",
                                    _captureAttempt, detail);
                            else if (_captureAttempt > 1)
                                _logger.LogInformation("Snapshot succeeded on attempt {N} ({D})",
                                    _captureAttempt, detail);

                            int exitCode = ok ? 0 : 3;
                            if (ok && _args.VerifyWipe && HasPlayerB)
                                exitCode = RunWipeVerification();

                            Shutdown(exitCode);
                            return;
                        }
                        _logger.LogWarning("Capture attempt {N} mismatch: {D}; retrying after {Ms}ms",
                            _captureAttempt, detail, RetryDelayMs);
                        DispatcherTimer.RunOnce(AttemptCapture, TimeSpan.FromMilliseconds(RetryDelayMs));
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

        private (bool ok, string detail) VerifyCaptureDims()
        {
            var dirOut   = Path.GetDirectoryName(Path.GetFullPath(_args.OutputPath)) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(_args.OutputPath);
            var ext      = Path.GetExtension(_args.OutputPath);

            var pathA = HasPlayerB ? Path.Combine(dirOut, $"{baseName}_a{ext}")
                                   : Path.GetFullPath(_args.OutputPath);
            var (gotAW, gotAH) = PngHelper.ReadDimensions(pathA);
            var (expAW, expAH) = ExpectedCrop(_vm.PlayerA, isFirst: true);
            _logger.LogInformation("[Verify] A: got {GW}x{GH} expected {EW}x{EH} (sourceW={SW} ratio={R:F4})",
                gotAW, gotAH, expAW, expAH, _vm.PlayerA.SourceWidth, _vm.BlendRatio);
            if (expAW <= 0)
                return (false, $"A source dims unknown (got {gotAW}x{gotAH})");
            if (gotAW != expAW || gotAH != expAH)
                return (false, $"A got {gotAW}x{gotAH}, expected {expAW}x{expAH}");

            if (HasPlayerB)
            {
                var pathB = Path.Combine(dirOut, $"{baseName}_b{ext}");
                var (gotBW, gotBH) = PngHelper.ReadDimensions(pathB);
                var (expBW, expBH) = ExpectedCrop(_vm.PlayerB, isFirst: false);
                _logger.LogInformation("[Verify] B: got {GW}x{GH} expected {EW}x{EH} (sourceW={SW})",
                    gotBW, gotBH, expBW, expBH, _vm.PlayerB.SourceWidth);
                if (expBW <= 0)
                    return (false, $"B source dims unknown (got {gotBW}x{gotBH})");
                if (gotBW != expBW || gotBH != expBH)
                    return (false, $"B got {gotBW}x{gotBH}, expected {expBW}x{expBH}");
            }
            return (true, "dims match");
        }

        /// <summary>
        /// Pixel-diff verification across the wipe seam. A's rightmost cropped column
        /// and B's leftmost cropped column correspond to ADJACENT pixels in the source
        /// frame (source col x = floor(W*ratio) - 1 vs x = floor(W*ratio)). For the
        /// same video on both players at the same timestamp, those columns must be
        /// nearly identical — a large diff indicates the wipe is mis-stitched.
        /// Returns 0 on pass, 4 on fail.
        /// </summary>
        private int RunWipeVerification()
        {
            try
            {
                var dirOut   = Path.GetDirectoryName(Path.GetFullPath(_args.OutputPath)) ?? string.Empty;
                var baseName = Path.GetFileNameWithoutExtension(_args.OutputPath);
                var ext      = Path.GetExtension(_args.OutputPath);
                var pathA = Path.Combine(dirOut, $"{baseName}_a{ext}");
                var pathB = Path.Combine(dirOut, $"{baseName}_b{ext}");

                var (gotAW, _) = PngHelper.ReadDimensions(pathA);
                if (gotAW <= 0)
                {
                    _logger.LogError("[VerifyWipe] could not read {Path}", pathA);
                    return 4;
                }

                // Seam: A's last cropped column (source x = round(W*ratio) - 1) vs
                // B's first cropped column (source x = round(W*ratio)). Adjacent.
                var (seamAvg, seamMax, rows) = WipeVerifier.DiffColumns(pathA, gotAW - 1, pathB, 0);

                // Baseline: an internal adjacent-column diff within A itself. Tells us
                // what "two adjacent pixels in this scene" looks like, independent of
                // wipe alignment. The seam diff should be in the same ballpark as this.
                int baseColA = Math.Max(0, gotAW / 2 - 1);
                var (baseAvg, baseMax, _) = WipeVerifier.DiffColumns(pathA, baseColA, pathA, baseColA + 1);

                // Pass if seam diff isn't dramatically larger than the natural adjacent-
                // pixel diff in the scene. 1.5x baseline tolerates sub-pixel rendering
                // jitter and minor encoder noise without false-pass for hard misalignments.
                var pass = seamAvg >= 0 && seamAvg <= Math.Max(baseAvg * 1.5, 30.0);

                _logger.LogInformation(
                    "[VerifyWipe] {Result} — seam avg={SeamAvg:F2} max={SeamMax} | baseline avg={BaseAvg:F2} max={BaseMax} | rows={Rows}",
                    pass ? "PASS" : "FAIL", seamAvg, seamMax, baseAvg, baseMax, rows);

                return pass ? 0 : 4;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VerifyWipe] failed");
                return 4;
            }
        }

        private (int w, int h) ExpectedCrop(VideoPlayerViewModel p, bool isFirst)
        {
            // Mirrors MainWindowViewModel.ApplyCrop's math; can't share directly because
            // ApplyCrop is private and producing the string only — we want the int dims.
            var w = p.SourceWidth;
            var h = p.SourceHeight;
            if (w <= 0 || h <= 0) return (0, 0);
            var r = Math.Clamp(_vm.BlendRatio, 0.02, 0.98);
            if (_vm.BlendMode == 0)
            {
                var x = (int)Math.Round(w * r);
                return isFirst ? (x, h) : (w - x, h);
            }
            else
            {
                var y = (int)Math.Round(h * r);
                return isFirst ? (w, y) : (w, h - y);
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
