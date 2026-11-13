using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.App.ViewModels;
using MyTodo.Core.Models;
using Windows.System;

namespace MyTodo.App.Views;

/// <summary>Today: the day's tasks on the left, the day's timeline on the right; drag tasks into slots to plan the day.</summary>
public sealed partial class TodayPage : Page, INewTaskTarget, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private readonly UndoHelper _undo;

    public TodayPage()
    {
        InitializeComponent();
        _undo = new UndoHelper(UndoBar);
        Schedule.ShowHeader = false;
        Schedule.WorkStartMinutes = _s.Settings.WorkStartMinutes;
        Schedule.WorkEndMinutes = _s.Settings.WorkEndMinutes;
        Schedule.TaskDropped += (id, start) => CalendarActions.CreateBlockFromTask(_s, id, start);
        Schedule.EventMoved += async (occ, start, end) => await CalendarActions.MoveAsync(XamlRoot, _s, occ, start, end);
        Schedule.EventInvoked += async occ => await CalendarActions.EditAsync(XamlRoot, _s, occ, null);
        Schedule.EventDeleteRequested += async occ => await CalendarActions.DeleteAsync(XamlRoot, _s, occ);
        Schedule.CreateRequested += async start => await CalendarActions.EditAsync(XamlRoot, _s, null, start);
        Detail.CloseRequested += () => Split.IsPaneOpen = false;
        Detail.TaskChanged += Refresh;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _s.Store.Changed += OnStoreChanged;
        _s.Timer.Changed += UpdateTimerBar;
        _s.Timer.Tick += TimerTick;
        Refresh();
        UpdateTimerBar();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _s.Store.Changed -= OnStoreChanged;
        _s.Timer.Changed -= UpdateTimerBar;
        _s.Timer.Tick -= TimerTick;
    }

    private void OnStoreChanged(string table)
    {
        if (table is "tasks" or "events" or "attachments" or "time_entries") DispatcherQueue.TryEnqueue(Refresh);
    }

    public void OnThemeChanged() => Refresh();
    public void FocusNewTask() => AddBox.Focus(FocusState.Keyboard);

    private void Refresh()
    {
        var today = Ui.Today;
        DateText.Text = DateTime.Now.ToString("dddd, d MMMM");
        var all = _s.Store.GetAllTasks();
        var top = all.Where(t => t.ParentId == null).ToList();
        DateOnly? LocalDue(TodoTask t) => t.DueUtc is DateTime d ? DateOnly.FromDateTime(Ui.Local(d)) : null;

        var overdue = top.Where(t => !t.IsCompleted && LocalDue(t) is DateOnly d && d < today).OrderBy(t => t.DueUtc).ToList();
        var dueToday = top.Where(t => !t.IsCompleted && LocalDue(t) == today)
            .OrderBy(t => t.DueHasTime ? 0 : 1).ThenBy(t => t.DueUtc).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).ToList();
        var doneToday = top.Where(t => t.IsCompleted && t.CompletedUtc is DateTime c && DateOnly.FromDateTime(Ui.Local(c)) == today).ToList();

        var builder = new TaskRowBuilder(_s.Store, all, _s.Timer.Running?.TaskId, Toggle);
        var rows = new List<TaskRow>();
        if (overdue.Count > 0) { rows.Add(TaskRow.Header($"Overdue · {overdue.Count}")); rows.AddRange(overdue.Select(t => builder.Row(t))); }
        if (overdue.Count > 0 && dueToday.Count > 0) rows.Add(TaskRow.Header("Today"));
        rows.AddRange(dueToday.Select(t => builder.Row(t)));
        if (doneToday.Count > 0) { rows.Add(TaskRow.Header($"Completed · {doneToday.Count}")); rows.AddRange(doneToday.Select(t => builder.Row(t))); }
        TaskList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = overdue.Count + dueToday.Count + doneToday.Count;
        ProgressText.Text = $"{doneToday.Count} of {total} done";
        Progress.Value = total == 0 ? 0 : doneToday.Count / (double)total;

        var from = Ui.ToUtc(today.ToDateTime(TimeOnly.MinValue));
        Schedule.Show(today, 1, _s.Store.GetOccurrences(from, from.AddDays(1)));
    }

    private void Toggle(TodoTask t)
    {
        if (t.IsCompleted) { _s.Store.ReopenTask(t); return; }
        var done = _s.Store.CompleteTask(t);
        _undo.Show($"\u201C{t.Title}\u201D completed.", () =>
        {
            if (done.Id == t.Id) _s.Store.ReopenTask(done);
            else
            {
                _s.Store.DeleteTask(done.Id);
                if (_s.Store.GetTask(t.Id) is TodoTask original && done.DueUtc != null) { original.DueUtc = done.DueUtc; _s.Store.SaveTask(original); }
            }
        });
    }

    private void AddBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(AddBox.Text)) return;
        TaskCreator.CreateFromText(_s, AddBox.Text, defaultDate: Ui.Today);
        AddBox.Text = "";
        e.Handled = true;
    }

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TaskRow { Task: TodoTask t }) return;
        Detail.Load(t);
        Split.IsPaneOpen = true;
    }

    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is TaskRow { Task: TodoTask t }) e.Data.SetText("task:" + t.Id);
        else e.Cancel = true;
    }

    private void UpdateTimerBar()
    {
        var running = _s.Timer.Running != null;
        TimerBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TimerText.Text = running ? $"Timer running on {_s.Timer.RunningTitle}" : "";
        TimerElapsed.Text = _s.Timer.ElapsedText;
    }

    private void TimerTick() => TimerElapsed.Text = _s.Timer.ElapsedText;
    private void StopTimer_Click(object sender, RoutedEventArgs e) => _s.Timer.Stop();
    private void OpenWeek_Click(object sender, RoutedEventArgs e) => App.Instance!.MainWindow!.Navigate(typeof(CalendarPage), null);
}

/// <summary>"Task completed · Undo" for 10 seconds (TSK-10).</summary>
public sealed class UndoHelper
{
    private readonly InfoBar _bar;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private Action? _undo;

    public UndoHelper(InfoBar bar)
    {
        _bar = bar;
        _timer = App.Services.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { _bar.IsOpen = false; _undo = null; };
    }

    public void Show(string message, Action undo)
    {
        _undo = undo;
        var button = new Button { Content = "Undo" };
        button.Click += (_, _) => { _undo?.Invoke(); _undo = null; _bar.IsOpen = false; };
        _bar.Message = message;
        _bar.ActionButton = button;
        _bar.IsOpen = true;
        _timer.Stop();
        _timer.Start();
    }
}
