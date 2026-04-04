using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Narabemi.Mpv;
using Narabemi.Services;
using Narabemi.Settings;
using Narabemi.ViewModels;
using Narabemi.Views;
using ZLogger;

namespace Narabemi
{
    public partial class App : Application
    {
        public static string ProductName { get; } =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Narabemi";

        public static string Version { get; } =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        public static IServiceProvider Services { get; private set; } = null!;

        private IHost? _host;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                DisableAvaloniaDataAnnotationValidation();

                _host = CreateHostBuilder().Build();
                Services = _host.Services;

                var appStatesService = Services.GetRequiredService<AppStatesService>();
                appStatesService.LoadFile();

                var mainWindow = Services.GetRequiredService<MainWindow>();
                var mainVm = Services.GetRequiredService<MainWindowViewModel>();
                var fadeManager = Services.GetRequiredService<ControlFadeManager>();

                mainWindow.DataContext = mainVm;
                mainWindow.Initialize(fadeManager);

                desktop.MainWindow = mainWindow;
                desktop.ShutdownRequested += (_, _) =>
                {
                    appStatesService.ApplyFrom(mainVm);
                    appStatesService.SaveFile();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static IHostBuilder CreateHostBuilder() =>
            Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                })
                .ConfigureLogging((_, logging) =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Debug);
                    logging.AddZLoggerFile(Path.Combine(AppContext.BaseDirectory, "Narabemi.log"));
#if DEBUG
                    logging.AddZLoggerConsole();
#endif
                })
                .ConfigureServices((context, services) =>
                {
                    services.Configure<AppSettings>(context.Configuration);

                    services.AddSingleton<AppStatesService>();
                    services.AddSingleton<ControlFadeManager>();
                    services.AddSingleton<MpvPlayer>();
                    services.AddSingleton<VideoPlayerViewModel>();
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<MainWindow>();
                });

        private static void DisableAvaloniaDataAnnotationValidation()
        {
            var dataValidationPluginsToRemove =
                System.Linq.Enumerable.ToArray(
                    System.Linq.Enumerable.OfType<DataAnnotationsValidationPlugin>(
                        BindingPlugins.DataValidators));

            foreach (var plugin in dataValidationPluginsToRemove)
                BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
