using System;
using System.Windows;
using System.Windows.Media.Animation;
using CommunityToolkit.Mvvm.Messaging;
using Narabemi.Services;

namespace Narabemi.UI.Controls
{
    /// <summary>
    /// UI-layer helper that subscribes to <see cref="ControlsVisibilityMessage"/> and drives
    /// fade-in / fade-out animations on registered <see cref="DependencyObject"/> targets.
    /// Keeps WPF rendering types (<see cref="Storyboard"/>, <see cref="DoubleAnimation"/>)
    /// out of the service layer.
    /// </summary>
    public sealed class ControlFadeAnimator : IDisposable
    {
        private readonly Storyboard _showStoryboard = new();
        private readonly Storyboard _hideStoryboard = new();

        private readonly Duration _durationShow = new(TimeSpan.FromMilliseconds(100));
        private readonly Duration _durationHide = new(TimeSpan.FromMilliseconds(300));
        private readonly PropertyPath _propPath = new("Opacity");

        public ControlFadeAnimator()
        {
            WeakReferenceMessenger.Default.Register<ControlsVisibilityMessage>(this, (r, m) =>
                ((ControlFadeAnimator)r).OnVisibilityChanged(m.Value));
        }

        /// <summary>
        /// Registers a <see cref="DependencyObject"/> to be faded in/out when
        /// <see cref="ControlsVisibilityMessage"/> is received.
        /// </summary>
        public void AddTarget(DependencyObject target)
        {
            var showAnim = new DoubleAnimation(0.0, 1.0, _durationShow);
            var hideAnim = new DoubleAnimation(1.0, 0.0, _durationHide);

            _showStoryboard.Children.Add(showAnim);
            _hideStoryboard.Children.Add(hideAnim);

            Storyboard.SetTargetProperty(showAnim, _propPath);
            Storyboard.SetTargetProperty(hideAnim, _propPath);
            Storyboard.SetTarget(showAnim, target);
            Storyboard.SetTarget(hideAnim, target);
        }

        private void OnVisibilityChanged(bool isVisible)
        {
            if (isVisible)
                _showStoryboard.Begin();
            else
                _hideStoryboard.Begin();
        }

        public void Dispose() =>
            WeakReferenceMessenger.Default.Unregister<ControlsVisibilityMessage>(this);
    }
}
