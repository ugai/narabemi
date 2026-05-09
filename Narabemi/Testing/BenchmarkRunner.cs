using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;
using Narabemi.ViewModels;

namespace Narabemi.Testing
{
    /// <summary>
    /// Runs the dual-HWND native pipeline for a fixed duration and reports per-player
    /// fps stats sampled from mpv's estimated-vf-fps property. Logs avg/min/max + drops.
    /// </summary>
    public sealed class BenchmarkRunner
    {
        private sealed class Stat
        {
            public string Name = "";
            public MpvPlayer Player = null!;
            public int    Samples;
            public double FpsSum;
            public double FpsMin = double.MaxValue;
            public double FpsMax;
            public long   StartDropped;
        }

        private readonly SnapshotArgs _args;
        private readonly MainWindowViewModel _vm;
        private readonly ILogger<BenchmarkRunner> _logger;
        private readonly List<Stat> _stats = new();
        private long _startTick;
        private DispatcherTimer? _sampleTimer;
        private bool _samplingStarted;

        public BenchmarkRunner(
            SnapshotArgs args,
            MainWindowViewModel vm,
            ILogger<BenchmarkRunner> logger)
        {
            _args = args;
            _vm = vm;
            _logger = logger;
        }

        public void Start()
        {
            // FileLoaded fires per loadfile (and again on reconfigure). Wait for the first one
            // from each player whose VideoPath is set, then start sampling exactly once.
            _vm.PlayerA.MpvPlayer.FileLoaded += OnAnyFileLoaded;
            _vm.PlayerB.MpvPlayer.FileLoaded += OnAnyFileLoaded;

            _logger.LogInformation("Benchmark queued (duration={Sec}s)", _args.BenchSeconds);
        }

        private void OnAnyFileLoaded()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_samplingStarted) return;

                // Determine which players have a video loaded right now.
                // Source of truth is the VM's VideoPath (set before LoadFile is invoked).
                var hasA = !string.IsNullOrEmpty(_vm.PlayerA.VideoPath);
                var hasB = !string.IsNullOrEmpty(_vm.PlayerB.VideoPath);

                if (!hasA && !hasB) return;

                // Wait a frame so the second player can also fire FileLoaded if both are loading.
                DispatcherTimer.RunOnce(() =>
                {
                    if (_samplingStarted) return;
                    _samplingStarted = true;

                    if (hasA) _stats.Add(new Stat { Name = "A", Player = _vm.PlayerA.MpvPlayer });
                    if (hasB) _stats.Add(new Stat { Name = "B", Player = _vm.PlayerB.MpvPlayer });
                    BeginSampling();
                }, TimeSpan.FromSeconds(1.0));
            });
        }

        private void BeginSampling()
        {
            _startTick = Stopwatch.GetTimestamp();
            foreach (var s in _stats)
            {
                s.StartDropped = ParseLong(s.Player.GetPropertyStr("vo-drop-frame-count"));
                var hwdec = s.Player.GetPropertyStr("hwdec-current") ?? "?";
                var codec = s.Player.GetPropertyStr("video-codec") ?? "?";
                var contFps = s.Player.GetPropertyStr("container-fps") ?? "?";
                _logger.LogInformation("[Bench:{N}] start hwdec={Hwdec} codec={Codec} containerFps={CF}",
                    s.Name, hwdec, codec, contFps);
            }

            _sampleTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background,
                OnSampleTick);
            _sampleTimer.Start();

            DispatcherTimer.RunOnce(EndSampling, TimeSpan.FromSeconds(_args.BenchSeconds));
        }

        private void OnSampleTick(object? sender, EventArgs e)
        {
            foreach (var s in _stats)
            {
                if (!double.TryParse(s.Player.GetPropertyStr("estimated-vf-fps"),
                                     NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                    continue;
                if (fps <= 0) continue;
                s.Samples++;
                s.FpsSum += fps;
                if (fps < s.FpsMin) s.FpsMin = fps;
                if (fps > s.FpsMax) s.FpsMax = fps;
            }
        }

        private void EndSampling()
        {
            _sampleTimer?.Stop();
            var elapsed = (Stopwatch.GetTimestamp() - _startTick) / (double)Stopwatch.Frequency;

            foreach (var s in _stats)
            {
                var dropped = ParseLong(s.Player.GetPropertyStr("vo-drop-frame-count")) - s.StartDropped;
                var avg = s.Samples > 0 ? s.FpsSum / s.Samples : 0;
                var min = s.Samples > 0 ? s.FpsMin : 0;
                _logger.LogInformation(
                    "[Bench:{N}] elapsed={E:F2}s vfFps avg={Avg:F2} min={Min:F2} max={Max:F2} samples={S} dropped={Drop}",
                    s.Name, elapsed, avg, min, s.FpsMax, s.Samples, dropped);
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                d.Shutdown(0);
        }

        private static long ParseLong(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }
}
