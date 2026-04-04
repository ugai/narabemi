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

        private readonly TimeSpan _hideStartDuration = TimeSpan.FromMilliseconds(1000);

        private readonly List<Control> _mouseMoveTargets = new();
        private readonly List<Control> _mouseHoverTargets = new();
        private readonly ILogger _logger;

        private DateTime _lastMouseMoveTime = DateTime.UtcNow;
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
            target.PointerMoved += (_, _) => WeakReferenceMessenger.Default.Send(new ControlsMouseMoveMessage());
            _mouseMoveTargets.Add(target);
        }

        public void AddMouseHoverTarget(Control target) =>
            _mouseHoverTargets.Add(target);

        private void OnTimer(object? sender, EventArgs e)
        {
            if (IsVisible)
            {
                var elapsed = DateTime.UtcNow - _lastMouseMoveTime;
                if (elapsed > _hideStartDuration)
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
            _lastMouseMoveTime = DateTime.UtcNow;
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
