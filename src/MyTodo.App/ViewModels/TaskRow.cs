using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyTodo.App.Helpers;
using MyTodo.Core.Insights;
using MyTodo.Core.Models;
using MyTodo.Core.Recurrence;
using MyTodo.Data;

namespace MyTodo.App.ViewModels;

/// <summary>Which transition an action runs. Callers switch on this rather than on the label, so
/// renaming a button cannot silently change how it is coloured or where it is placed.</summary>
public enum TaskActionKind { Start, Hold, Complete, Reopen, Delete, Restore, Edit }

/// <summary>One stage button on a task row or a board card ("Start", "Hold", "Complete", …).</summary>
public sealed class TaskAction
{
    public required TaskActionKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public required ICommand Command { get; init; }
    public bool IsDanger => Kind == TaskActionKind.Delete;
    public Brush Foreground => IsDanger ? Ui.Brush("DangerBrush") : Ui.Brush("TextBrush");
}

/// <summary>
/// What a stage button does. Pages supply the handlers so undo bars, dialogs and navigation stay in the
/// page; a handler left null hides its button (the archive view offers no Start, for instance).
/// </summary>
public sealed class TaskStageHandlers
{
    public Action<TodoTask>? Start { get; init; }
    public Action<TodoTask>? Hold { get; init; }
    public Action<TodoTask>? Complete { get; init; }
    public Action<TodoTask>? Reopen { get; init; }
    public Action<TodoTask>? Delete { get; init; }
    public Action<TodoTask>? Restore { get; init; }
}

/// <summary>One row in a task list or a card on the board, or a section header ("Today", "This week").</summary>
public sealed class TaskRow
{
    public TodoTask? Task { get; init; }
    public bool IsHeader { get; init; }
    public string HeaderText { get; init; } = "";
    public string Title => Task?.Title ?? "";
    public string Meta { get; init; } = "";
    public Visibility MetaVisibility => Meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PriorityText => Task is { Priority: > 0 } t ? "P" + t.Priority : "";
    public Brush PriorityBrush => Ui.PriorityBrush(Task?.Priority ?? 0);
    public Brush RingBrush => Task?.IsCompleted == true ? Ui.Brush("BrandAccentFillBrush") : Ui.PriorityBrush(Task?.Priority ?? 0);
    public Brush RingFill => Task?.IsCompleted == true ? Ui.Brush("BrandAccentFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public Brush TitleBrush => Task?.IsCompleted == true || IsDeleted ? Ui.Brush("Text2Brush") : Ui.Brush("TextBrush");
    public Brush MetaBrush => IsOverdue ? Ui.Brush("DangerBrush") : Ui.Brush("Text2Brush");
    public Windows.UI.Text.TextDecorations TitleDecorations =>
        Task?.IsCompleted == true || IsDeleted ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;
    public Visibility CheckVisibility => Task?.IsCompleted == true ? Visibility.Visible : Visibility.Collapsed;
    public bool IsOverdue { get; init; }
    public bool IsTimerRunning { get; init; }
    public Visibility TimerVisibility => IsTimerRunning ? Visibility.Visible : Visibility.Collapsed;
    public string CompleteLabel => Task?.IsCompleted == true ? $"Reopen {Title}" : $"Complete {Title}";
    public string CompleteText => Task?.IsCompleted == true ? "Reopen" : "Complete";
    public string CompleteGlyph => Task?.IsCompleted == true ? "" : "";
    public Brush CompleteBrush => Task?.IsCompleted == true ? Ui.Brush("Text2Brush") : Ui.Brush("BrandAccentBrush");
    public ICommand? ToggleCommand { get; init; }

    /// <summary>A deleted task is a tombstone the board still shows; nothing else lists it.</summary>
    public bool IsDeleted => Task?.Deleted == true;

    public string StageText => IsDeleted ? "Deleted" : Task?.StageText ?? "";

    /// <summary>Open needs no badge: it is the resting state, and a badge on every row is noise.</summary>
    public Visibility StageVisibility =>
        Task != null && (IsDeleted || Task.Status != TaskState.Open) ? Visibility.Visible : Visibility.Collapsed;

    public Brush StageTextBrush => StagePair.text;
    public Brush StageFillBrush => StagePair.fill;

    private (Brush fill, Brush text) StagePair => IsDeleted
        ? (Ui.Brush("DangerSoftBrush"), Ui.Brush("DangerBrush"))
        : Task?.Status switch
        {
            TaskState.InProgress => (Ui.Brush("MeetingFillBrush"), Ui.Brush("MeetingTextBrush")),
            TaskState.Hold => (Ui.Brush("AmberSoftBrush"), Ui.Brush("AmberBrush")),
            TaskState.Completed => (Ui.Brush("AccentSoftBrush"), Ui.Brush("BrandAccentBrush")),
            _ => (Ui.Brush("Surface2Brush"), Ui.Brush("Text2Brush"))
        };

    public IReadOnlyList<TaskAction> Actions { get; init; } = Array.Empty<TaskAction>();
    public Visibility ActionsVisibility => Actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static TaskRow Header(string text) => new() { IsHeader = true, HeaderText = text };

    /// <summary>What a screen reader announces for the row: the container has no name of its own and
    /// falls back to this.</summary>
    public override string ToString() => IsHeader
        ? HeaderText
        : string.Join(", ", new[] { Title, StageText, Meta }.Where(x => x.Length > 0));
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
    private readonly TaskStageHandlers? _stages;

    public TaskRowBuilder(DataStore store, IReadOnlyCollection<TodoTask> all, Guid? runningTask,
        Action<TodoTask>? onToggle = null, TaskStageHandlers? stages = null)
    {
        _onToggle = onToggle;
        _stages = stages;
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
        var overdue = t.IsActive && !t.Deleted && t.DueUtc is DateTime due &&
                      (t.DueHasTime ? due < DateTime.UtcNow : DateOnly.FromDateTime(Ui.Local(due)) < Ui.Today);
        return new TaskRow
        {
            Task = t, Meta = string.Join("  ·  ", parts), IsOverdue = overdue, IsTimerRunning = _runningTask == t.Id,
            ToggleCommand = _onToggle == null ? null : new RelayCommand(() => _onToggle(t)),
            Actions = ActionsFor(t)
        };
    }

    /// <summary>
    /// The buttons a task offers at its current stage: Open gives Start and Delete; In progress gives Hold
    /// and Complete; On hold gives Resume; Completed gives Reopen; a deleted tombstone gives Restore.
    /// </summary>
    private IReadOnlyList<TaskAction> ActionsFor(TodoTask t)
    {
        if (_stages == null) return Array.Empty<TaskAction>();
        var list = new List<TaskAction>();
        void Add(TaskActionKind kind, string label, string glyph, Action<TodoTask>? run)
        {
            if (run != null) list.Add(new TaskAction { Kind = kind, Label = label, Glyph = glyph, Command = new RelayCommand(() => run(t)) });
        }

        if (t.Deleted) { Add(TaskActionKind.Restore, "Restore", "", _stages.Restore); return list; }
        switch (t.Status)
        {
            case TaskState.Open:
                Add(TaskActionKind.Start, "Start", "", _stages.Start);
                Add(TaskActionKind.Delete, "Delete", "", _stages.Delete);
                break;
            case TaskState.InProgress:
                Add(TaskActionKind.Hold, "Hold", "", _stages.Hold);
                Add(TaskActionKind.Complete, "Complete", "", _stages.Complete);
                break;
            case TaskState.Hold:
                Add(TaskActionKind.Start, "Resume", "", _stages.Start);
                break;
            case TaskState.Completed:
                Add(TaskActionKind.Reopen, "Reopen", "", _stages.Reopen);
                break;
        }
        return list;
    }
}

/// <summary>Picks the header or task template. Two instances exist: one whose task template draws the
/// stage buttons, and one whose task template leaves them out because swipe supplies the actions.</summary>
public sealed class TaskRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? TaskTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is TaskRow { IsHeader: true } ? HeaderTemplate : TaskTemplate;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
