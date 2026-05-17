using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            // Subscribe to all three global exception streams before any startup work
            // so that crashes during DI build, mpv init, or view-model construction
            // are captured rather than silently terminating the process.
            RegisterGlobalExceptionHandlers();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                try
                {
                    InitializeDesktop(desktop);
                }
                catch (Exception ex)
                {
                    // Startup failure (missing libmpv, corrupt appstates.json, DI resolution
                    // error, etc.) — log, write crash file, and show a dialog before exit.
                    var logger = TryGetLogger<App>();
                    CrashHandler.Handle(ex, logger, "startup");
                    FlushAndShutdownHost();
                    Environment.Exit(1);
                    return;
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void RegisterGlobalExceptionHandlers()
        {
            // CLR thread-pool and background threads.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var ex = args.ExceptionObject as Exception
                    ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown error");
                var logger = TryGetLogger<App>();
                CrashHandler.Handle(ex, logger, "AppDomain.UnhandledException");
                FlushAndShutdownHost();
                // IsTerminating is already true when this fires; let CLR exit.
            };

            // Fire-and-forget async tasks that faulted without an awaiter.
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                args.SetObserved();   // prevent CLR from re-throwing on GC finaliser thread
                var logger = TryGetLogger<App>();
                CrashHandler.Handle(args.Exception, logger, "TaskScheduler.UnobservedTaskException");
            };

            // Avalonia dispatcher (UI thread).
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                args.Handled = true;  // prevent Avalonia from re-throwing / crashing
                var logger = TryGetLogger<App>();
                CrashHandler.Handle(args.Exception, logger, "Dispatcher.UIThread.UnhandledException");
                FlushAndShutdownHost();
                Environment.Exit(1);
            };
        }

        private void InitializeDesktop(IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            var snapshotArgs = SnapshotArgs.Parse(desktop.Args);

            _host = CreateHostBuilder().Build();
            Services = _host.Services;

            if (snapshotArgs.IsProbeNativeMode)
            {
                var probeLogger = Services.GetRequiredService<ILogger<ProbeRunner>>();
                new ProbeRunner(snapshotArgs, probeLogger).Start();
                base.OnFrameworkInitializationCompleted();
                return;
            }

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
            // Skip window geometry restore in headless test modes so the fixed
            // 1280×720 XAML default is used for reproducible snapshot/bench output.
            var savedState = (snapshotArgs.IsSnapshotMode || snapshotArgs.IsBenchMode)
                ? null : appStatesService.Current;
            mainWindow.Initialize(fadeManager, savedState);
            if (snapshotArgs.IsSnapshotMode || snapshotArgs.IsBenchMode)
                mainVm.IsSnapshotMode = true;
            if (snapshotArgs.IsBenchMode)
                mainVm.IsBenchMode = true;

            // CLI overrides for layout — applied AFTER appstates restore so they win.
            // The state restore happens via vm.LoadedCommand which fires on Window.Loaded;
            // hook to re-apply overrides right after that.
            if (snapshotArgs.SetRatio.HasValue || snapshotArgs.SetMode.HasValue)
            {
                void ApplyOverrides(object? _, Avalonia.Interactivity.RoutedEventArgs __)
                {
                    if (snapshotArgs.SetMode.HasValue)  mainVm.BlendMode  = snapshotArgs.SetMode.Value;
                    if (snapshotArgs.SetRatio.HasValue) mainVm.BlendRatio = snapshotArgs.SetRatio.Value;
                    mainWindow.Loaded -= ApplyOverrides;
                }
                mainWindow.Loaded += ApplyOverrides;
            }

            desktop.MainWindow = mainWindow;

            // Persistence on shutdown is owned by MainWindowViewModel.Closed (the single
            // canonical save path). The duplicate ShutdownRequested handler that previously
            // lived here was removed — the VM command already gates on
            // IsSnapshotMode/IsBenchMode and handles the same ApplyFrom/SaveFile sequence.

            // Start snapshot/bench runner after window is shown
            if (snapshotArgs.IsSnapshotMode)
            {
                var logger = Services.GetRequiredService<ILogger<SnapshotRunner>>();
                var runner = new SnapshotRunner(snapshotArgs, mainVm, logger);
                runner.Start(mainWindow);
            }
            else if (snapshotArgs.IsBenchMode)
            {
                var logger = Services.GetRequiredService<ILogger<BenchmarkRunner>>();
                var runner = new BenchmarkRunner(snapshotArgs, mainVm, logger);
                runner.Start();
            }
        }

        /// <summary>
        /// Attempts to obtain an <see cref="ILogger{T}"/> from the DI container.
        /// Returns <c>null</c> if the host has not been built yet or has already
        /// been disposed.
        /// </summary>
        private ILogger<T>? TryGetLogger<T>()
        {
            try { return _host?.Services.GetService<ILogger<T>>(); }
            catch { return null; }
        }

        /// <summary>
        /// Stops the hosted service (which triggers ZLogger flush) without throwing.
        /// </summary>
        private void FlushAndShutdownHost()
        {
            try
            {
                _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch
            {
                // Swallow — we are already in an error path.
            }
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

                    // Two independent mpv players (keyed). Each renders directly into its own
                    // child HWND via vo=gpu, gpu-api=d3d11, hwdec=d3d11va — no shared GPU pipeline.
                    services.AddKeyedSingleton<MpvPlayer>("PlayerA");
                    services.AddKeyedSingleton<MpvPlayer>("PlayerB");

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
