using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VoiceChatbot;

// "Ask pi ..." / "pi agent, ...": read-only questions for the local Pi coding agent (PiAgentService).
public partial class MainWindow
{
    private async Task SendPiAgentMessageAsync(string userText, string piPrompt, string blockedReason)
    {
        AssistantMessageUi? assistantMessage = null;

        AddUserMessage(userText, new List<string>());
        _history.Add("user", userText);
        assistantMessage = AddAssistantMessage("");

        if (!string.IsNullOrWhiteSpace(blockedReason))
        {
            assistantMessage.Body.Text = blockedReason;
            SpeakLastResponse(blockedReason, assistantMessage);
            return;
        }

        var turnCts = BeginTurnCancellation();
        SetUIState("processing", "Asking Pi...");
        AddSystemMessage("Sending read-only request to Pi.");

        try
        {
            var response = await _piAgent.AskAsync(piPrompt, turnCts.Token);
            var cleaned = CleanDisplayText(response);
            SetAssistantMessageText(assistantMessage, cleaned);
            _history.Add("assistant", $"Pi result:\n{cleaned}");
            SpeakLastResponse(cleaned, assistantMessage);
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text += " [cancelled]";
            FinishTurn();
        }
        catch (Exception ex)
        {
            AppLog.Error("Pi agent request failed", ex);
            assistantMessage.Body.Text = $"Pi error: {ex.Message}";
            AddSystemMessage($"Pi error: {ex.Message}");
            FinishTurn();
        }
        finally
        {
            EndTurnCancellation(turnCts);
        }
    }
}
