using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;

namespace Narabemi.Services
{
    public partial class ControlFadeManager : ObservableObject, IDisposable, IRecipient<ControlsMouseMoveMessage>, IRecipient<ControlsVisibilityMessage>
    {
        public bool IsVisible { get; private set; } = true;

        private const long HideStartDurationMs = 1000;
        private const long ThrottleMs = 50;

        // Reused singleton — ControlsMouseMoveMessage carries no data.
        private static readonly ControlsMouseMoveMessage _sharedMoveMessage = new();

        private readonly List<Control> _mouseMoveTargets = new();
        private readonly List<Control> _mouseHoverTargets = new();
        private readonly ILogger _logger;

        // TickCount64 (ms) of the last accepted pointer-move message sent to the messenger.
        private long _lastSendTickCount = Environment.TickCount64;

        // TickCount64 (ms) of the last received ControlsMouseMoveMessage.
        private long _lastMouseMoveTick = Environment.TickCount64;

        private readonly DispatcherTimer _hideTimer;

        public ControlFadeManager(ILogger<ControlFadeManager> logger)
        {
            _logger = logger;

            _hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnTimer);
            _hideTimer.Start();

            WeakReferenceMessenger.Default.RegisterAll(this);
        }

        public void AddMouseMoveTarget(Control target)
        {
            target.PointerMoved += (_, _) =>
            {
                // Throttle: only forward pointer-move messages at most once per ThrottleMs.
                var now = Environment.TickCount64;
                if (now - _lastSendTickCount < ThrottleMs)
                    return;

                _lastSendTickCount = now;
                WeakReferenceMessenger.Default.Send(_sharedMoveMessage);
            };
            _mouseMoveTargets.Add(target);
        }

        public void AddMouseHoverTarget(Control target) =>
            _mouseHoverTargets.Add(target);

        private void OnTimer(object? sender, EventArgs e)
        {
            if (IsVisible)
            {
                var elapsedMs = Environment.TickCount64 - _lastMouseMoveTick;
                if (elapsedMs > HideStartDurationMs)
                {
                    if (!_mouseHoverTargets.Any(v => v.IsPointerOver))
                        WeakReferenceMessenger.Default.Send(new ControlsVisibilityMessage(false));
                }
            }
            else
            {
                _hideTimer.Stop();
            }
        }

        public void Receive(ControlsMouseMoveMessage message)
        {
            _lastMouseMoveTick = Environment.TickCount64;
            if (!IsVisible)
                WeakReferenceMessenger.Default.Send(new ControlsVisibilityMessage(true));
        }

        public void Receive(ControlsVisibilityMessage message)
        {
            var newValue = message.Value;
            var oldValue = IsVisible;
            if (newValue != oldValue)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (newValue)
                    {
                        _hideTimer.Start();
                        _mouseMoveTargets.ForEach(v => v.Cursor = Cursor.Default);
                    }
                    else
                    {
                        _mouseMoveTargets.ForEach(v => v.Cursor = new Cursor(StandardCursorType.None));
                    }

                    IsVisible = newValue;
                });
            }
        }

        public void Dispose()
        {
            _hideTimer.Stop();
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    public class ControlsMouseMoveMessage { }
    public class ControlsVisibilityMessage : ValueChangedMessage<bool> { public ControlsVisibilityMessage(bool value) : base(value) { } }
}
