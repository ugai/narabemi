using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Epoxy;
using Microsoft.Extensions.Logging;

namespace Narabemi.Services
{
    [INotifyPropertyChanged]
    public partial class ControlFadeManager : IDisposable, IRecipient<ControlsMouseMoveMessage>, IRecipient<ControlsVisibilityMessage>
    {
        public bool IsVisible { get; private set; } = true;

        private readonly TimeSpan _hideStartDuration = TimeSpan.FromMilliseconds(1000);

        private readonly List<FrameworkElement> _mouseMoveTargets = new();
        private readonly List<FrameworkElement> _mouseHoverTargets = new();
        private readonly ILogger _logger;

        private DateTime _lastMouseMoveTime = DateTime.UtcNow;
        private Timer _hideTimer = new(500);

        public ControlFadeManager(ILogger<ControlFadeManager> logger)
        {
            _logger = logger;

            _hideTimer.Elapsed += OnTimer;
            _hideTimer.AutoReset = true;
            _hideTimer.Enabled = true;
            _hideTimer.Start();

            WeakReferenceMessenger.Default.RegisterAll(this);
        }

        public void AddMouseMoveTarget(FrameworkElement target)
        {
            target.MouseMove += (s, e) => WeakReferenceMessenger.Default.Send(new ControlsMouseMoveMessage());
            _mouseMoveTargets.Add(target);
        }

        public void AddMouseHoverTarget(FrameworkElement target) =>
            _mouseHoverTargets.Add(target);

        private async void OnTimer(object? sender, ElapsedEventArgs e)
        {
            _logger.LogTrace("{name}: {value}", nameof(OnTimer), e.SignalTime);

            if (IsVisible)
            {
                var elapsed = e.SignalTime.ToUniversalTime() - _lastMouseMoveTime;
                if (elapsed > _hideStartDuration)
                {
                    // IsMouseOver is a WPF DependencyProperty — read it and conditionally
                    // send the hide message atomically on the UI thread.
                    await UIThread.TryInvokeAsync(() =>
                    {
                        if (!_mouseHoverTargets.Any(v => v.IsMouseOver))
                            WeakReferenceMessenger.Default.Send(new ControlsVisibilityMessage(false));
                        return ValueTask.CompletedTask;
                    });
                }
            }
            else
            {
                _hideTimer.Stop();
            }
        }

        public void Receive(ControlsMouseMoveMessage message)
        {
            _logger.LogTrace("{name}", nameof(ControlsMouseMoveMessage));

            _lastMouseMoveTime = DateTime.UtcNow;
            if (!IsVisible)
                WeakReferenceMessenger.Default.Send(new ControlsVisibilityMessage(true));
        }

        public async void Receive(ControlsVisibilityMessage message)
        {
            _logger.LogTrace("{name}: {value}", nameof(ControlsVisibilityMessage), message.Value);

            var newValue = message.Value;
            var oldValue = IsVisible;
            if (newValue != oldValue)
            {
                await UIThread.TryInvokeAsync(() =>
                {
                    if (newValue)
                    {
                        _hideTimer.Start();
                        _mouseMoveTargets.ForEach(v => v.Cursor = Cursors.Arrow);
                    }
                    else
                    {
                        _mouseMoveTargets.ForEach(v => v.Cursor = Cursors.None);
                    }

                    IsVisible = newValue;
                    return ValueTask.CompletedTask;
                });
            }
        }

        public void Dispose()
        {
            _hideTimer.Stop();
            _hideTimer.Dispose();
        }
    }

    public class ControlsMouseMoveMessage { }
    public class ControlsVisibilityMessage : ValueChangedMessage<bool> { public ControlsVisibilityMessage(bool value) : base(value) { } }
}
