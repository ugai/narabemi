using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Narabemi.Gpu;

namespace Narabemi.Testing
{
    /// <summary>
    /// Runs the full GPU pipeline for a fixed duration and exits.
    /// Usage: --bench &lt;seconds&gt; --video-a path [--video-b path]
    /// Collects timing logs without capturing a screenshot.
    /// </summary>
    public sealed class BenchmarkRunner
    {
        private readonly SnapshotArgs _args;
        private readonly FrameSyncManager _syncManager;
        private readonly ILogger<BenchmarkRunner> _logger;

        private int _frameCount;
        private readonly long _startTick = Stopwatch.GetTimestamp();

        public BenchmarkRunner(
            SnapshotArgs args,
            FrameSyncManager syncManager,
            ILogger<BenchmarkRunner> logger)
        {
            _args = args;
            _syncManager = syncManager;
            _logger = logger;
        }

        public void Start()
        {
            BenchCounters.Presents = 0;
            _syncManager.BlendFrameReady += OnFrameReady;

            var timer = new DispatcherTimer(
                TimeSpan.FromSeconds(_args.BenchSeconds),
                DispatcherPriority.Normal,
                OnComplete);
            timer.Start();

            _logger.LogInformation(
                "Benchmark started ({Dur}s)", _args.BenchSeconds);
        }

        private void OnFrameReady() => Interlocked.Increment(ref _frameCount);

        private void OnComplete(object? sender, EventArgs e)
        {
            (sender as DispatcherTimer)?.Stop();
            _syncManager.BlendFrameReady -= OnFrameReady;

            double elapsedSec = (Stopwatch.GetTimestamp() - _startTick) * 1.0 / Stopwatch.Frequency;
            int blendFrames  = _frameCount;
            int presents     = BenchCounters.Presents;
            double blendFps  = blendFrames / elapsedSec;
            double presentFps = presents / elapsedSec;

            _logger.LogInformation(
                "[Bench] frames={F} presents={P} elapsed={E:F1}s blendFps={BF:F1} presentFps={PF:F1} drops={D}",
                blendFrames, presents, elapsedSec, blendFps, presentFps, blendFrames - presents);

            if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(0);
        }
    }
}
