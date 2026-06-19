using System;
using System.IO;
using System.Windows;

namespace VoiceChatbot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 1 && e.Args[0].Equals("--vad-self-test", StringComparison.OrdinalIgnoreCase))
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Models", "silero_vad.onnx");
            using var vad = new SileroVad(modelPath);
            Console.WriteLine($"SILERO_VAD_OK:{vad.SelfTest():0.000000}");
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // Catch unhandled exceptions on UI thread
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                $"UI Error:\n\n{args.Exception}\n\nInner: {args.Exception.InnerException?.Message ?? "none"}",
                "Voice Chatbot Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show(
                $"Fatal Error:\n\n{ex?.Message ?? args.ExceptionObject?.ToString() ?? "unknown"}\n\nInner: {ex?.InnerException?.Message ?? "none"}",
                "Voice Chatbot Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Catch async unhandled exceptions
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            MessageBox.Show(
                $"Task Error:\n\n{args.Exception}\n\nInner: {args.Exception?.InnerException?.Message ?? "none"}",
                "Voice Chatbot Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.SetObserved();
        };
    }
}
