using System.Collections.Generic;
using System.Windows.Media;

namespace Narabemi.Settings
{
    /// <summary>
    /// Represents the target for reading and writing application state.
    /// Implemented by the main view model to decouple the Settings layer from the UI layer.
    /// </summary>
    public interface IAppStateTarget
    {
        bool Loop { get; set; }
        bool AutoSync { get; set; }
        int MainPlayerIndex { get; set; }
        IList<IAppStatePlayerTarget> StatePlayers { get; }
        double BlendBorderWidth { get; set; }
        Color BlendBorderColor { get; set; }
    }

    /// <summary>
    /// Represents the per-player state exposed to <see cref="AppStatesService"/>.
    /// </summary>
    public interface IAppStatePlayerTarget
    {
        string VideoPath { get; set; }
    }
}
