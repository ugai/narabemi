using System.Numerics;
using System.Windows;
using System.Windows.Threading;
using Narabemi.Models;

namespace Narabemi
{
    public static class Utils
    {
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
