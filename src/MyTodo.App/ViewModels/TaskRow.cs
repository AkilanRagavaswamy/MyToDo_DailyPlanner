using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyTodo.App.Helpers;
using MyTodo.Core.Insights;
using MyTodo.Core.Models;
using MyTodo.Core.Recurrence;
using MyTodo.Data;

namespace MyTodo.App.ViewModels;

/// <summary>One row in a task list, or a section header ("Today", "This week").</summary>
public sealed class TaskRow
{
    public TodoTask? Task { get; init; }
    public bool IsHeader { get; init; }
    public string HeaderText { get; init; } = "";
    public string Title => Task?.Title ?? "";
    public string Meta { get; init; } = "";
    public string PriorityText => Task is { Priority: > 0 } t ? "P" + t.Priority : "";
    public Brush PriorityBrush => Ui.PriorityBrush(Task?.Priority ?? 0);
    public Brush RingBrush => Task?.IsCompleted == true ? Ui.Brush("BrandAccentFillBrush") : Ui.PriorityBrush(Task?.Priority ?? 0);
    public Brush RingFill => Task?.IsCompleted == true ? Ui.Brush("BrandAccentFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public Brush TitleBrush => Task?.IsCompleted == true ? Ui.Brush("Text2Brush") : Ui.Brush("TextBrush");
    public Brush MetaBrush => IsOverdue ? Ui.Brush("DangerBrush") : Ui.Brush("Text2Brush");
    public Windows.UI.Text.TextDecorations TitleDecorations => Task?.IsCompleted == true ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;
    public Visibility CheckVisibility => Task?.IsCompleted == true ? Visibility.Visible : Visibility.Collapsed;
    public bool IsOverdue { get; init; }
    public bool IsTimerRunning { get; init; }
    public Visibility TimerVisibility => IsTimerRunning ? Visibility.Visible : Visibility.Collapsed;
    public string CompleteLabel => Task?.IsCompleted == true ? $"Reopen {Title}" : $"Complete {Title}";
    public System.Windows.Input.ICommand? ToggleCommand { get; init; }

    public static TaskRow Header(string text) => new() { IsHeader = true, HeaderText = text };
}

/// <summary>Builds rows with their meta line in one pass (one query per related table).</summary>
public sealed class TaskRowBuilder
{
    private readonly Dictionary<Guid, string> _lists;
    private readonly ILookup<Guid, TodoTask> _subtasks;
    private readonly Dictionary<Guid, int> _files;
    private readonly Dictionary<Guid, int> _minutes;
    private readonly Guid? _runningTask;
    private readonly Action<TodoTask>? _onToggle;

    public TaskRowBuilder(DataStore store, IReadOnlyCollection<TodoTask> all, Guid? runningTask, Action<TodoTask>? onToggle = null)
    {
        _onToggle = onToggle;
        _lists = store.GetLists().ToDictionary(l => l.Id, l => l.Name);
        _subtasks = all.Where(t => t.ParentId != null).ToLookup(t => t.ParentId!.Value);
        _files = store.GetAllAttachments().GroupBy(a => a.TaskId).ToDictionary(g => g.Key, g => g.Count());
        _minutes = store.GetAllTimeEntries().Where(e => !e.IsRunning).GroupBy(e => e.TaskId).ToDictionary(g => g.Key, g => g.Sum(e => e.Minutes));
        _runningTask = runningTask;
    }

    public TaskRow Row(TodoTask t, bool showList = true)
    {
        var parts = new List<string>();
        if (showList && _lists.TryGetValue(t.ListId, out var list)) parts.Add(list);
        if (t.DueUtc != null) parts.Add(Ui.DueText(t));
        if (t.Rrule != null)
        {
            try { parts.Add(RecurrenceRule.Parse(t.Rrule).Describe()); } catch (FormatException) { }
        }
        var subs = _subtasks[t.Id].ToList();
        if (subs.Count > 0) parts.Add($"Subtasks {subs.Count(s => s.IsCompleted)} of {subs.Count}");
        if (_files.TryGetValue(t.Id, out var f)) parts.Add(f == 1 ? "1 attachment" : $"{f} attachments");
        if (_minutes.TryGetValue(t.Id, out var m) && m > 0) parts.Add(InsightsCalculator.FormatMinutes(m) + " spent");
        if (t.Tags.Count > 0) parts.Add(string.Join(" ", t.Tags.Select(x => "#" + x)));
        var overdue = !t.IsCompleted && t.DueUtc is DateTime due &&
                      (t.DueHasTime ? due < DateTime.UtcNow : DateOnly.FromDateTime(Ui.Local(due)) < Ui.Today);
        return new TaskRow
        {
            Task = t, Meta = string.Join("  ·  ", parts), IsOverdue = overdue, IsTimerRunning = _runningTask == t.Id,
            ToggleCommand = _onToggle == null ? null : new CommunityToolkit.Mvvm.Input.RelayCommand(() => _onToggle(t))
        };
    }
}

public sealed class TaskRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? TaskTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is TaskRow { IsHeader: true } ? HeaderTemplate : TaskTemplate;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
