using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MyTodo.App.Helpers;
using MyTodo.App.Services;

namespace MyTodo.App.Views;

public sealed class SearchResultRow
{
    public string Group { get; init; } = "";
    public string Kind { get; init; } = "";
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Glyph { get; init; } = "\uE73E";
    public bool IsHeader { get; init; }
    public Visibility ItemVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
    public Windows.UI.Text.FontWeight TitleWeight => IsHeader ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush TitleBrush => IsHeader ? Ui.Brush("Text2Brush") : Ui.Brush("TextBrush");
}

/// <summary>Search across tasks, archive, events, diary and attachment file names (SET-05), under 100 ms with FTS5.</summary>
public sealed partial class SearchPage : Page
{
    private readonly AppServices _s = App.Services;
    private string _query = "";
    private string _filter = "All";
    private List<SearchResultRow> _all = new();

    public SearchPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Run(e.Parameter as string ?? "");

    public void Run(string query)
    {
        _query = query.Trim();
        TitleText.Text = $"Results for \u201C{_query}\u201D";
        var store = _s.Store;
        var lists = store.GetLists().ToDictionary(l => l.Id, l => l.Name);
        var rows = new List<SearchResultRow>();
        foreach (var (kind, id, title, snippet) in store.Search(_query))
        {
            switch (kind)
            {
                case "task" when store.GetTask(id) is { } t:
                    var group = t.IsArchived ? "Archive" : "Tasks";
                    var meta = string.Join(" · ", new[] { lists.GetValueOrDefault(t.ListId, ""), Ui.DueText(t), t.IsCompleted ? "Completed" : "", t.Priority > 0 ? "P" + t.Priority : "" }.Where(x => x.Length > 0 && x != "No date"));
                    rows.Add(new SearchResultRow { Group = group, Kind = "task", Id = id, Title = t.Title, Meta = meta, Glyph = t.IsArchived ? "\uE7B8" : "\uE73E" });
                    break;
                case "event" when store.GetEvent(id) is { } ev:
                    rows.Add(new SearchResultRow { Group = "Events", Kind = "event", Id = id, Title = ev.Title, Meta = $"{ev.Kind} · {Ui.FriendlyDate(Ui.Local(ev.StartUtc))}, {Ui.Local(ev.StartUtc):HH:mm}" + (ev.Rrule != null ? " · repeats" : ""), Glyph = "\uE787" });
                    break;
                case "diary":
                    rows.Add(new SearchResultRow { Group = "Diary", Kind = "diary", Id = id, Title = snippet.Length > 0 ? snippet.Replace("[", "").Replace("]", "") : title, Meta = title, Glyph = "\uE736" });
                    break;
                case "file" when store.GetAttachment(id) is { } a && !a.Deleted:
                    rows.Add(new SearchResultRow { Group = "Attachments", Kind = "file", Id = a.TaskId, Title = a.FileName, Meta = $"On \u201C{store.GetTask(a.TaskId)?.Title}\u201D · {a.SizeText}", Glyph = "\uE723" });
                    break;
            }
        }
        _all = rows;
        CountText.Text = rows.Count == 0 ? "No results. Try fewer or shorter words." : $"{rows.Count} result{(rows.Count == 1 ? "" : "s")}";
        BuildFilters();
        Show();
    }

    private void BuildFilters()
    {
        Filters.Children.Clear();
        var groups = new[] { "All", "Tasks", "Events", "Diary", "Archive", "Attachments" };
        foreach (var g in groups)
        {
            var count = g == "All" ? _all.Count : _all.Count(r => r.Group == g);
            if (g != "All" && count == 0) continue;
            var b = new ToggleButton { Content = $"{g} · {count}", IsChecked = g == _filter, CornerRadius = new CornerRadius(14), Tag = g };
            b.Click += (_, _) => { _filter = (string)b.Tag; BuildFilters(); Show(); };
            Filters.Children.Add(b);
        }
    }

    private void Show()
    {
        var list = new List<SearchResultRow>();
        foreach (var g in new[] { "Tasks", "Events", "Diary", "Archive", "Attachments" })
        {
            if (_filter != "All" && _filter != g) continue;
            var items = _all.Where(r => r.Group == g).ToList();
            if (items.Count == 0) continue;
            list.Add(new SearchResultRow { IsHeader = true, Title = g });
            list.AddRange(items);
        }
        Results.ItemsSource = list;
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultRow { IsHeader: false } r) return;
        var window = App.Instance!.MainWindow!;
        switch (r.Kind)
        {
            case "task": case "file": window.Navigate(typeof(ListsPage), "task:" + r.Id); break;
            case "event": window.Navigate(typeof(CalendarPage), r.Id); break;
            case "diary":
                var entry = _s.Store.GetAllDiary().FirstOrDefault(d => d.Id == r.Id);
                window.Navigate(typeof(DiaryPage), entry?.Date ?? Ui.Today);
                break;
        }
    }
}
