using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.App.ViewModels;
using MyTodo.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MyTodo.App.Views;

/// <summary>One column of the board. Rebuilt on every refresh, so it needs no change notification.</summary>
public sealed class BoardColumn
{
    public required string Name { get; init; }
    public required TaskState Status { get; init; }
    public required Brush AccentBrush { get; init; }
    public required string EmptyText { get; init; }
    public IReadOnlyList<TaskRow> Rows { get; init; } = Array.Empty<TaskRow>();
    public string CountText => Rows.Count.ToString();
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public override string ToString() => $"{Name}, {Rows.Count}";
}

/// <summary>
/// Where tasks are worked on, in either of two shapes the toggle switches between.
///
/// Board: Open, In progress, On hold and Completed side by side; dropping a card in a column runs the
/// same stage transition as that column's buttons. Deleted tasks stay in Completed, struck through and
/// badged, rather than vanishing.
///
/// List: one flat list, with the stage actions on a SwipeControl. Swipe is touch-first, so the same
/// actions are on the right-click menu for mouse and on the keyboard accelerators.
/// </summary>
public sealed partial class BoardPage : Page, INewTaskTarget, IThemeAware
{
    /// <summary>How many finished or deleted cards the Completed column keeps. Without a cap the column
    /// grows without bound and the board gets slower every week.</summary>
    private const int CompletedShown = 50;

    private const string ViewSetting = "board.view";

    private readonly AppServices _s = App.Services;
    private readonly UndoHelper _undo;
    private Guid? _listFilter;
    private string? _tagFilter;
    private bool _listView;
    private Guid? _openTask;

    public BoardPage()
    {
        InitializeComponent();
        _undo = new UndoHelper(UndoBar);
        Detail.CloseRequested += CloseDetail;
        Detail.TaskChanged += Refresh;
        _listView = _s.Store.GetValue(ViewSetting) == "list";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // A reminder passes a bare Guid; everything else passes "list:<id>" or "task:<id>".
        var target = e.Parameter switch
        {
            Guid g => g,
            string s when s.StartsWith("task:") && Guid.TryParse(s[5..], out var parsed) => parsed,
            _ => (Guid?)null
        };
        if (e.Parameter is string p && p.StartsWith("list:") && Guid.TryParse(p[5..], out var listId)) _listFilter = listId;
        if (e.Parameter is string tp && tp.StartsWith("tag:")) _tagFilter = Core.Models.Tag.Normalise(tp[4..]);
        if (target is Guid id && _s.Store.GetTask(id) is { } t)
        {
            // Arriving at a particular task: show the list it lives in, in the shape that can open it.
            _listFilter = t.ListId;
            _openTask = t.Id;
            _listView = true;
        }

        _s.Store.Changed += OnStoreChanged;
        BuildListFilter();
        BuildTagFilter();
        ApplyView();
        if (_openTask is Guid open && _s.Store.GetTask(open) is { } selected) OpenDetail(selected);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _s.Store.Changed -= OnStoreChanged;

    private void OnStoreChanged(string table)
    {
        if (table is "tasks" or "attachments" or "time_entries" or "lists") DispatcherQueue.TryEnqueue(Refresh);
        if (table == "tags") DispatcherQueue.TryEnqueue(BuildTagFilter);
    }

    public void OnThemeChanged() => Refresh();

    public void FocusNewTask()
    {
        if (_listView) AddBox.Focus(FocusState.Keyboard);
        else App.Instance!.ShowQuickAdd();
    }

    // ---------------- view toggle ----------------

    private void View_Click(object sender, RoutedEventArgs e)
    {
        _listView = ReferenceEquals(sender, ListToggle);
        _s.Store.SetValue(ViewSetting, _listView ? "list" : "board");
        ApplyView();
    }

    private void ApplyView()
    {
        // The pair is mutually exclusive: clicking the already-checked one must not leave both off.
        BoardToggle.IsChecked = !_listView;
        ListToggle.IsChecked = _listView;
        BoardHost.Visibility = _listView ? Visibility.Collapsed : Visibility.Visible;
        ListHost.Visibility = _listView ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Text = _listView
            ? "Swipe a row for its actions, or right-click it."
            : "Drag a card between columns to change its stage.";
        Refresh();
    }

    private void BuildListFilter()
    {
        ListMenu.Items.Clear();
        void Add(string label, Guid? id)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "BoardList", IsChecked = _listFilter == id };
            item.Click += (_, _) => { _listFilter = id; ListLabel.Text = label; Refresh(); };
            ListMenu.Items.Add(item);
        }
        Add("All lists", null);
        foreach (var l in _s.Store.GetLists()) Add(l.Name, l.Id);
        ListLabel.Text = _listFilter is Guid f && _s.Store.GetLists().FirstOrDefault(l => l.Id == f) is { } sel ? sel.Name : "All lists";
    }

    /// <summary>Tags come from the master, so one can be filtered on before any task carries it.</summary>
    private void BuildTagFilter()
    {
        TagMenu.Items.Clear();
        void Add(string label, string? tag)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "BoardTag", IsChecked = _tagFilter == tag };
            item.Click += (_, _) => { _tagFilter = tag; TagLabel.Text = label; Refresh(); };
            TagMenu.Items.Add(item);
        }
        Add("All tags", null);
        foreach (var t in _s.Store.GetTags()) Add("#" + t.Name, t.Name);
        TagLabel.Text = _tagFilter == null ? "All tags" : "#" + _tagFilter;
    }

    // ---------------- content ----------------

    private void Refresh()
    {
        var all = _s.Store.GetAllTasks();
        var builder = new TaskRowBuilder(_s.Store, all, _s.Timer.Running?.TaskId, CompleteOrReopen, Stages);
        bool InScope(TodoTask t) => t.ParentId == null
            && (_listFilter == null || t.ListId == _listFilter)
            && (_tagFilter == null || t.Tags.Contains(_tagFilter));
        var top = all.Where(InScope).ToList();

        IEnumerable<TodoTask> Stage(TaskState s) => top
            .Where(t => t.Status == s && !t.IsArchived)
            .OrderBy(t => t.DueUtc ?? DateTime.MaxValue)
            .ThenBy(t => t.Priority == 0 ? 9 : t.Priority);

        if (_listView) RefreshList(top, builder);
        else RefreshBoard(InScope, Stage, builder);
    }

    private void RefreshBoard(Func<TodoTask, bool> inScope, Func<TaskState, IEnumerable<TodoTask>> stage, TaskRowBuilder builder)
    {
        // Completed and deleted share a column: both are "off the board", and keeping a deleted card
        // visible is what makes a delete undoable by eye rather than only through the undo bar.
        var finished = stage(TaskState.Completed)
            .Select(t => (task: t, when: t.CompletedUtc ?? t.UpdatedUtc))
            .Concat(_s.Store.GetDeletedTasks(CompletedShown).Where(inScope).Select(t => (task: t, when: t.UpdatedUtc)))
            .OrderByDescending(x => x.when)
            .Take(CompletedShown)
            .Select(x => x.task);

        Col0.Content = Column("Open", TaskState.Open, "Text2Brush", "Nothing waiting. Add a task to get started.", stage(TaskState.Open), builder);
        Col1.Content = Column("In progress", TaskState.InProgress, "MeetingTextBrush", "Press Start on a card, or drop one here.", stage(TaskState.InProgress), builder);
        Col2.Content = Column("On hold", TaskState.Hold, "AmberBrush", "Paused work lands here, with its timer stopped.", stage(TaskState.Hold), builder);
        Col3.Content = Column("Completed", TaskState.Completed, "BrandAccentBrush", "Finished and deleted tasks collect here.", finished, builder);
    }

    private void RefreshList(List<TodoTask> top, TaskRowBuilder builder)
    {
        var rows = new List<TaskRow>();
        void Section(string name, IEnumerable<TodoTask> group)
        {
            var list = group.ToList();
            if (list.Count == 0) return;
            rows.Add(TaskRow.Header($"{name} · {list.Count}"));
            rows.AddRange(list.Select(t => builder.Row(t, showList: _listFilter == null)));
        }

        IEnumerable<TodoTask> Of(TaskState s) => top
            .Where(t => t.Status == s && !t.IsArchived)
            .OrderBy(t => t.DueUtc ?? DateTime.MaxValue)
            .ThenBy(t => t.Priority == 0 ? 9 : t.Priority);

        // Same four groups as the board columns, stacked, so switching view does not reshuffle the work.
        Section("In progress", Of(TaskState.InProgress));
        Section("On hold", Of(TaskState.Hold));
        Section("Open", Of(TaskState.Open));
        Section("Completed", Of(TaskState.Completed).OrderByDescending(t => t.CompletedUtc).Take(CompletedShown));

        TaskList.ItemsSource = rows;
        EmptyText.Text = $"Nothing here yet. Add a task above, or press {_s.Settings.QuickAddHotkey} from any app.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BoardColumn Column(string name, TaskState status, string accent, string empty,
        IEnumerable<TodoTask> tasks, TaskRowBuilder builder) => new()
    {
        Name = name,
        Status = status,
        AccentBrush = Ui.Brush(accent),
        EmptyText = empty,
        Rows = tasks.Select(t => builder.Row(t)).ToList()
    };

    private TaskStageHandlers Stages => new()
    {
        Start = t => _s.Store.StartTask(t),
        Hold = t => _s.Store.HoldTask(t),
        Complete = CompleteWithUndo,
        Reopen = t => _s.Store.ReopenTask(t),
        Delete = DeleteWithUndo,
        Restore = t => _s.Store.RestoreTask(t.Id)
    };

    private void CompleteWithUndo(TodoTask t)
    {
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
        _undo.Show($"“{t.Title}” deleted. It stays in Completed until you clear it.", () => _s.Store.RestoreTask(t.Id));
    }

    // ---------------- list view: swipe, detail, keyboard ----------------

    /// <summary>
    /// Builds the swipe actions for the row a recycled container has just been given. A SwipeItem is not
    /// a FrameworkElement and has no DataContext of its own, so the items cannot be bound in the
    /// template and have to be rebuilt here whenever the container is reused.
    /// </summary>
    private void Swipe_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not SwipeControl swipe) return;
        swipe.LeftItems = null;
        swipe.RightItems = null;
        if (args.NewValue is not TaskRow { Task: TodoTask task } row || row.IsHeader) return;

        var left = new SwipeItems { Mode = SwipeMode.Reveal };
        var right = new SwipeItems { Mode = SwipeMode.Reveal };

        foreach (var action in row.Actions)
            (action.IsDanger ? right : left).Add(Item(action.Label, action.Glyph, SwipeBackground(action.Kind), action.Command.Execute));

        // Edit is not a stage change, so it is not in Actions; it belongs with the swipe actions anyway.
        right.Add(Item("Edit", "", Ui.Brush("Surface2Brush"), _ => OpenDetail(task)));

        if (left.Count > 0) swipe.LeftItems = left;
        if (right.Count > 0) swipe.RightItems = right;
    }

    private static SwipeItem Item(string label, string glyph, Brush background, Action<object?> run)
    {
        var item = new SwipeItem { Text = label, IconSource = new FontIconSource { Glyph = glyph }, Background = background };
        item.Invoked += (_, _) => run(null);
        return item;
    }

    private static Brush SwipeBackground(TaskActionKind kind) => kind switch
    {
        TaskActionKind.Start => Ui.Brush("AccentSoftBrush"),
        TaskActionKind.Hold => Ui.Brush("AmberSoftBrush"),
        TaskActionKind.Complete => Ui.Brush("AccentSoftBrush"),
        TaskActionKind.Delete => Ui.Brush("DangerSoftBrush"),
        _ => Ui.Brush("Surface2Brush")
    };

    private void AddBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(AddBox.Text)) return;
        var created = TaskCreator.CreateFromText(_s, AddBox.Text, _listFilter);
        if (_tagFilter != null && !created.Tags.Contains(_tagFilter))
        {
            created.Tags.Add(_tagFilter);
            _s.Store.SaveTask(created);
        }
        AddBox.Text = "";
        e.Handled = true;
    }

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskRow { Task: TodoTask t }) OpenDetail(t);
    }

    private void TaskList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TaskRow { Task: TodoTask t }) return;
        TaskMenu.Build(_s, t, CompleteOrReopen, DeleteWithUndo, OpenDetail)
            .ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
    }

    private void CompleteOrReopen(TodoTask t)
    {
        if (t.IsCompleted) _s.Store.ReopenTask(t);
        else CompleteWithUndo(t);
    }

    private TodoTask? Selected => (TaskList.SelectedItem as TaskRow)?.Task;

    private void CompleteKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Selected is TodoTask t) CompleteOrReopen(t);
        args.Handled = true;
    }

    private void DeleteKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Selected is TodoTask t) DeleteWithUndo(t);
        args.Handled = true;
    }

    private void OpenDetail(TodoTask t)
    {
        Detail.Load(t);
        DetailSplit.IsPaneOpen = true;
    }

    private void CloseDetail() => DetailSplit.IsPaneOpen = false;

    /// <summary>Light dismiss closes the pane without going through CloseDetail, so refresh from here
    /// rather than from the close button, and both routes end up in the same place.</summary>
    private void Detail_PaneClosed(SplitView sender, object args) => Refresh();

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!DetailSplit.IsPaneOpen) return;
        CloseDetail();
        args.Handled = true;
    }

    // ---------------- board view: drag and drop between columns ----------------

    private void Card_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is TaskRow { Task: TodoTask t })
        {
            e.Data.SetText("task:" + t.Id);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        }
        else e.Cancel = true;
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        var target = Target(sender);
        var accept = e.DataView.Contains(StandardDataFormats.Text) && target != null;
        e.AcceptedOperation = accept ? DataPackageOperation.Move : DataPackageOperation.None;
        if (accept && e.DragUIOverride != null)
        {
            e.DragUIOverride.Caption = target!.Name;
            e.DragUIOverride.IsGlyphVisible = false;
        }
        e.Handled = true;
    }

    private async void Column_Drop(object sender, DragEventArgs e)
    {
        if (Target(sender) is not { } column || !e.DataView.Contains(StandardDataFormats.Text)) return;
        // The data view is only readable while the deferral is held.
        var deferral = e.GetDeferral();
        try
        {
            var text = await e.DataView.GetTextAsync();
            if (text.StartsWith("task:") && Guid.TryParse(text[5..], out var id)) MoveTo(id, column.Status);
        }
        catch (Exception ex) { Log.Error("Board drop", ex); }
        finally { deferral.Complete(); }
        e.Handled = true;
    }

    private static BoardColumn? Target(object sender) => (sender as FrameworkElement)?.DataContext as BoardColumn;

    /// <summary>A drop runs the same transition as the card's own button, so both paths behave alike.</summary>
    private void MoveTo(Guid id, TaskState target)
    {
        var t = _s.Store.FindTask(id);
        if (t == null) return;
        if (t.Deleted)
        {
            // Dragging a tombstone out of Completed undoes the delete; dropping it back changes nothing.
            if (target == TaskState.Completed) return;
            _s.Store.RestoreTask(id);
            t = _s.Store.GetTask(id);
            if (t == null) return;
        }
        if (t.Status == target && !t.IsArchived) return;
        switch (target)
        {
            case TaskState.Open: _s.Store.ReopenTask(t); break;
            case TaskState.InProgress: _s.Store.StartTask(t); break;
            case TaskState.Hold: _s.Store.HoldTask(t); break;
            case TaskState.Completed: CompleteWithUndo(t); break;
        }
    }

    private void Card_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskRow { Task: TodoTask t } && !t.Deleted) OpenDetail(t);
    }
}
