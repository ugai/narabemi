using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Epoxy;
using MahApps.Metro.IconPacks;
using Narabemi.Models;
using Unosquare.FFME.Common;

// `Epoxy.ValueConverter<TFrom, TTo>` implementations
// https://github.com/kekyo/Epoxy#valueconverter

namespace Narabemi
{
    public class TimeSpanToStringConverter : ValueConverter<TimeSpan, string>
    {
        public override bool TryConvert(TimeSpan from, out string result)
        {
            result = $"{(int)from.TotalHours:00}:{from.Minutes:00}:{from.Seconds:00}.{from.Milliseconds:000}";
            return true;
        }
    }

    public class TimeSpanToSecondsConverter : ValueConverter<TimeSpan, double>
    {
        public override bool TryConvert(TimeSpan from, out double result)
        {
            result = from.TotalSeconds;
            return true;
        }

        public override bool TryConvertBack(double to, out TimeSpan result)
        {
            if (double.IsNaN(to))
            {
                result = TimeSpan.Zero;
                return false;
            }

            result = TimeSpan.FromSeconds(to);
            return true;
        }
    }

    public class FilePathToFileNameConverter : ValueConverter<string, string>
    {
        public override bool TryConvert(string from, out string result)
        {
            result = Path.GetFileName(from);
            return !string.IsNullOrEmpty(result);
        }
    }

    public class DurationToSecondsConverter : ValueConverter<Duration, double>
    {
        public override bool TryConvert(Duration from, out double result)
        {
            result = from.TimeSpan.TotalSeconds;
            return true;
        }
    }

    public class ColorToStringConverter : ValueConverter<Color, string>
    {
        public override bool TryConvert(Color from, out string result)
        {
            result = from.ToString();
            return true;
        }

        public override bool TryConvertBack(string to, out Color result)
        {
            object colorObject = ColorConverter.ConvertFromString(to);
            if (colorObject is Color color)
            {
                result = color;
                return true;
            }

            result = Colors.White;
            return false;
        }
    }

    public class MediaPlaybackStateToIconKindConverter : ValueConverter<MediaPlaybackState, PackIconMaterialKind>
    {
        public override bool TryConvert(MediaPlaybackState from, out PackIconMaterialKind result)
        {
            result = from switch
            {
                MediaPlaybackState.Manual => PackIconMaterialKind.ProgressClock,
                MediaPlaybackState.Play => PackIconMaterialKind.Play,
                MediaPlaybackState.Close => PackIconMaterialKind.Minus,
                MediaPlaybackState.Pause => PackIconMaterialKind.Pause,
                MediaPlaybackState.Stop => PackIconMaterialKind.Stop,
                _ => PackIconMaterialKind.Help,
            };
            return true;
        }
    }

    public class GlobalPlaybackStateToTogglePlayPauseIconKindConverter : ValueConverter<GlobalPlaybackState, PackIconMaterialKind>
    {
        public override bool TryConvert(GlobalPlaybackState from, out PackIconMaterialKind result)
        {
            var nextState = from.TogglePlayPause();
            result = nextState switch
            {
                GlobalPlaybackState.Play => PackIconMaterialKind.Play,
                GlobalPlaybackState.Pause => PackIconMaterialKind.Pause,
                _ => throw new NotImplementedException(),
            };
            return true;
        }
    }

    public class BoolToMutedVolumeIconKindConverter : ValueConverter<bool, PackIconMaterialKind>
    {
        public override bool TryConvert(bool from, out PackIconMaterialKind result)
        {
            result = from ? PackIconMaterialKind.VolumeMute : PackIconMaterialKind.VolumeHigh;
            return true;
        }
    }

    public class AspectRatioToStringConverter : ValueConverter<AspectRatio, string>
    {
        public override bool TryConvert(AspectRatio to, out string result)
        {
            result = to.ToString();
            return true;
        }

        public override bool TryConvertBack(string from, out AspectRatio result)
        {
            if (!string.IsNullOrWhiteSpace(from))
            {
                var fields = from.Split(AspectRatio.Delimiter);
                if (fields.Length == 2 &&
                    double.TryParse(fields[0], out double numerator) && numerator > 0.0 &&
                    double.TryParse(fields[1], out double denominator) && denominator > 0.0)
                {
                    result = new(numerator, denominator);
                    return true;
                }
            }

            result = AspectRatios.Ratio_16_9;
            return false;
        }
    }

    public class InverseBooleanConverter : ValueConverter<bool, bool>
    {
        public override bool TryConvert(bool from, out bool result)
        {
            result = !from;
            return true;
        }

        public override bool TryConvertBack(bool to, out bool result)
        {
            result = !to;
            return true;
        }
    }

    public class BoolToLoopingBehaviorMediaPlaybackStateConverter : ValueConverter<bool, MediaPlaybackState>
    {
        public override bool TryConvert(bool from, out MediaPlaybackState result)
        {
            result = from ? MediaPlaybackState.Play : MediaPlaybackState.Pause;
            return true;
        }
    }
}
