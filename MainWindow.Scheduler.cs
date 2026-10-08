using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Scheduler ====================

    private void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerWindow is { IsVisible: true })
        {
            _schedulerWindow.Activate();
            return;
        }

        _schedulerWindow = new SchedulerWindow(
            _schedulerStore,
            SaveScheduler,
            RunScheduledTaskNowAsync,
            AddScheduledRunToMainChatAsync,
            path => _speech.PlayAudioFile(path))
        {
            Owner = this
        };
        _schedulerWindow.Closed += (_, _) => _schedulerWindow = null;
        _schedulerWindow.Show();
    }

    private void SaveScheduler()
    {
        _schedulerStore.Save();
        _schedulerWindow?.RefreshTasks();
    }

    private async Task RunDueScheduledTasksAsync()
    {
        if (_schedulerRunning)
            return;

        if (SendBtn?.IsEnabled != true)
            return;

        var now = DateTime.Now;
        var due = _schedulerStore.Tasks
            .Where(t => t.IsEnabled && t.NextRunAt <= now && !string.IsNullOrWhiteSpace(t.Prompt))
            .OrderBy(t => t.NextRunAt)
            .ToList();

        if (due.Count == 0)
            return;

        _schedulerRunning = true;
        try
        {
            foreach (var task in due)
                await ExecuteAndStoreScheduledTaskAsync(task, CancellationToken.None);
        }
        finally
        {
            _schedulerRunning = false;
        }
    }

    private async Task RunScheduledTaskNowAsync(ScheduledPromptTask task)
    {
        if (_schedulerRunning)
            throw new InvalidOperationException("A scheduled task is already running.");
        if (SendBtn?.IsEnabled != true)
            throw new InvalidOperationException("The app is busy. Try again after the current response finishes.");

        _schedulerRunning = true;
        try
        {
            await ExecuteAndStoreScheduledTaskAsync(task, CancellationToken.None, advanceSchedule: false);
        }
        finally
        {
            _schedulerRunning = false;
        }
    }

    private async Task ExecuteAndStoreScheduledTaskAsync(
        ScheduledPromptTask task,
        CancellationToken ct,
        bool advanceSchedule = true)
    {
        var run = new ScheduledPromptRun
        {
            StartedAt = DateTime.Now,
            Prompt = task.Prompt
        };

        task.LastStatus = "Running";
        _schedulerStore.Save();
        _schedulerWindow?.RefreshTasks();

        try
        {
            var result = await ExecuteScheduledPromptAsync(task.Prompt, ct);
            run.ResponseText = result.Text;
            run.AudioPath = result.AudioPath;
            task.LastStatus = "Completed";
            if (task.ShowInMainChat)
                await AddScheduledRunToMainChatAsync(task, run);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Scheduled task \"{task.Name}\" failed", ex);
            run.Error = ex.Message;
            task.LastStatus = $"Error: {ex.Message}";
            if (task.ShowInMainChat)
                await AddScheduledRunToMainChatAsync(task, run);
        }
        finally
        {
            run.CompletedAt = DateTime.Now;
            _schedulerStore.AddRun(task, run);
            if (advanceSchedule)
                SchedulerStore.AdvanceAfterRun(task, DateTime.Now);
            _schedulerStore.Save();
            _schedulerWindow?.RefreshTasks();
        }
    }

    private async Task<ScheduledPromptResult> ExecuteScheduledPromptAsync(string prompt, CancellationToken ct)
    {
        string model = "";
        string systemPrompt = "";
        double temperature = 0.7;
        int maxTokens = 2048;
        bool makeAudio = false;
        bool webSearchEnabled = false;
        string tavilyApiKey = "";

        await Dispatcher.InvokeAsync(() =>
        {
            model = ModelCombo.Text;
            systemPrompt = GetEffectiveSystemPrompt(prompt);
            temperature = TempSlider.Value;
            maxTokens = GetMaxTokensForRequest(prompt, model);
            makeAudio = TtsToggle.IsChecked == true;
            webSearchEnabled = WebSearchToggle.IsChecked == true;
            tavilyApiKey = TavilyApiKeyBox.Password.Trim();
        });

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Select a model before running scheduled prompts.");

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content = prompt
            }
        };

        var modelPrompt = prompt;
        if (webSearchEnabled && ShouldTriggerWebSearch(prompt) && !string.IsNullOrWhiteSpace(tavilyApiKey))
        {
            _tavily.ApiKey = tavilyApiKey;
            var webSearchQuery = RemoveWebSearchTriggerPhrases(prompt);
            var searchContext = await _tavily.SearchAndBuildContextAsync(webSearchQuery, maxResults: 5, ct: ct);
            if (!string.IsNullOrWhiteSpace(searchContext))
            {
                modelPrompt =
                    $"{webSearchQuery}\n\nCurrent web search context:\n{searchContext}\n\nAnswer the user's scheduled prompt using the current web search context above. If the prompt asks for latest or current information, prioritize dated current sources.";
                messages.Insert(0, new ChatMessage
                {
                    Role = "system",
                    Content = "You have current web search context for this scheduled answer. Use it as the authoritative source for current facts, releases, versions, prices, dates, schedules, and news."
                });
                messages[^1] = new ChatMessage { Role = "user", Content = modelPrompt };
            }
        }

        var contextTokens = await GetContextTokensForRequestAsync(model, ct);
        TrimMessagesToContextBudget(messages, systemPrompt, contextTokens, maxTokens);
        var response = await _ollama.ChatAsync(model, messages, systemPrompt, temperature, maxTokens, ct, contextTokens);
        response = await CompleteCodeArtifactIfNeededAsync(
            response,
            prompt,
            messages,
            systemPrompt,
            model,
            temperature,
            maxTokens,
            contextTokens,
            ct);

        var isCodeResponse = IsCodeOrScriptRequest(prompt) || ContainsFencedCodeBlock(response);
        var cleaned = CleanDisplayText(response, preserveCodeBlocks: isCodeResponse);
        var audioPath = "";
        if (makeAudio)
        {
            var speechText = CleanSpeechText(cleaned);
            if (!string.IsNullOrWhiteSpace(speechText))
                audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetSchedulerAudioDirectory()) ?? "";
        }

        return new ScheduledPromptResult(cleaned, audioPath);
    }

    private async Task AddScheduledRunToMainChatAsync(ScheduledPromptTask task, ScheduledPromptRun run)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            var promptLabel = $"[Scheduled] {task.Name}\n\n{run.Prompt}";
            AddUserMessage(promptLabel);
            _history.Add("user", promptLabel);

            var response = string.IsNullOrWhiteSpace(run.Error)
                ? run.ResponseText
                : $"Scheduled task error: {run.Error}";
            var assistantMessage = AddAssistantMessage(response);
            if (!string.IsNullOrWhiteSpace(run.AudioPath) && File.Exists(run.AudioPath))
                AddAudioButtons(assistantMessage, run.AudioPath);

            _history.Add("assistant", response);
        });
    }

    private static string GetSchedulerAudioDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            "scheduled-audio");
    }
}
