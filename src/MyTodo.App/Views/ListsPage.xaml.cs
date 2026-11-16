using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Models;

namespace MyTodo.App.Views;

/// <summary>One list on the list master.</summary>
public sealed class ListCard
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string CountText { get; init; }
    public required Brush ColorBrush { get; init; }
    public required Brush SoftBrush { get; init; }
    public string OpenLabel => "Open " + Name;
    public string MenuLabel => "Options for " + Name;

    /// <summary>The name a screen reader reads for the card.</summary>
    public override string ToString() => $"{Name}, {CountText}";
}

/// <summary>
/// The list master (TSK-02): every list the user has, with Edit and Delete. It deliberately shows no
/// tasks â tasks live on the Board, and this page exists to manage the lists themselves.
/// </summary>
public sealed partial class ListsPage : Page, INewTaskTarget, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private readonly UndoHelper _undo;

    public ListsPage()
    {
        InitializeComponent();
        _undo = new UndoHelper(UndoBar);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _s.Store.Changed += OnStoreChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _s.Store.Changed -= OnStoreChanged;

    private void OnStoreChanged(string table)
    {
        if (table is "lists" or "tasks") DispatcherQueue.TryEnqueue(Refresh);
    }

    public void OnThemeChanged() => Refresh();

    /// <summary>There is no task entry on this page, so Ctrl+N opens quick add rather than doing nothing.</summary>
    public void FocusNewTask() => App.Instance!.ShowQuickAdd();

    private void Refresh()
    {
        var tasks = _s.Store.GetAllTasks().Where(t => t.ParentId == null).ToList();
        ListsView.ItemsSource = _s.Store.GetLists().Select(l =>
        {
            var inList = tasks.Where(t => t.ListId == l.Id).ToList();
            var open = inList.Count(t => t.IsActive);
            return new ListCard
            {
                Id = l.Id,
                Name = l.Name,
                CountText = inList.Count == 0
                    ? "No tasks yet"
                    : $"{open} open Â· {inList.Count - open} completed",
                ColorBrush = Ui.Hex(l.Color),
                SoftBrush = Ui.Brush("Surface2Brush")
            };
        }).ToList();
    }

    private TodoList? Find(object sender) =>
        (sender as FrameworkElement)?.Tag is Guid id ? _s.Store.GetLists().FirstOrDefault(l => l.Id == id) : null;

    private static void OpenBoard(Guid id) => App.Instance!.MainWindow!.Navigate(typeof(BoardPage), "list:" + id);

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ListCard card) OpenBoard(card.Id);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is { } l) OpenBoard(l.Id);
    }

    private async void NewList_Click(object sender, RoutedEventArgs e)
    {
        var count = _s.Store.GetLists().Count;
        await EditAsync(new TodoList { Color = TodoList.ColorFor(count), SortOrder = count }, "New list", "Create");
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is { } l) await EditAsync(l, "Edit list", "Save");
    }

    /// <summary>Name and colour, through the dialog the tag master also uses.</summary>
    private async Task EditAsync(TodoList list, string title, string action)
    {
        var r = await NameColourDialog.ShowAsync(XamlRoot, title, action, list.Name, list.Color,
            TodoList.Palette, "Work, Personal, Home…");
        if (!r.Confirmed) return;
        list.Name = r.Name;
        list.Color = r.Colour;
        _s.Store.SaveList(list);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } list) return;
        if (_s.Store.GetLists().Count <= 1)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "This is your only list",
                Content = "Create another list before deleting this one. Tasks always need a list to live in.",
                CloseButtonText = "OK"
            }.ShowAsync();
            return;
        }
        var tasks = _s.Store.GetTasksInList(list.Id).Where(t => t.ParentId == null).ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Delete the list â{list.Name}â?",
            Content = $"The list and its {tasks.Count} task{(tasks.Count == 1 ? "" : "s")} are deleted.",
            PrimaryButtonText = "Delete list", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _s.Store.DeleteList(list.Id);
        // DeleteList tombstones the list and its tasks, so undo has to lift both again.
        _undo.Show($"â{list.Name}â deleted.", () =>
        {
            list.Deleted = false;
            _s.Store.SaveList(list);
            foreach (var t in tasks) _s.Store.RestoreTask(t.Id);
        });
    }
}
