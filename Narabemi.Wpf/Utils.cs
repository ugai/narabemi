using System;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Narabemi.Models;

namespace Narabemi
{
    public static class Utils
    {
        /// <summary>
        /// Shared handler for volume icon mouse-down events.
        /// Toggles mute state on left-click and marks the event as handled.
        /// </summary>
        /// <param name="e">The mouse event args passed from the command.</param>
        /// <param name="isMuted">Current mute state.</param>
        /// <param name="setMuted">Setter that applies the toggled mute state.</param>
        public static void ToggleMuteOnLeftClick(MouseEventArgs e, bool isMuted, Action<bool> setMuted)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                setMuted(!isMuted);
                e.Handled = true;
            }
        }

        /// <summary>
        /// [BUG] Rendering bug when SizeToContent is set. #378
        /// https://github.com/Kinnara/ModernWpf/issues/378
        /// </summary>
        /// <param name="window"></param>
        public static void FixModernWpfSizeSizeToContentUIGlitch(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                if (s is UIElement element)
                    element.Dispatcher.Invoke(element.InvalidateVisual, DispatcherPriority.Input);
            };
        }

        public static AspectRatio GetAspectRatio(int width, int height)
        {
            var gcd = (int)BigInteger.GreatestCommonDivisor(width, height);
            if (gcd > 0)
                return new AspectRatio(width / gcd, height / gcd);
            else
                return AspectRatios.Ratio_1_1;
        }
    }
}
