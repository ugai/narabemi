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
                EnsureLibmpvPresent();

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

        /// <summary>
        /// Surfaces a clear message instead of letting a generic DllNotFoundException
        /// bubble up from the first P/Invoke call into libmpv.
        /// </summary>
        private static void EnsureLibmpvPresent()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
            if (File.Exists(path)) return;

            var msg =
                $"libmpv-2.dll not found at:\n  {path}\n\n" +
                "Download a Windows libmpv build (mpv-dev archive from https://mpv.io/installation/) " +
                "and copy libmpv-2.dll into Narabemi/lib/ before building. " +
                "See README.md for details.";
            throw new FileNotFoundException(msg, "libmpv-2.dll");
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
