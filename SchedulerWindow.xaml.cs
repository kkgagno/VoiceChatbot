using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VoiceChatbot;

public partial class SchedulerWindow : Window
{
    private readonly SchedulerStore _store;
    private readonly Action _save;
    private readonly Func<ScheduledPromptTask, Task> _runNowAsync;
    private readonly Action<string> _playAudio;
    private readonly ObservableCollection<ScheduledPromptTask> _tasks;
    private bool _loading;

    public SchedulerWindow(
        SchedulerStore store,
        Action save,
        Func<ScheduledPromptTask, Task> runNowAsync,
        Action<string> playAudio)
    {
        InitializeComponent();
        _store = store;
        _save = save;
        _runNowAsync = runNowAsync;
        _playAudio = playAudio;
        _tasks = new ObservableCollection<ScheduledPromptTask>(_store.Tasks);

        RecurrenceBox.ItemsSource = Enum.GetValues<ScheduledTaskRecurrence>();
        TaskList.ItemsSource = _tasks;
        if (_tasks.Count > 0)
            TaskList.SelectedIndex = 0;
        else
            CreateTask(select: true);
    }

    public void RefreshTasks()
    {
        var selectedId = (TaskList.SelectedItem as ScheduledPromptTask)?.Id;
        _tasks.Clear();
        foreach (var task in _store.Tasks)
            _tasks.Add(task);

        TaskList.SelectedItem = _tasks.FirstOrDefault(t => t.Id == selectedId) ?? _tasks.FirstOrDefault();
        LoadSelectedTask();
    }

    private void NewTask_Click(object sender, RoutedEventArgs e) => CreateTask(select: true);

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not ScheduledPromptTask task)
            return;

        _store.Tasks.Remove(task);
        _tasks.Remove(task);
        _save();
        TaskList.SelectedItem = _tasks.FirstOrDefault();
        LoadSelectedTask();
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelectedTask();

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelectedRun();

    private void SaveTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not ScheduledPromptTask task)
            return;

        if (!TryApplyForm(task))
            return;

        _save();
        TaskList.Items.Refresh();
        StatusText.Text = $"Saved. Next run: {task.NextRunAt:g}";
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not ScheduledPromptTask task)
            return;

        if (!TryApplyForm(task))
            return;

        _save();
        SetBusy(true, "Running scheduled prompt now...");
        try
        {
            await _runNowAsync(task);
            RefreshTasks();
            StatusText.Text = "Run complete.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Run failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PlayAudio_Click(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is not ScheduledPromptRun run || string.IsNullOrWhiteSpace(run.AudioPath))
            return;

        if (!File.Exists(run.AudioPath))
        {
            StatusText.Text = "Audio file no longer exists.";
            return;
        }

        _playAudio(run.AudioPath);
    }

    private void CreateTask(bool select)
    {
        var task = new ScheduledPromptTask
        {
            Name = "Scheduled prompt",
            NextRunAt = DateTime.Now.AddHours(1),
            KeepRuns = 5,
            Recurrence = ScheduledTaskRecurrence.Once
        };
        _store.Tasks.Add(task);
        _tasks.Add(task);
        _save();
        if (select)
            TaskList.SelectedItem = task;
    }

    private void LoadSelectedTask()
    {
        if (TaskList.SelectedItem is not ScheduledPromptTask task)
        {
            _loading = true;
            NameBox.Text = "";
            PromptBox.Text = "";
            EnabledBox.IsChecked = false;
            ShowInMainChatBox.IsChecked = false;
            RunDatePicker.SelectedDate = null;
            RunTimeBox.Text = "";
            KeepRunsBox.Text = "5";
            RunList.ItemsSource = null;
            ResponseBox.Text = "";
            PlayAudioBtn.IsEnabled = false;
            _loading = false;
            return;
        }

        _loading = true;
        NameBox.Text = task.Name;
        PromptBox.Text = task.Prompt;
        EnabledBox.IsChecked = task.IsEnabled;
        ShowInMainChatBox.IsChecked = task.ShowInMainChat;
        RunDatePicker.SelectedDate = task.NextRunAt.Date;
        RunTimeBox.Text = task.NextRunAt.ToString("h:mm tt", CultureInfo.CurrentCulture);
        RecurrenceBox.SelectedItem = task.Recurrence;
        KeepRunsBox.Text = task.KeepRuns.ToString(CultureInfo.InvariantCulture);
        RunList.ItemsSource = task.Runs;
        RunList.Items.Refresh();
        RunList.SelectedIndex = task.Runs.Count > 0 ? 0 : -1;
        StatusText.Text = task.LastStatus;
        _loading = false;
        LoadSelectedRun();
    }

    private void LoadSelectedRun()
    {
        if (_loading)
            return;

        if (RunList.SelectedItem is not ScheduledPromptRun run)
        {
            ResponseBox.Text = "";
            PlayAudioBtn.IsEnabled = false;
            return;
        }

        ResponseBox.Text = string.IsNullOrWhiteSpace(run.Error)
            ? run.ResponseText
            : $"Error: {run.Error}";
        PlayAudioBtn.IsEnabled = !string.IsNullOrWhiteSpace(run.AudioPath) && File.Exists(run.AudioPath);
    }

    private bool TryApplyForm(ScheduledPromptTask task)
    {
        var name = NameBox.Text.Trim();
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "Prompt is required.";
            return false;
        }

        if (RunDatePicker.SelectedDate is not DateTime date)
        {
            StatusText.Text = "Pick a run date.";
            return false;
        }

        if (!DateTime.TryParse($"{date:d} {RunTimeBox.Text.Trim()}", CultureInfo.CurrentCulture, DateTimeStyles.None, out var nextRun))
        {
            StatusText.Text = "Enter a valid time, for example 9:00 AM.";
            return false;
        }

        if (!int.TryParse(KeepRunsBox.Text.Trim(), out var keepRuns))
            keepRuns = 5;

        task.Name = string.IsNullOrWhiteSpace(name) ? "Scheduled prompt" : name;
        task.Prompt = prompt;
        task.IsEnabled = EnabledBox.IsChecked == true;
        task.ShowInMainChat = ShowInMainChatBox.IsChecked == true;
        task.NextRunAt = nextRun;
        task.Recurrence = RecurrenceBox.SelectedItem is ScheduledTaskRecurrence recurrence
            ? recurrence
            : ScheduledTaskRecurrence.Once;
        task.KeepRuns = Math.Clamp(keepRuns, 1, 100);
        SchedulerStore.PruneRuns(task);
        return true;
    }

    private void SetBusy(bool busy, string status = "")
    {
        TaskList.IsEnabled = !busy;
        NameBox.IsEnabled = !busy;
        PromptBox.IsEnabled = !busy;
        RunDatePicker.IsEnabled = !busy;
        RunTimeBox.IsEnabled = !busy;
        RecurrenceBox.IsEnabled = !busy;
        KeepRunsBox.IsEnabled = !busy;
        EnabledBox.IsEnabled = !busy;
        ShowInMainChatBox.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
            StatusText.Text = status;
    }
}
