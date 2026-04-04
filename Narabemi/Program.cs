using System;
using System.IO;
using Avalonia;

namespace Narabemi
{
    sealed class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                BuildAvaloniaApp()
                    .StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.WriteAllText(logPath, $"{DateTime.Now}\n{ex}");
                Console.Error.WriteLine(ex);
                throw;
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
