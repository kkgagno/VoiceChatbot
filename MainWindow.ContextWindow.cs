using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Context window line (Chat Backend) ====================

    // The last detection a chat request used; for the message when a request still does not fit.
    private ServerContextWindow? _lastContextWindow;
    private DispatcherTimer? _contextStatusTimer;
    private bool _contextStatusForceDetect;
    private int _contextStatusVersion;

    /// <summary>
    /// Updates the line under the Context window box (the window requests use and where it comes from)
    /// shortly after the last change: typing a URL or model name changes it on every key. With
    /// <paramref name="forceDetect"/> the server is asked again instead of using the cached value.
    /// </summary>
    private void ScheduleContextWindowStatusRefresh(bool forceDetect = false)
    {
        if (ContextWindowStatusText is null || _ollama is null)
            return;

        _contextStatusForceDetect |= forceDetect;
        if (_contextStatusTimer is null)
        {
            _contextStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _contextStatusTimer.Tick += async (_, _) =>
            {
                _contextStatusTimer.Stop();
                var force = _contextStatusForceDetect;
                _contextStatusForceDetect = false;
                await RefreshContextWindowStatusAsync(force);
            };
        }

        _contextStatusTimer.Stop();
        _contextStatusTimer.Start();
    }

    private async Task RefreshContextWindowStatusAsync(bool forceDetect)
    {
        var version = ++_contextStatusVersion;
        var model = (ModelCombo.Text ?? "").Trim();
        var isOllama = !_ollama.IsOpenAiCompatibleBackend;

        ServerContextWindow window;
        try
        {
            // Detection makes HTTP requests: never on the UI thread, and never holding up startup.
            window = await Task.Run(() => _ollama.DetectContextWindowAsync(model, forceDetect));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Context window check failed", ex);
            window = ServerContextWindow.NotDetected(reachable: false);
        }

        // A newer check (other model, endpoint or setting) has started meanwhile.
        if (version != _contextStatusVersion)
            return;

        var effective = ResolveContextTokens(window);
        ContextWindowStatusText.Text = ContextWindowText.Describe(window, _settings.ContextWindow, effective, isOllama,
            hasModel: model.Length > 0);
    }
}
