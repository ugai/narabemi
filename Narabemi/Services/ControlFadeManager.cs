using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Epoxy;
using Microsoft.Extensions.Logging;

namespace Narabemi.Services
{
    [INotifyPropertyChanged]
    public partial class ControlFadeManager : IRecipient<ControlsMouseMoveMessage>, IRecipient<ControlsVisibilityMessage>
    {
        public Storyboard ShowControlStoryboard { get; } = new();
        public Storyboard HideControlStoryboard { get; } = new();
        public bool IsVisible { get; private set; } = true;

        private readonly Duration _durationShow = new(TimeSpan.FromMilliseconds(100));
        private readonly Duration _durationHide = new(TimeSpan.FromMilliseconds(300));
        private readonly PropertyPath _propPath = new("Opacity");
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

        public void AddAnimationTarget(DependencyObject target)
        {
            var showAnim = new DoubleAnimation(0.0, 1.0, _durationShow);
            var hideAnim = new DoubleAnimation(1.0, 0.0, _durationHide);
            ShowControlStoryboard.Children.Add(showAnim);
            HideControlStoryboard.Children.Add(hideAnim);
            Storyboard.SetTargetProperty(showAnim, _propPath);
            Storyboard.SetTargetProperty(hideAnim, _propPath);
            Storyboard.SetTarget(showAnim, target);
            Storyboard.SetTarget(hideAnim, target);
        }

        private void OnTimer(object? sender, ElapsedEventArgs e)
        {
            _logger.LogTrace("{name}: {value}", nameof(OnTimer), e.SignalTime);

            if (IsVisible)
            {
                var elapsed = e.SignalTime.ToUniversalTime() - _lastMouseMoveTime;
                if (elapsed > _hideStartDuration && _mouseHoverTargets.All(v => !v.IsMouseOver))
                    WeakReferenceMessenger.Default.Send(new ControlsVisibilityMessage(false));
            }
            else
            {
                _hideTimer.Stop();
            }
        }

        public void Receive(ControlsMouseMoveMessage message)
        {
            _logger.LogTrace("{name}: {value}", nameof(ControlsMouseMoveMessage));

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
                        ShowControlStoryboard.Begin();
                        _hideTimer.Start();
                        _mouseMoveTargets.ForEach(v => v.Cursor = Cursors.Arrow);
                    }
                    else
                    {
                        HideControlStoryboard.Begin();
                        _mouseMoveTargets.ForEach(v => v.Cursor = Cursors.None);
                    }

                    return ValueTask.CompletedTask;
                });

                IsVisible = newValue;
            }
        }
    }

    public class ControlsMouseMoveMessage { }
    public class ControlsVisibilityMessage : ValueChangedMessage<bool> { public ControlsVisibilityMessage(bool value) : base(value) { } }
}
