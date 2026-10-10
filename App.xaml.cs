using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace VoiceChatbot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Info($"{AppPaths.ProductName} {typeof(App).Assembly.GetName().Version} starting " +
                    $"({RuntimeInformation.OSDescription}, .NET {Environment.Version})");

        // Catch unhandled exceptions on UI thread
        DispatcherUnhandledException += (s, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
            // The full stack trace is in the log; the dialog stays short.
            MessageBox.Show(
                $"Something went wrong: {FriendlyErrors.Describe(args.Exception)}\n\n" +
                $"The details were written to the log in:\n{AppLog.LogDirectory}",
                $"{AppPaths.ProductName} Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            AppLog.Error($"Unhandled exception (terminating: {args.IsTerminating})" +
                         (ex == null ? $": {args.ExceptionObject}" : ""), ex);
            MessageBox.Show(
                $"Fatal Error:\n\n{ex?.Message ?? args.ExceptionObject?.ToString() ?? "unknown"}\n\nInner: {ex?.InnerException?.Message ?? "none"}",
                $"{AppPaths.ProductName} Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Catch async unhandled exceptions
        // Runs on the finalizer thread, where a dialog could block or deadlock: log it and move on.
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            try { AppLog.Error("Unobserved task exception", args.Exception); }
            catch { }
            args.SetObserved();
        };

        // Read the settings here so the saved theme is in place before MainWindow (StartupUri) is created.
        try
        {
            _startupSettings = SettingsManager.Load();
            ThemeManager.Apply(_startupSettings.Theme);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not apply the saved theme.", ex);
        }
    }

    private static AppSettings? _startupSettings;

    /// <summary>The settings OnStartup read, once (MainWindow takes them instead of reading the file again).</summary>
    internal static AppSettings? TakeStartupSettings()
    {
        var settings = _startupSettings;
        _startupSettings = null;
        return settings;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"{AppPaths.ProductName} exiting (code {e.ApplicationExitCode})");
        base.OnExit(e);
    }
}
