using System.Collections.Generic;

namespace Narabemi.Settings
{
    /// <summary>
    /// Writeable applciation settings for runtime states.
    /// </summary>
    public class AppStates
    {
        public bool Loop { get; set; } = false;
        public bool AutoSync { get; set; } = true;
        public int MainPlayerIndex { get; set; } = 0;
        public List<string> VideoPathList { get; set; } = new();
        public double BlendBorderWidth { get; set; } = 1.0;
        public ColorRgba BlendBorderColor { get; set; } = ColorRgba.White;
    }
}
