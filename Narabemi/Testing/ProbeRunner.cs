using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Narabemi.Mpv;

namespace Narabemi.Testing
{
    /// <summary>
    /// Validates whether the dual-HWND + native mpv D3D11 path can sustain target fps
    /// before committing to the full architectural rewrite. Bypasses the entire blend pipeline.
    ///
    /// Single player: --probe-native &lt;seconds&gt; --video-a path
    /// Dual player:   --probe-native &lt;seconds&gt; --video-a pathA --video-b pathB
    /// </summary>
    public sealed class ProbeRunner
    {
        private sealed class Slot
        {
            public string Name = "";
            public MpvVideoView View = null!;
            public MpvPlayer? Player;
            public long StartDecoded;
            public long StartDropped;
            public int SampleCount;
            public double VfFpsSum;
            public double VfFpsMin = double.MaxValue;
            public double VfFpsMax;
            public string? StartVideoPath;
        }

        private readonly SnapshotArgs _args;
        private readonly ILogger _logger;
        private readonly List<Slot> _slots = new();
        private long _startTick;
        private DispatcherTimer? _sampleTimer;
        private int _readyCount;

        public ProbeRunner(SnapshotArgs args, ILogger logger)
        {
            _args = args;
            _logger = logger;
        }

        public void Start()
        {
            if (string.IsNullOrEmpty(_args.VideoPathA))
            {
                _logger.LogError("--probe-native requires --video-a");
                Shutdown(1);
                return;
            }

            var dual = !string.IsNullOrEmpty(_args.VideoPathB);

            _slots.Add(new Slot { Name = "A", View = new MpvVideoView(), StartVideoPath = _args.VideoPathA });
            if (dual)
                _slots.Add(new Slot { Name = "B", View = new MpvVideoView(), StartVideoPath = _args.VideoPathB });

            Control content;
            if (dual)
            {
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                };
                Grid.SetColumn(_slots[0].View, 0);
                Grid.SetColumn(_slots[1].View, 1);
                grid.Children.Add(_slots[0].View);
                grid.Children.Add(_slots[1].View);
                content = grid;
            }
            else
            {
                content = _slots[0].View;
            }

            var window = new Window
            {
                Title = dual ? "Narabemi Probe (native D3D11, dual)" : "Narabemi Probe (native D3D11)",
                Width = 1280,
                Height = 720,
                Background = Brushes.Black,
                Content = content,
            };

            foreach (var slot in _slots)
            {
                var captured = slot;
                slot.View.HandleReady += hwnd => OnHandleReady(captured, hwnd);
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
                window.Show();
            }
        }

        private void OnHandleReady(Slot slot, IntPtr hwnd)
        {
            try
            {
                slot.Player = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
                slot.Player.InitNativeD3D11(hwnd.ToInt64());
                slot.Player.LoadFile(slot.StartVideoPath!);
                slot.Player.Loop = true;
                slot.Player.Play();

                if (System.Threading.Interlocked.Increment(ref _readyCount) == _slots.Count)
                {
                    // Both players ready — wait briefly for hwdec/codec settle, then begin sampling.
                    DispatcherTimer.RunOnce(BeginSampling, TimeSpan.FromSeconds(1.5));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Probe failed during init for slot {Slot}", slot.Name);
                Shutdown(2);
            }
        }

        private void BeginSampling()
        {
            _startTick = Stopwatch.GetTimestamp();

            foreach (var s in _slots)
            {
                if (s.Player is null) continue;
                s.StartDecoded = ParseLong(s.Player.GetPropertyStr("decoder-frame-count"));
                s.StartDropped = ParseLong(s.Player.GetPropertyStr("vo-drop-frame-count"));

                var hwdec = s.Player.GetPropertyStr("hwdec-current") ?? "?";
                var codec = s.Player.GetPropertyStr("video-codec") ?? "?";
                var contFps = s.Player.GetPropertyStr("container-fps") ?? "?";
                _logger.LogInformation(
                    "[Probe-Native:{N}] start hwdec={Hwdec} codec={Codec} containerFps={CF}",
                    s.Name, hwdec, codec, contFps);
            }

            _logger.LogInformation("[Probe-Native] sampling for {Sec}s", _args.ProbeSeconds);

            _sampleTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background,
                OnSampleTick);
            _sampleTimer.Start();

            DispatcherTimer.RunOnce(EndSampling, TimeSpan.FromSeconds(_args.ProbeSeconds));
        }

        private void OnSampleTick(object? sender, EventArgs e)
        {
            foreach (var s in _slots)
            {
                if (s.Player is null) continue;
                if (!double.TryParse(s.Player.GetPropertyStr("estimated-vf-fps"),
                                     NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                    continue;
                if (fps <= 0) continue;

                s.SampleCount++;
                s.VfFpsSum += fps;
                if (fps < s.VfFpsMin) s.VfFpsMin = fps;
                if (fps > s.VfFpsMax) s.VfFpsMax = fps;
            }
        }

        private void EndSampling()
        {
            _sampleTimer?.Stop();

            var elapsed = (Stopwatch.GetTimestamp() - _startTick) / (double)Stopwatch.Frequency;

            foreach (var s in _slots)
            {
                if (s.Player is null) continue;
                var decoded  = ParseLong(s.Player.GetPropertyStr("decoder-frame-count")) - s.StartDecoded;
                var dropped  = ParseLong(s.Player.GetPropertyStr("vo-drop-frame-count")) - s.StartDropped;
                var avg      = s.SampleCount > 0 ? s.VfFpsSum / s.SampleCount : 0;
                var min      = s.SampleCount > 0 ? s.VfFpsMin : 0;
                var jitter   = s.Player.GetPropertyStr("vsync-jitter") ?? "?";
                var displayFps = s.Player.GetPropertyStr("display-fps") ?? "?";

                _logger.LogInformation(
                    "[Probe-Native:{N}] elapsed={E:F2}s decoded={Dec} dropped={Drop} vfFps avg={Avg:F2} min={Min:F2} max={Max:F2} samples={N2} | displayFps={DF} vsyncJitter={J}",
                    s.Name, elapsed, decoded, dropped, avg, min, s.VfFpsMax, s.SampleCount, displayFps, jitter);
            }

            Shutdown(0);
        }

        private void Shutdown(int code)
        {
            foreach (var s in _slots)
            {
                try { s.Player?.Dispose(); } catch { /* best-effort */ }
            }
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                d.Shutdown(code);
        }

        private static long ParseLong(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }
}
