namespace Narabemi.Settings
{
    /// <summary>
    /// Read-only application settings. Managed by Generic Host.
    /// </summary>
    public class AppSettings
    {
        public string MpvDirectory { get; set; } = "./mpv";
        public uint SyncTimerMs { get; set; } = 30;
    }
}
