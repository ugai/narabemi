using System;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Narabemi.UI.Windows;
using ZLogger;

namespace Narabemi
{

    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static string ProductName { get; } = GetProductName();
        public static string Version { get; } = GetVersion();
        public static Uri SiteUrl { get; } = new("https://github.com/ugai/narabemi");

        public static IServiceProvider? Services { get; private set; }

        private IHost? _host;

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, appConfiguration) =>
                {
                    appConfiguration.SetBasePath(context.HostingEnvironment.ContentRootPath);
                    appConfiguration.AddJsonFile("appsettings.json", false);
                })
                .ConfigureLogging(logging =>
                {
                    logging
                        .ClearProviders()
                        .SetMinimumLevel(LogLevel.Debug)
                        .AddDebug()
                        .AddZLoggerFile($"{ProductName}.log");
                })
                .ConfigureServices((_, services) =>
                {
                    services
                        .AddSingleton<Services.MediaElementsManager>()
                        .AddSingleton<Services.ControlFadeManager>()
                        .AddSingleton<Settings.AppStatesService>()
                        .AddSingleton<MainWindow>()
                        .AddSingleton<MainWindowViewModel>()
                        .AddTransient<VersionWindow>()
                        .AddSingleton(new VersionWindowViewModel()
                        {
                            VersionText = $"{App.ProductName} v{App.Version}",
                            SiteUrl = App.SiteUrl,
                        });
                });

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _host = CreateHostBuilder(e.Args).Build();
            Services = _host.Services;

            var logger = Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("{ProductName} v{Version}", ProductName, Version);

            var config = Services.GetRequiredService<IConfiguration>();
            var appSettings = config.Get<Settings.AppSettings>();
            var appStateManager = Services.GetRequiredService<Settings.AppStatesService>();
            appStateManager.LoadFile();

            Unosquare.FFME.Library.FFmpegDirectory = appSettings.FFmpegDirectory;
            logger.LogInformation("{Name}: '{FFmpegDirectory}'", nameof(appSettings.FFmpegDirectory), appSettings.FFmpegDirectory);

            // Start Generic Host
            await _host.StartAsync();

            // Start WPF MainWindow
            MainWindow = Services.GetRequiredService<MainWindow>();
            MainWindow.Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                var appStateManager = Services?.GetRequiredService<Settings.AppStatesService>();
                appStateManager?.SaveFile();

                await _host.StopAsync();
                _host.Dispose();
            }

            base.OnExit(e);
        }

        private static string GetProductName() => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? string.Empty;
        private static string GetVersion() => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    }
}
