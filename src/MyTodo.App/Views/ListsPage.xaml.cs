using System.Collections.ObjectModel;
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

/// <summary>A list (Inbox, Work, …) or a smart view: Upcoming, Overdue, No date, Completed, Archive (TSK-02, TSK-06, TSK-08).</summary>
public sealed partial class ListsPage : Page, INewTaskTarget, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private readonly UndoHelper _undo;
    private TodoList? _list;
    private string _view = "";
    private Guid? _selectTask;
    private bool _loading;
    private ObservableCollection<TaskRow> _rows = new();

    public ListsPage()
    {
        InitializeComponent();
        _undo = new UndoHelper(UndoBar);
        Detail.CloseRequested += CloseDetail;
        Detail.TaskChanged += Refresh;
        _loading = true;
        SortCombo.SelectedIndex = 0;
        _loading = false;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var p = e.Parameter as string ?? "";
        if (p.StartsWith("task:") && Guid.TryParse(p[5..], out var taskId) && _s.Store.GetTask(taskId) is TodoTask t)
        {
            _selectTask = t.Id;
            p = t.IsArchived ? "view:archive" : "list:" + t.ListId;
        }
        else if (e.Parameter is Guid id && _s.Store.GetTask(id) is TodoTask t2)
        {
            _selectTask = t2.Id;
            p = "list:" + t2.ListId;
        }
        if (p.StartsWith("list:") && Guid.TryParse(p[5..], out var listId)) { _list = _s.Store.GetLists().FirstOrDefault(l => l.Id == listId); _view = ""; }
        else if (p.StartsWith("view:")) { _list = null; _view = p[5..]; }
        _list ??= _view == "" ? _s.Store.Inbox() : null;

        _s.Store.Changed += OnStoreChanged;
        BuildTagFilter();
        Refresh();
        if (_selectTask is Guid sel && _s.Store.GetTask(sel) is TodoTask selected) OpenDetail(selected);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _s.Store.Changed -= OnStoreChanged;

    private void OnStoreChanged(string table)
    {
        if (table is "tasks" or "attachments" or "time_entries" or "lists") DispatcherQueue.TryEnqueue(Refresh);
    }

    public void OnThemeChanged() => Refresh();
    public void FocusNewTask() => AddBox.Focus(FocusState.Keyboard);

    private void BuildTagFilter()
    {
        _loading = true;
        var tags = _s.Store.GetAllTasks().SelectMany(t => t.Tags).Distinct().OrderBy(t => t).ToList();
        TagCombo.ItemsSource = new[] { "All tags" }.Concat(tags.Select(t => "#" + t)).ToList();
        TagCombo.SelectedIndex = 0;
        _loading = false;
    }

    private void Options_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) Refresh(); }

    private void Refresh()
    {
        var all = _s.Store.GetAllTasks(includeArchived: _view == "archive");
        var builder = new TaskRowBuilder(_s.Store, all, _s.Timer.Running?.TaskId, Toggle);
        var tag = TagCombo.SelectedIndex > 0 ? (TagCombo.SelectedItem as string)?.TrimStart('#') : null;
        var top = all.Where(t => t.ParentId == null && (tag == null || t.Tags.Contains(tag))).ToList();
        var today = Ui.Today;
        DateOnly? Due(TodoTask t) => t.DueUtc is DateTime d ? DateOnly.FromDateTime(Ui.Local(d)) : null;
        var rows = new List<TaskRow>();
        var manual = SortCombo.SelectedIndex == 2;
        IEnumerable<TodoTask> Sort(IEnumerable<TodoTask> items) => SortCombo.SelectedIndex switch
        {
            1 => items.OrderBy(t => t.Priority == 0 ? 9 : t.Priority).ThenBy(t => t.DueUtc ?? DateTime.MaxValue),
            2 => items.OrderBy(t => t.SortOrder),
            _ => items.OrderBy(t => t.DueUtc ?? DateTime.MaxValue).ThenBy(t => t.Priority == 0 ? 9 : t.Priority)
        };
        void Section(string name, IEnumerable<TodoTask> items, bool showList)
        {
            var list = Sort(items).ToList();
            if (list.Count == 0) return;
            if (!manual) rows.Add(TaskRow.Header($"{name} · {list.Count}"));
            rows.AddRange(list.Select(t => builder.Row(t, showList)));
        }

        ListMenuButton.Visibility = _list is { IsInbox: false } ? Visibility.Visible : Visibility.Collapsed;
        AddBox.Visibility = _view is "" or "upcoming" or "nodate" ? Visibility.Visible : Visibility.Collapsed;
        SortCombo.Visibility = _view == "" ? Visibility.Visible : Visibility.Collapsed;

        if (_list != null)
        {
            TitleText.Text = _list.Name;
            ListDot.Fill = Ui.Hex(_list.Color);
            ListDot.Visibility = Visibility.Visible;
            var inList = top.Where(t => t.ListId == _list.Id).ToList();
            var open = inList.Where(t => !t.IsCompleted).ToList();
            if (manual) Section("Tasks", open, false);
            else
            {
                Section("Overdue", open.Where(t => Due(t) is DateOnly d && d < today), false);
                Section("Today", open.Where(t => Due(t) == today), false);
                Section("This week", open.Where(t => Due(t) is DateOnly d && d > today && d <= today.AddDays(7)), false);
                Section("Later", open.Where(t => Due(t) is DateOnly d && d > today.AddDays(7)), false);
                Section("No date", open.Where(t => t.DueUtc == null), false);
            }
            var done = inList.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedUtc).Take(20).ToList();
            if (done.Count > 0) { rows.Add(TaskRow.Header($"Completed · {done.Count}")); rows.AddRange(done.Select(t => builder.Row(t, false))); }
        }
        else
        {
            ListDot.Visibility = Visibility.Collapsed;
            var open = top.Where(t => !t.IsCompleted).ToList();
            switch (_view)
            {
                case "upcoming":
                    TitleText.Text = "Upcoming";
                    for (var i = 0; i <= 7; i++)
                    {
                        var d = today.AddDays(i);
                        Section(Ui.FriendlyDate(d.ToDateTime(TimeOnly.MinValue)), open.Where(t => Due(t) == d), true);
                    }
                    break;
                case "overdue":
                    TitleText.Text = "Overdue";
                    Section("Overdue", open.Where(t => Due(t) is DateOnly d && d < today), true);
                    break;
                case "nodate":
                    TitleText.Text = "No date";
                    Section("No date", open.Where(t => t.DueUtc == null), true);
                    break;
                case "completed":
                    TitleText.Text = "Completed";
                    foreach (var g in top.Where(t => t.IsCompleted && !t.IsArchived && t.CompletedUtc != null)
                                 .GroupBy(t => DateOnly.FromDateTime(Ui.Local(t.CompletedUtc!.Value))).OrderByDescending(g => g.Key))
                    {
                        rows.Add(TaskRow.Header(Ui.FriendlyDate(g.Key.ToDateTime(TimeOnly.MinValue))));
                        rows.AddRange(g.OrderByDescending(t => t.CompletedUtc).Select(t => builder.Row(t)));
                    }
                    break;
                case "archive":
                    TitleText.Text = "Archive";
                    rows.AddRange(top.Where(t => t.IsArchived).OrderByDescending(t => t.CompletedUtc).Select(t => builder.Row(t)));
                    break;
            }
        }

        _rows = new ObservableCollection<TaskRow>(rows);
        TaskList.ItemsSource = _rows;
        TaskList.CanReorderItems = manual && _list != null;
        TaskList.AllowDrop = manual && _list != null;
        EmptyText.Text = _view == "archive"
            ? $"Completed tasks move here {_s.Settings.ArchiveAfterDays} days after completion."
            : "Nothing here. Add a task above, or press Win+Shift+T from any app.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Toggle(TodoTask t)
    {
        if (t.IsCompleted) { _s.Store.ReopenTask(t); return; }
        var done = _s.Store.CompleteTask(t);
        _undo.Show($"\u201C{t.Title}\u201D completed.", () =>
        {
            if (done.Id == t.Id) _s.Store.ReopenTask(done);
            else { _s.Store.DeleteTask(done.Id); if (_s.Store.GetTask(t.Id) is TodoTask orig) { orig.DueUtc = done.DueUtc; _s.Store.SaveTask(orig); } }
        });
    }

    private void AddBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(AddBox.Text)) return;
        DateOnly? date = _view == "upcoming" ? Ui.Today : null;
        TaskCreator.CreateFromText(_s, AddBox.Text, _list?.Id, date);
        AddBox.Text = "";
        e.Handled = true;
    }

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskRow { Task: TodoTask t }) OpenDetail(t);
    }

    private void OpenDetail(TodoTask t)
    {
        Detail.Load(t);
        DetailColumn.Width = new GridLength(420);
        DetailHost.Visibility = Visibility.Visible;
    }

    private void CloseDetail()
    {
        DetailColumn.Width = new GridLength(0);
        DetailHost.Visibility = Visibility.Collapsed;
    }

    private TodoTask? Selected => (TaskList.SelectedItem as TaskRow)?.Task;

    // ---------------- drag: to the calendar, or reorder in manual sort ----------------

    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is TaskRow { Task: TodoTask t }) e.Data.SetText("task:" + t.Id);
        else e.Cancel = true;
    }

    private void TaskList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (!TaskList.CanReorderItems) return;
        var order = 0;
        foreach (var row in _rows.Where(r => r.Task != null))
        {
            if (row.Task!.SortOrder != order) { row.Task.SortOrder = order; _s.Store.SaveTask(row.Task); }
            order++;
        }
    }

    private void Move(int delta)
    {
        if (Selected is not TodoTask t || SortCombo.SelectedIndex != 2) return;
        var tasks = _rows.Where(r => r.Task is { IsCompleted: false }).Select(r => r.Task!).ToList();
        var i = tasks.FindIndex(x => x.Id == t.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= tasks.Count) return;
        (tasks[i], tasks[j]) = (tasks[j], tasks[i]);
        for (var k = 0; k < tasks.Count; k++) if (tasks[k].SortOrder != k) { tasks[k].SortOrder = k; _s.Store.SaveTask(tasks[k]); }
    }

    private void MoveUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Move(-1); args.Handled = true; }
    private void MoveDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Move(1); args.Handled = true; }

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

    private void DeleteWithUndo(TodoTask t)
    {
        var subs = _s.Store.GetSubtasks(t.Id);
        _s.Store.DeleteTask(t.Id);
        if (Detail.Task?.Id == t.Id) CloseDetail();
        _undo.Show($"\u201C{t.Title}\u201D deleted.", () =>
        {
            t.Deleted = false;
            _s.Store.SaveTask(t);
            foreach (var s in subs) { s.Deleted = false; _s.Store.SaveTask(s); }
        });
    }

    // ---------------- context menu: move to list, timer, delete ----------------

    private void TaskList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TaskRow { Task: TodoTask t }) return;
        var menu = new MenuFlyout();
        var complete = new MenuFlyoutItem { Text = t.IsCompleted ? "Reopen" : "Complete" };
        complete.Click += (_, _) => Toggle(t);
        menu.Items.Add(complete);
        if (t.IsArchived)
        {
            var restore = new MenuFlyoutItem { Text = "Restore from archive" };
            restore.Click += (_, _) => _s.Store.ReopenTask(t);
            menu.Items.Add(restore);
        }
        var timer = new MenuFlyoutItem { Text = _s.Timer.Running?.TaskId == t.Id ? "Stop timer" : "Start timer" };
        timer.Click += (_, _) => _s.Timer.Toggle(t.Id);
        menu.Items.Add(timer);
        var moveTo = new MenuFlyoutSubItem { Text = "Move to" };
        foreach (var l in _s.Store.GetLists().Where(l => l.Id != t.ListId))
        {
            var target = l;
            var item = new MenuFlyoutItem { Text = l.Name };
            item.Click += (_, _) =>
            {
                t.ListId = target.Id;
                _s.Store.SaveTask(t);
                foreach (var sub in _s.Store.GetSubtasks(t.Id)) { sub.ListId = target.Id; _s.Store.SaveTask(sub); }
            };
            moveTo.Items.Add(item);
        }
        menu.Items.Add(moveTo);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Delete" };
        delete.Click += (_, _) => DeleteWithUndo(t);
        menu.Items.Add(delete);
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
    }

    // ---------------- list options ----------------

    private async void RenameList_Click(object sender, RoutedEventArgs e)
    {
        if (_list == null) return;
        var box = new TextBox { Text = _list.Name };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Rename list", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        _list.Name = box.Text.Trim();
        _s.Store.SaveList(_list);
    }

    private async void DeleteList_Click(object sender, RoutedEventArgs e)
    {
        if (_list == null || _list.IsInbox) return;
        var count = _s.Store.GetTasksInList(_list.Id).Count(t => t.ParentId == null);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Delete the list \u201C{_list.Name}\u201D?",
            Content = $"The list and its {count} task{(count == 1 ? "" : "s")} are deleted.",
            PrimaryButtonText = "Delete list", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _s.Store.DeleteList(_list.Id);
        App.Instance!.MainWindow!.Navigate(typeof(ListsPage), "list:" + _s.Store.Inbox().Id);
    }
}
