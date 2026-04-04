using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Narabemi.Mpv
{
    /// <summary>
    /// High-level wrapper around a single mpv instance.
    /// Provides play/pause/seek/volume and property observation via a background event loop.
    /// </summary>
    public sealed class MpvPlayer : IDisposable
    {
        private IntPtr _ctx;
        private Thread? _eventThread;
        private volatile bool _disposed;
        private readonly ILogger _logger;

        // Observed property reply IDs
        private const ulong ReplyPosition = 1;
        private const ulong ReplyDuration = 2;
        private const ulong ReplyPause = 3;
        private const ulong ReplyEofReached = 4;

        public event Action<double>? PositionChanged;
        public event Action<double>? DurationChanged;
        public event Action<bool>? PauseChanged;
        public event Action? FileLoaded;
        public event Action? EndOfFile;

        public IntPtr Handle => _ctx;
        public bool IsInitialized => _ctx != IntPtr.Zero;

        public MpvPlayer(ILogger<MpvPlayer> logger)
        {
            _logger = logger;
        }

        public void Init(long windowId = 0)
        {
            _ctx = MpvApi.Create();
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("mpv_create failed");

            if (windowId != 0)
                CheckError(MpvApi.SetOptionString(_ctx, "wid", windowId.ToString()));

            CheckError(MpvApi.SetOptionString(_ctx, "keep-open", "yes"));
            CheckError(MpvApi.SetOptionString(_ctx, "keep-open-pause", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "idle", "yes"));
            CheckError(MpvApi.SetOptionString(_ctx, "input-default-bindings", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "input-vo-keyboard", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "osc", "no"));
            CheckError(MpvApi.SetOptionString(_ctx, "osd-level", "0"));

            CheckError(MpvApi.Initialize(_ctx));

            MpvApi.ObserveProperty(_ctx, ReplyPosition, "time-pos", MpvFormat.Double);
            MpvApi.ObserveProperty(_ctx, ReplyDuration, "duration", MpvFormat.Double);
            MpvApi.ObserveProperty(_ctx, ReplyPause, "pause", MpvFormat.Flag);
            MpvApi.ObserveProperty(_ctx, ReplyEofReached, "eof-reached", MpvFormat.Flag);

            _eventThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "mpv-event-loop",
            };
            _eventThread.Start();

            _logger.LogInformation("mpv initialized (wid={WindowId})", windowId);
        }

        public void LoadFile(string path)
        {
            EnsureInit();
            var err = MpvApi.CommandArgs(_ctx, "loadfile", path, "replace");
            CheckError(err);
            _logger.LogInformation("Loading file: {Path}", path);
        }

        public void Play()
        {
            EnsureInit();
            SetPause(false);
        }

        public void Pause()
        {
            EnsureInit();
            SetPause(true);
        }

        public void TogglePause()
        {
            EnsureInit();
            MpvApi.CommandArgs(_ctx, "cycle", "pause");
        }

        public void Stop()
        {
            EnsureInit();
            MpvApi.CommandArgs(_ctx, "stop");
        }

        public bool Loop
        {
            set
            {
                EnsureInit();
                MpvApi.SetPropertyString(_ctx, "loop-file", value ? "inf" : "no");
            }
        }

        public bool IsPaused
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetPropertyFlag(_ctx, "pause", MpvFormat.Flag, out var val) == 0)
                    return val != 0;
                return true;
            }
        }

        public void Seek(double seconds, bool absolute = true)
        {
            EnsureInit();
            var mode = absolute ? "absolute" : "relative";
            MpvApi.CommandArgs(_ctx, "seek", seconds.ToString("F3"), mode);
        }

        public double Volume
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "volume", MpvFormat.Double, out var val) == 0)
                    return val;
                return 100.0;
            }
            set
            {
                EnsureInit();
                var v = value;
                MpvApi.SetProperty(_ctx, "volume", MpvFormat.Double, ref v);
            }
        }

        public bool IsMuted
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetPropertyFlag(_ctx, "mute", MpvFormat.Flag, out var val) == 0)
                    return val != 0;
                return false;
            }
            set
            {
                EnsureInit();
                var v = value ? 1 : 0;
                MpvApi.SetPropertyFlag(_ctx, "mute", MpvFormat.Flag, ref v);
            }
        }

        public double Position
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "time-pos", MpvFormat.Double, out var val) == 0)
                    return val;
                return 0.0;
            }
        }

        public double Duration
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "duration", MpvFormat.Double, out var val) == 0)
                    return val;
                return 0.0;
            }
        }

        public double Speed
        {
            get
            {
                EnsureInit();
                if (MpvApi.GetProperty(_ctx, "speed", MpvFormat.Double, out var val) == 0)
                    return val;
                return 1.0;
            }
            set
            {
                EnsureInit();
                var v = value;
                MpvApi.SetProperty(_ctx, "speed", MpvFormat.Double, ref v);
            }
        }

        private void SetPause(bool paused)
        {
            var val = paused ? 1 : 0;
            MpvApi.SetPropertyFlag(_ctx, "pause", MpvFormat.Flag, ref val);
        }

        private void EventLoop()
        {
            while (!_disposed)
            {
                var evtPtr = MpvApi.WaitEvent(_ctx, 0.5);
                if (evtPtr == IntPtr.Zero) continue;

                var evt = Marshal.PtrToStructure<MpvEvent>(evtPtr);

                switch (evt.EventId)
                {
                    case MpvEventId.None:
                        break;
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.FileLoaded:
                        FileLoaded?.Invoke();
                        break;
                    case MpvEventId.EndFile:
                        EndOfFile?.Invoke();
                        break;
                    case MpvEventId.PropertyChange:
                        HandlePropertyChange(evt);
                        break;
                }
            }
        }

        private void HandlePropertyChange(MpvEvent evt)
        {
            if (evt.Data == IntPtr.Zero) return;

            var prop = Marshal.PtrToStructure<MpvEventProperty>(evt.Data);
            if (prop.Data == IntPtr.Zero) return;

            switch (evt.ReplyUserdata)
            {
                case ReplyPosition when prop.Format == MpvFormat.Double:
                    PositionChanged?.Invoke(Marshal.PtrToStructure<double>(prop.Data));
                    break;
                case ReplyDuration when prop.Format == MpvFormat.Double:
                    DurationChanged?.Invoke(Marshal.PtrToStructure<double>(prop.Data));
                    break;
                case ReplyPause when prop.Format == MpvFormat.Flag:
                    PauseChanged?.Invoke(Marshal.PtrToStructure<int>(prop.Data) != 0);
                    break;
                case ReplyEofReached when prop.Format == MpvFormat.Flag:
                    if (Marshal.PtrToStructure<int>(prop.Data) != 0)
                        EndOfFile?.Invoke();
                    break;
            }
        }

        private void EnsureInit()
        {
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("MpvPlayer is not initialized. Call Init() first.");
        }

        private void CheckError(int errorCode)
        {
            if (errorCode < 0)
            {
                var msg = MpvApi.GetErrorMessage(errorCode) ?? $"mpv error {errorCode}";
                _logger.LogError("mpv error: {Error}", msg);
                throw new InvalidOperationException($"mpv error: {msg}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_ctx != IntPtr.Zero)
            {
                MpvApi.TerminateDestroy(_ctx);
                _ctx = IntPtr.Zero;
            }

            _eventThread?.Join(TimeSpan.FromSeconds(2));
            _logger.LogInformation("mpv disposed");
        }
    }
}
