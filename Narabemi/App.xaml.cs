using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var logger = Services?.GetService<ILogger<App>>();
            logger?.LogCritical(e.Exception, "Unhandled exception on UI thread");
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe application will now close.",
                $"{ProductName} — Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var logger = Services?.GetService<ILogger<App>>();
            var ex = e.ExceptionObject as Exception;
            logger?.LogCritical(ex, "Unhandled exception on background thread (IsTerminating={IsTerminating})", e.IsTerminating);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var logger = Services?.GetService<ILogger<App>>();
            logger?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        }

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
                        .AddSingleton(new VersionWindowViewModel()
                        {
                            VersionText = $"{App.ProductName} v{App.Version}",
                            SiteUrl = App.SiteUrl,
                        });
                });

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                _host = CreateHostBuilder(e.Args).Build();
                Services = _host.Services;

                var logger = Services.GetRequiredService<ILogger<App>>();
                logger.LogInformation("{ProductName} v{Version}", ProductName, Version);

                var config = Services.GetRequiredService<IConfiguration>();
                var appSettings = config.Get<Settings.AppSettings>()
                    ?? throw new InvalidOperationException("Failed to load application settings from appsettings.json.");
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
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"{ProductName} failed to start.\n\n{ex.Message}",
                    ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Current.Shutdown(1);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                try
                {
                    var appStateManager = Services?.GetRequiredService<Settings.AppStatesService>();
                    appStateManager?.SaveFile();
                }
                finally
                {
                    await _host.StopAsync();
                    _host.Dispose();
                }
            }

            base.OnExit(e);
        }

        private static string GetProductName() => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? string.Empty;
        private static string GetVersion() => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    }
}
