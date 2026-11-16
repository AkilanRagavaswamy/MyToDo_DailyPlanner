using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.App.ViewModels;
using MyTodo.Core.Models;

namespace MyTodo.App.Views;

/// <summary>One saved filter in the Report rail.</summary>
public sealed class ReportFilter
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required string Subtitle { get; init; }
    public int Count { get; set; }
    public string CountText => Count.ToString();
    public Visibility CountVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The name a screen reader reads for the rail item.</summary>
    public override string ToString() => Count > 0 ? $"{Name}, {Count}" : Name;
}

/// <summary>
/// Report: every cross-list view in one place — Upcoming, Overdue, In progress, On hold, No date,
/// Completed and Archive. These used to hang off the Lists navigation group; Lists is now a list
/// master, so the filters moved here where they can carry counts and sit side by side.
/// </summary>
public sealed partial class ReportPage : Page, INewTaskTarget, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private readonly UndoHelper _undo;
    private readonly List<ReportFilter> _filters =
    [
        new() { Key = "upcoming", Name = "Upcoming", Glyph = "", Subtitle = "Everything due in the next seven days, by day." },
        new() { Key = "overdue", Name = "Overdue", Glyph = "", Subtitle = "Past their due date and not finished." },
        new() { Key = "inprogress", Name = "In progress", Glyph = "", Subtitle = "Started, with the clock running." },
        new() { Key = "hold", Name = "On hold", Glyph = "", Subtitle = "Paused part-way through." },
        new() { Key = "nodate", Name = "No date", Glyph = "", Subtitle = "Open tasks nobody has scheduled yet." },
        new() { Key = "completed", Name = "Completed", Glyph = "", Subtitle = "Finished tasks, newest first." },
        new() { Key = "archive", Name = "Archive", Glyph = "", Subtitle = "Completed tasks that have aged out of the lists." }
    ];

    private string _view = "upcoming";
    private string? _tag;

    public ReportPage()
    {
        InitializeComponent();
        _undo = new UndoHelper(UndoBar);
        Detail.CloseRequested += CloseDetail;
        Detail.TaskChanged += Refresh;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string p && p.StartsWith("view:") && _filters.Any(f => f.Key == p[5..])) _view = p[5..];
        _s.Store.Changed += OnStoreChanged;
        BuildTagFilter();
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _s.Store.Changed -= OnStoreChanged;

    private void OnStoreChanged(string table)
    {
        if (table is "tasks" or "attachments" or "time_entries" or "lists") DispatcherQueue.TryEnqueue(Refresh);
        if (table == "tags") DispatcherQueue.TryEnqueue(BuildTagFilter);
    }

    public void OnThemeChanged() => Refresh();
    public void FocusNewTask() => App.Instance!.ShowQuickAdd();

    /// <summary>Tags come from the master, not from what happens to be in use, so a tag can be filtered
    /// on before any task carries it.</summary>
    private void BuildTagFilter()
    {
        TagMenu.Items.Clear();
        void Add(string label, string? tag)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "ReportTag", IsChecked = _tag == tag };
            item.Click += (_, _) => { _tag = tag; TagLabel.Text = label; Refresh(); };
            TagMenu.Items.Add(item);
        }
        Add("All tags", null);
        foreach (var t in _s.Store.GetTags()) Add("#" + t.Name, t.Name);
        TagLabel.Text = _tag == null ? "All tags" : "#" + _tag;
    }

    /// <summary>Rebuilds the heading menu. Counts change with the data, so this runs on every refresh.</summary>
    private void BuildFilterMenu()
    {
        FilterMenu.Items.Clear();
        foreach (var f in _filters)
        {
            var key = f.Key;
            var item = new RadioMenuFlyoutItem
            {
                Text = f.Count > 0 ? $"{f.Name}  ·  {f.Count}" : f.Name,
                GroupName = "ReportView",
                IsChecked = key == _view,
                Icon = new FontIcon { Glyph = f.Glyph }
            };
            item.Click += (_, _) => { if (_view == key) return; _view = key; Refresh(); };
            FilterMenu.Items.Add(item);
        }
    }

    private void Refresh()
    {
        // The archive is excluded from GetAllTasks by default, so it has to be asked for explicitly.
        var all = _s.Store.GetAllTasks(includeArchived: true);
        var builder = new TaskRowBuilder(_s.Store, all, _s.Timer.Running?.TaskId, Toggle, Stages);
        var tag = _tag;
        var top = all.Where(t => t.ParentId == null && (tag == null || t.Tags.Contains(tag))).ToList();
        var today = Ui.Today;
        DateOnly? Due(TodoTask t) => t.DueUtc is DateTime d ? DateOnly.FromDateTime(Ui.Local(d)) : null;

        var live = top.Where(t => !t.IsArchived).ToList();
        var open = live.Where(t => t.IsActive).ToList();

        List<TodoTask> For(string key) => key switch
        {
            "upcoming" => open.Where(t => Due(t) is DateOnly d && d >= today && d <= today.AddDays(7)).ToList(),
            "overdue" => open.Where(t => Due(t) is DateOnly d && d < today).ToList(),
            "inprogress" => open.Where(t => t.IsInProgress).ToList(),
            "hold" => open.Where(t => t.IsOnHold).ToList(),
            "nodate" => open.Where(t => t.DueUtc == null).ToList(),
            "completed" => live.Where(t => t.IsCompleted && t.CompletedUtc != null).ToList(),
            _ => top.Where(t => t.IsArchived).ToList()
        };

        foreach (var f in _filters) f.Count = For(f.Key).Count;
        BuildFilterMenu();

        var current = _filters.First(f => f.Key == _view);
        TitleRun.Text = current.Name;
        SubtitleText.Text = current.Subtitle;

        var items = For(_view);
        var rows = new List<TaskRow>();
        void Section(string name, IEnumerable<TodoTask> group)
        {
            var list = group.ToList();
            if (list.Count == 0) return;
            rows.Add(TaskRow.Header($"{name} · {list.Count}"));
            rows.AddRange(list.Select(t => builder.Row(t)));
        }

        switch (_view)
        {
            case "upcoming":
                for (var i = 0; i <= 7; i++)
                {
                    var d = today.AddDays(i);
                    Section(Ui.FriendlyDate(d.ToDateTime(TimeOnly.MinValue)),
                        items.Where(t => Due(t) == d).OrderBy(t => t.DueUtc).ThenBy(t => t.Priority == 0 ? 9 : t.Priority));
                }
                break;
            case "completed":
                foreach (var g in items.GroupBy(t => DateOnly.FromDateTime(Ui.Local(t.CompletedUtc!.Value))).OrderByDescending(g => g.Key))
                    Section(Ui.FriendlyDate(g.Key.ToDateTime(TimeOnly.MinValue)), g.OrderByDescending(t => t.CompletedUtc));
                break;
            case "archive":
                rows.AddRange(items.OrderByDescending(t => t.CompletedUtc).Select(t => builder.Row(t)));
                break;
            default:
                rows.AddRange(items
                    .OrderBy(t => t.DueUtc ?? DateTime.MaxValue)
                    .ThenBy(t => t.Priority == 0 ? 9 : t.Priority)
                    .Select(t => builder.Row(t)));
                break;
        }

        TaskList.ItemsSource = rows;
        EmptyText.Text = _view switch
        {
            "archive" => $"Completed tasks move here {_s.Settings.ArchiveAfterDays} days after completion.",
            "overdue" => "Nothing is overdue.",
            _ => $"Nothing here. Press {_s.Settings.QuickAddHotkey} from any app to add a task."
        };
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TaskStageHandlers Stages => new()
    {
        Start = t => _s.Store.StartTask(t),
        Hold = t => _s.Store.HoldTask(t),
        Complete = Toggle,
        Reopen = t => _s.Store.ReopenTask(t),
        Delete = DeleteWithUndo
    };

    private void Toggle(TodoTask t)
    {
        if (t.IsCompleted) { _s.Store.ReopenTask(t); return; }
        var done = _s.Store.CompleteTask(t);
        _undo.Show($"“{t.Title}” completed.", () =>
        {
            if (done.Id == t.Id) _s.Store.ReopenTask(done);
            else { _s.Store.DeleteTask(done.Id); if (_s.Store.GetTask(t.Id) is TodoTask orig) { orig.DueUtc = done.DueUtc; _s.Store.SaveTask(orig); } }
        });
    }

    private void DeleteWithUndo(TodoTask t)
    {
        _s.Store.DeleteTask(t.Id);
        if (Detail.Task?.Id == t.Id) CloseDetail();
        _undo.Show($"“{t.Title}” deleted.", () => _s.Store.RestoreTask(t.Id));
    }

    private TodoTask? Selected => (TaskList.SelectedItem as TaskRow)?.Task;

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskRow { Task: TodoTask t }) OpenDetail(t);
    }

    private void OpenDetail(TodoTask t)
    {
        Detail.Load(t);
        DetailSplit.IsPaneOpen = true;
    }

    private void CloseDetail() => DetailSplit.IsPaneOpen = false;

    private void Detail_PaneClosed(SplitView sender, object args) => Refresh();

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!DetailSplit.IsPaneOpen) return;
        CloseDetail();
        args.Handled = true;
    }

    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is TaskRow { Task: TodoTask t }) e.Data.SetText("task:" + t.Id);
        else e.Cancel = true;
    }

    private void CompleteKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Selected is TodoTask t) Toggle(t);
        args.Handled = true;
    }

    private void DeleteKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Selected is TodoTask t) DeleteWithUndo(t);
        args.Handled = true;
    }

    private void TaskList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TaskRow { Task: TodoTask t }) return;
        TaskMenu.Build(_s, t, Toggle, DeleteWithUndo).ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
    }
}
