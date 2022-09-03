namespace Narabemi.Settings
{
    /// <summary>
    /// Read-only application aettings. Magaged by Generic Host.
    /// </summary>
    public class AppSettings
    {
        public string FFmpegDirectory { get; set; } = string.Empty;
        public string ShaderPath { get; set; } = BlendEffect.DefaultShaderFilePath;
        public uint SyncTimerMs { get; set; } = 30;
    }
}
