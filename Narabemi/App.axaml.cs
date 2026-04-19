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
using Narabemi.Gpu;
using Narabemi.Mpv;
using Narabemi.Services;
using Narabemi.Settings;
using Narabemi.Testing;
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

                var snapshotArgs = SnapshotArgs.Parse(desktop.Args);

                _host = CreateHostBuilder().Build();
                Services = _host.Services;

                var appStatesService = Services.GetRequiredService<AppStatesService>();
                appStatesService.LoadFile();

                // In snapshot/bench mode, override video paths before ApplyTo runs
                if (snapshotArgs.IsSnapshotMode || snapshotArgs.IsBenchMode)
                {
                    var state = appStatesService.Current!;
                    if (snapshotArgs.VideoPathA is not null)
                    {
                        state.VideoPathList.Clear();
                        state.VideoPathList.Add(snapshotArgs.VideoPathA);
                        if (snapshotArgs.VideoPathB is not null)
                            state.VideoPathList.Add(snapshotArgs.VideoPathB);
                    }
                }

                var mainWindow = Services.GetRequiredService<MainWindow>();
                var mainVm = Services.GetRequiredService<MainWindowViewModel>();
                var fadeManager = Services.GetRequiredService<ControlFadeManager>();

                mainWindow.DataContext = mainVm;
                mainWindow.Initialize(fadeManager);
                if (snapshotArgs.IsSnapshotMode || snapshotArgs.IsBenchMode)
                    mainVm.IsSnapshotMode = true;

                desktop.MainWindow = mainWindow;

                // Don't save appstates on exit in snapshot/bench mode (avoids overwriting user state)
                if (!snapshotArgs.IsSnapshotMode && !snapshotArgs.IsBenchMode)
                {
                    desktop.ShutdownRequested += (_, _) =>
                    {
                        appStatesService.ApplyFrom(mainVm);
                        appStatesService.SaveFile();
                    };
                }

                // Start snapshot capture runner after window is shown
                if (snapshotArgs.IsSnapshotMode)
                {
                    var syncManager = Services.GetRequiredService<FrameSyncManager>();
                    var logger = Services.GetRequiredService<ILogger<SnapshotRunner>>();
                    var runner = new SnapshotRunner(snapshotArgs, mainVm, syncManager, logger);
                    runner.Start(mainWindow);
                }
                else if (snapshotArgs.IsBenchMode)
                {
                    var syncManager = Services.GetRequiredService<FrameSyncManager>();
                    var logger = Services.GetRequiredService<ILogger<BenchmarkRunner>>();
                    var runner = new BenchmarkRunner(snapshotArgs, syncManager, logger);
                    runner.Start();
                }
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

                    // GPU pipeline services
                    services.AddSingleton<D3D11DeviceManager>();
                    services.AddSingleton<BlendRenderer>();
                    services.AddSingleton<FrameSyncManager>();

                    // Two independent mpv players (keyed)
                    services.AddKeyedSingleton<MpvPlayer>("PlayerA");
                    services.AddKeyedSingleton<MpvPlayer>("PlayerB");

                    // GL renderers (one per player — each owns a WGL context + FBO)
                    services.AddKeyedSingleton<MpvGlRenderer>("PlayerA",
                        (sp, _) => new MpvGlRenderer(
                            sp.GetRequiredKeyedService<MpvPlayer>("PlayerA"),
                            sp.GetRequiredService<D3D11DeviceManager>(),
                            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MpvGlRenderer>>()));
                    services.AddKeyedSingleton<MpvGlRenderer>("PlayerB",
                        (sp, _) => new MpvGlRenderer(
                            sp.GetRequiredKeyedService<MpvPlayer>("PlayerB"),
                            sp.GetRequiredService<D3D11DeviceManager>(),
                            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MpvGlRenderer>>()));

                    // Two independent VideoPlayerViewModels (keyed, each with their own MpvPlayer)
                    services.AddKeyedSingleton<VideoPlayerViewModel>("PlayerA",
                        (sp, _) => new VideoPlayerViewModel(
                            sp.GetRequiredKeyedService<MpvPlayer>("PlayerA"),
                            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VideoPlayerViewModel>>()));
                    services.AddKeyedSingleton<VideoPlayerViewModel>("PlayerB",
                        (sp, _) => new VideoPlayerViewModel(
                            sp.GetRequiredKeyedService<MpvPlayer>("PlayerB"),
                            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VideoPlayerViewModel>>()));

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
