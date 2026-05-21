using System.Collections.Generic;

namespace Narabemi.Settings
{
    /// <summary>
    /// Writeable application settings for runtime states.
    /// </summary>
    public class AppStates
    {
        public bool Loop { get; set; }
        public bool AutoSync { get; set; } = true;
        public int MainPlayerIndex { get; set; }
        public List<string> VideoPathList { get; set; } = new();
        public List<double> PlayerSpeedList { get; set; } = new();
        public List<double> PlayerTimeOffsetList { get; set; } = new();
        public double BlendBorderWidth { get; set; } = 1.0;
        public ColorRgba BlendBorderColor { get; set; } = ColorRgba.White;
        public double BlendRatio { get; set; } = 0.5;
        public int BlendMode { get; set; }

        // Window geometry. Width==0 means "not yet saved" → use XAML defaults on first run.
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public bool IsWindowMaximized { get; set; }
    }
}
