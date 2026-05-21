using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using CommunityToolkit.Mvvm.Messaging;
using Narabemi.Services;

namespace Narabemi.UI.Controls
{
    /// <summary>
    /// UI-layer helper that subscribes to <see cref="ControlsVisibilityMessage"/> and drives
    /// fade-in / fade-out animations on registered targets via Avalonia Transitions.
    /// </summary>
    public sealed class ControlFadeAnimator : IDisposable
    {
        private readonly List<Visual> _targets = new();
        private readonly TimeSpan _durationShow = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan _durationHide = TimeSpan.FromMilliseconds(300);

        public ControlFadeAnimator()
        {
            WeakReferenceMessenger.Default.Register<ControlsVisibilityMessage>(this, (r, m) =>
                ((ControlFadeAnimator)r).OnVisibilityChanged(m.Value));
        }

        public void AddTarget(Visual target)
        {
            _targets.Add(target);

            // Attach a Transition so that Opacity changes animate smoothly.
            // The duration is updated dynamically in OnVisibilityChanged.
            if (target is Animatable animatable)
            {
                animatable.Transitions ??= new Transitions();
                animatable.Transitions.Add(new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = _durationHide,
                });
            }
        }

        private void OnVisibilityChanged(bool isVisible)
        {
            var to = isVisible ? 1.0 : 0.0;
            var duration = isVisible ? _durationShow : _durationHide;

            foreach (var target in _targets)
            {
                // Update transition duration to match show/hide
                if (target is Animatable animatable && animatable.Transitions is not null)
                {
                    foreach (var t in animatable.Transitions)
                    {
                        if (t is DoubleTransition dt && dt.Property == Visual.OpacityProperty)
                            dt.Duration = duration;
                    }
                }

                target.Opacity = to;
            }
        }

        public void Dispose() =>
            WeakReferenceMessenger.Default.Unregister<ControlsVisibilityMessage>(this);
    }
}
