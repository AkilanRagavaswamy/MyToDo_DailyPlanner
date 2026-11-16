using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Models;
using TagModel = MyTodo.Core.Models.Tag;

namespace MyTodo.App.Views;

/// <summary>One tag on the tag master.</summary>
public sealed class TagCard
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int Usage { get; init; }
    public required Brush ColorBrush { get; init; }
    public required Brush SoftBrush { get; init; }
    public string Display => "#" + Name;
    public string UsageText => Usage switch { 0 => "Not used yet", 1 => "On 1 task", _ => $"On {Usage} tasks" };
    public string OpenLabel => "Show tasks tagged " + Display;
    public string MenuLabel => "Options for " + Display;

    public override string ToString() => $"{Display}, {UsageText}";
}

/// <summary>
/// The tag master: tags exist here in their own right, so one can be created before any task uses it,
/// renamed everywhere at once, or removed from every task. A tag typed into quick add still appears
/// here on its own, because saving a task creates any tag it mentions.
/// </summary>
public sealed partial class TagsPage : Page, INewTaskTarget, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private readonly UndoHelper _undo;

    public TagsPage()
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
        if (table is "tags" or "tasks") DispatcherQueue.TryEnqueue(Refresh);
    }

    public void OnThemeChanged() => Refresh();
    public void FocusNewTask() => App.Instance!.ShowQuickAdd();

    private void Refresh()
    {
        var usage = _s.Store.TagUsage();
        var cards = _s.Store.GetTags().Select(t => new TagCard
        {
            Id = t.Id,
            Name = t.Name,
            Usage = usage.GetValueOrDefault(t.Name),
            ColorBrush = Ui.Hex(t.Color),
            SoftBrush = Ui.Brush("Surface2Brush")
        }).ToList();
        TagsView.ItemsSource = cards;
        EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TagModel? Find(object sender) =>
        (sender as FrameworkElement)?.Tag is Guid id ? _s.Store.GetTag(id) : null;

    private static void OpenBoard(string name) => App.Instance!.MainWindow!.Navigate(typeof(BoardPage), "tag:" + name);

    private void Tag_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TagCard card) OpenBoard(card.Name);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is { } t) OpenBoard(t.Name);
    }

    private async void NewTag_Click(object sender, RoutedEventArgs e)
    {
        var count = _s.Store.GetTags().Count;
        var r = await NameColourDialog.ShowAsync(XamlRoot, "New tag", "Create", "", TagModel.ColorFor(count),
            TagModel.Palette, "planning");
        if (!r.Confirmed) return;

        var name = TagModel.Normalise(r.Name);
        if (_s.Store.GetTags().Any(t => t.Name == name))
        {
            await Tell($"“#{name}” already exists.");
            return;
        }
        _s.Store.SaveTag(new TagModel { Name = name, Color = r.Colour, SortOrder = count });
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } tag) return;
        var r = await NameColourDialog.ShowAsync(XamlRoot, "Edit tag", "Save", tag.Name, tag.Color, TagModel.Palette, "planning");
        if (!r.Confirmed) return;

        var name = TagModel.Normalise(r.Name);
        if (name != tag.Name && _s.Store.GetTags().Any(t => t.Id != tag.Id && t.Name == name))
        {
            // Renaming onto an existing tag merges them, which is destructive enough to confirm.
            var merge = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = $"Merge into “#{name}”?",
                Content = $"“#{tag.Name}” already has a twin. Every task tagged #{tag.Name} will be tagged #{name} instead, and #{tag.Name} will be removed.",
                PrimaryButtonText = "Merge", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await merge.ShowAsync() != ContentDialogResult.Primary) return;
        }

        if (r.Colour != tag.Color)
        {
            tag.Color = r.Colour;
            _s.Store.SaveTag(tag);
        }
        // Rename last: it rewrites task_tags, and on a merge it removes this row.
        if (name != tag.Name) _s.Store.RenameTag(tag.Id, name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } tag) return;
        var usage = _s.Store.TagUsage().GetValueOrDefault(tag.Name);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Delete “#{tag.Name}”?",
            Content = usage == 0
                ? "The tag is not on any task."
                : $"It is taken off {usage} task{(usage == 1 ? "" : "s")}. The tasks themselves are kept.",
            PrimaryButtonText = "Delete tag", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Which tasks carried it, so undo can put it back on exactly those.
        var tagged = _s.Store.GetAllTasks(includeArchived: true).Where(t => t.Tags.Contains(tag.Name)).ToList();
        _s.Store.DeleteTag(tag.Id);
        _undo.Show($"“#{tag.Name}” deleted.", () =>
        {
            tag.Deleted = false;
            _s.Store.SaveTag(tag);
            foreach (var t in tagged)
            {
                if (_s.Store.GetTask(t.Id) is not { } live || live.Tags.Contains(tag.Name)) continue;
                live.Tags.Add(tag.Name);
                _s.Store.SaveTask(live);
            }
        });
    }

    private Task Tell(string message) => new ContentDialog
    {
        XamlRoot = XamlRoot, Title = "That tag exists", Content = message, CloseButtonText = "OK"
    }.ShowAsync().AsTask();
}
