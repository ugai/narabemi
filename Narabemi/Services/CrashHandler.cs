using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Narabemi.Services
{
    /// <summary>
    /// Handles unhandled exceptions by writing a crash log and showing a minimal
    /// error dialog. Called from the three global exception hooks in App.
    /// </summary>
    internal static class CrashHandler
    {
        /// <summary>
        /// Writes a crash log file to AppContext.BaseDirectory and, if called on or
        /// dispatched to the UI thread, shows a blocking error dialog before returning.
        /// Safe to call from background threads and from within exception handlers.
        /// </summary>
        internal static void Handle(Exception ex, ILogger? logger, string context)
        {
            // 1. Log via structured logger (best-effort — logger may not be built yet).
            try
            {
                logger?.LogCritical(ex, "Unhandled exception [{Context}]", context);
            }
            catch
            {
                // Swallow — logger itself might be broken.
            }

            // 2. Write a plain-text crash log alongside the executable.
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.WriteAllText(logPath,
                    $"{DateTime.Now:u}  [{context}]\n{ex}\n");
            }
            catch
            {
                // Swallow — disk might be full or path inaccessible.
            }

            // 3. Show a minimal error dialog on the UI thread.
            try
            {
                ShowErrorDialog(ex, context);
            }
            catch
            {
                // Swallow — UI may already be torn down.
            }
        }

        private static void ShowErrorDialog(Exception ex, string context)
        {
            // Build a self-contained error window that doesn't depend on the DI container
            // or XAML resources so it works even when those failed to initialise.
            void Show()
            {
                var message = $"An unexpected error occurred ({context}).\n\n" +
                              $"A crash log has been written to:\n  crash.log\n\n" +
                              $"{ex.GetType().Name}: {ex.Message}";

                var textBlock = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16),
                    MaxWidth = 560,
                };

                var button = new Button
                {
                    Content = "Close",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 0, 16, 16),
                };

                var panel = new StackPanel();
                panel.Children.Add(textBlock);
                panel.Children.Add(button);

                var owner = (Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

                var dialog = new Window
                {
                    Title = $"{App.ProductName} — Unhandled Error",
                    Content = panel,
                    Width = 600,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = owner is not null
                        ? WindowStartupLocation.CenterOwner
                        : WindowStartupLocation.CenterScreen,
                };

                button.Click += (_, _) => dialog.Close();

                if (owner is not null)
                    dialog.ShowDialog(owner).GetAwaiter().GetResult();
                else
                    dialog.Show();
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Show();
            }
            else
            {
                // Block the calling thread until the dialog is closed so the process
                // doesn't exit before the user has seen the message.
                using var done = new ManualResetEventSlim(false);
                Dispatcher.UIThread.Post(() =>
                {
                    try { Show(); }
                    finally { done.Set(); }
                });
                done.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }
}
