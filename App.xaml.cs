using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace VoiceChatbot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Info($"Voice Chatbot {typeof(App).Assembly.GetName().Version} starting " +
                    $"({RuntimeInformation.OSDescription}, .NET {Environment.Version})");

        // Catch unhandled exceptions on UI thread
        DispatcherUnhandledException += (s, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"UI Error:\n\n{args.Exception}\n\nInner: {args.Exception.InnerException?.Message ?? "none"}",
                "Voice Chatbot Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                "Voice Chatbot Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Catch async unhandled exceptions
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            MessageBox.Show(
                $"Task Error:\n\n{args.Exception}\n\nInner: {args.Exception?.InnerException?.Message ?? "none"}",
                "Voice Chatbot Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"Voice Chatbot exiting (code {e.ApplicationExitCode})");
        base.OnExit(e);
    }
}
