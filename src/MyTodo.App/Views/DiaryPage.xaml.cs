using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Insights;
using MyTodo.Core.Models;
using Windows.System;

namespace MyTodo.App.Views;

/// <summary>One rich-text entry per day, with that day's completed tasks, meetings and an end-of-day summary (DIA-01 to DIA-04).</summary>
public sealed partial class DiaryPage : Page, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private static DateOnly _day = Ui.Today;
    private DiaryEntry? _entry;
    private HashSet<DateOnly> _entryDates = new();
    private bool _loading;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private string _summaryText = "";

    public DiaryPage()
    {
        InitializeComponent();
        _saveTimer = _s.Dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();

        void Acc(VirtualKey key, VirtualKeyModifiers mods, Action a)
        {
            var k = new KeyboardAccelerator { Key = key, Modifiers = mods };
            k.Invoked += (_, e) => { a(); e.Handled = true; };
            KeyboardAccelerators.Add(k);
        }
        Acc(VirtualKey.PageUp, VirtualKeyModifiers.Control, () => GoTo(_day.AddDays(-1)));
        Acc(VirtualKey.PageDown, VirtualKeyModifiers.Control, () => GoTo(_day.AddDays(1)));
        Acc(VirtualKey.L, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Bullets_Click(this, new RoutedEventArgs()));
        Acc(VirtualKey.K, VirtualKeyModifiers.Control, () => Link_Click(this, new RoutedEventArgs()));
        Acc(VirtualKey.Number1, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => Heading_Click(this, new RoutedEventArgs()));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is DateOnly d) _day = d;
        MiniCalendar.FirstDayOfWeek = _s.Settings.FirstDayOfWeek == DayOfWeek.Sunday ? Windows.Globalization.DayOfWeek.Sunday : Windows.Globalization.DayOfWeek.Monday;
        _entryDates = _s.Store.GetDiaryDates(Ui.Today.AddYears(-5), Ui.Today.AddYears(1));
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) { if (_saveTimer.IsRunning) { _saveTimer.Stop(); Save(); } }

    public void OnThemeChanged() => LoadSidePanel();

    private void GoTo(DateOnly day)
    {
        if (_saveTimer.IsRunning) { _saveTimer.Stop(); Save(); }
        _day = day;
        Load();
    }

    private void Load()
    {
        _loading = true;
        DayTitle.Text = _day.ToString("dddd, d MMMM");
        _entry = _s.Store.GetDiary(_day);
        if (_entry != null && _entry.BodyRtf.Length > 0) Editor.Document.SetText(TextSetOptions.FormatRtf, _entry.BodyRtf);
        else Editor.Document.SetText(TextSetOptions.None, "");
        SavedText.Text = _entry == null ? "Not written yet" : $"Saved on this PC · {Ui.Local(_entry.UpdatedUtc):HH:mm}";
        MiniCalendar.SelectedDates.Clear();
        MiniCalendar.SelectedDates.Add(new DateTimeOffset(_day.ToDateTime(TimeOnly.MinValue)));
        MiniCalendar.SetDisplayDate(new DateTimeOffset(_day.ToDateTime(TimeOnly.MinValue)));
        _loading = false;
        LoadRecent();
        LoadSidePanel();
    }

    private void LoadRecent()
    {
        RecentList.Children.Clear();
        foreach (var r in _s.Store.GetRecentDiary(6))
        {
            var entry = r;
            var text = r.PlainText.Trim().Replace("\r", " ").Replace("\n", " ");
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = r.Date == _day ? Ui.Brush("AccentSoftBrush") : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = r.Date.ToString("ddd d MMM"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Brush("Text2Brush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
            b.Content = stack;
            b.Click += (_, _) => GoTo(entry.Date);
            RecentList.Children.Add(b);
        }
    }

    /// <summary>The day at a glance: completed tasks, meetings, slipped tasks and time per list.</summary>
    private void LoadSidePanel()
    {
        var from = Ui.ToUtc(_day.ToDateTime(TimeOnly.MinValue));
        var to = from.AddDays(1);
        var lists = _s.Store.GetLists().ToDictionary(l => l.Id, l => l.Name);
        var done = _s.Store.GetCompletedBetween(from, to).Where(t => t.ParentId == null).ToList();
        var all = _s.Store.GetAllTasks();
        var slipped = all.Where(t => !t.IsCompleted && t.ParentId == null && t.DueUtc is DateTime d && DateOnly.FromDateTime(Ui.Local(d)) == _day).ToList();
        var meetings = _s.Store.GetOccurrences(from, to).Where(o => o.Event.Kind == EventKind.Meeting).ToList();
        var taskList = all.Concat(done).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().ListId);
        var time = _s.Store.GetTimeEntriesBetween(from, to).Where(e => !e.IsRunning)
            .GroupBy(e => taskList.TryGetValue(e.TaskId, out var l) && lists.TryGetValue(l, out var n) ? n : "Other")
            .Select(g => (name: g.Key, minutes: g.Sum(e => e.Minutes))).OrderByDescending(x => x.minutes).ToList();

        DoneTitle.Text = $"Completed · {done.Count}";
        DoneList.Children.Clear();
        foreach (var t in done)
            DoneList.Children.Add(new TextBlock { Text = "\u2713  " + t.Title, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("TextBrush") });
        if (done.Count == 0) DoneList.Children.Add(new TextBlock { Text = "Nothing completed.", Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });

        MeetingsTitle.Text = $"Meetings · {meetings.Count}";
        MeetingsList.Children.Clear();
        foreach (var m in meetings)
            MeetingsList.Children.Add(new TextBlock { Text = $"{Ui.Local(m.StartUtc):HH:mm}  {m.Event.Title}", FontSize = 13, Foreground = Ui.Brush("MeetingTextBrush") });

        SummaryGrid.Children.Clear();
        SummaryGrid.RowDefinitions.Clear();
        SummaryGrid.ColumnDefinitions.Clear();
        SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rows = new List<(string, string)> { ("Completed", $"{done.Count} task{(done.Count == 1 ? "" : "s")}"), ("Slipped", $"{slipped.Count} task{(slipped.Count == 1 ? "" : "s")}") };
        rows.AddRange(time.Select(x => (x.name, InsightsCalculator.FormatMinutes(x.minutes))));
        for (var i = 0; i < rows.Count; i++)
        {
            SummaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Item1, FontSize = 13, Foreground = Ui.Brush("Text2Brush") };
            var value = new TextBlock { Text = rows[i].Item2, FontSize = 13, Foreground = i == 1 && slipped.Count > 0 ? Ui.Brush("AmberBrush") : Ui.Brush("TextBrush") };
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            SummaryGrid.Children.Add(label);
            SummaryGrid.Children.Add(value);
        }

        _summaryText = "End-of-day summary: " + string.Join(", ", rows.Select(r => $"{r.Item1.ToLowerInvariant()} {r.Item2}")) + "."
                       + (slipped.Count > 0 ? " Slipped: " + string.Join("; ", slipped.Select(t => t.Title)) + "." : "");
    }

    private void MiniCalendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_loading || args.AddedDates.Count == 0) return;
        GoTo(DateOnly.FromDateTime(args.AddedDates[0].Date));
    }

    private void MiniCalendar_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        var d = DateOnly.FromDateTime(args.Item.Date.Date);
        if (_entryDates.Contains(d))
            args.Item.SetDensityColors(new[] { ((Microsoft.UI.Xaml.Media.SolidColorBrush)Ui.Brush("BrandAccentFillBrush")).Color });
        else args.Item.SetDensityColors(null);
    }

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SavedText.Text = "Saving…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        Editor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        Editor.Document.GetText(TextGetOptions.None, out var plain);
        if (_entry == null && string.IsNullOrWhiteSpace(plain)) return;
        _entry ??= new DiaryEntry { Date = _day };
        _entry.BodyRtf = rtf;
        _entry.PlainText = plain.Trim();
        _s.Store.SaveDiary(_entry);
        if (_entry.PlainText.Length > 0) _entryDates.Add(_day); else _entryDates.Remove(_day);
        SavedText.Text = $"Saved on this PC · {DateTime.Now:HH:mm}";
        LoadRecent();
    }

    private void PrevDay_Click(object sender, RoutedEventArgs e) => GoTo(_day.AddDays(-1));
    private void NextDay_Click(object sender, RoutedEventArgs e) => GoTo(_day.AddDays(1));

    private ITextSelection Sel => Editor.Document.Selection;
    private void Bold_Click(object sender, RoutedEventArgs e) => Sel.CharacterFormat.Bold = FormatEffect.Toggle;
    private void Italic_Click(object sender, RoutedEventArgs e) => Sel.CharacterFormat.Italic = FormatEffect.Toggle;
    private void Underline_Click(object sender, RoutedEventArgs e) =>
        Sel.CharacterFormat.Underline = Sel.CharacterFormat.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;

    private void Heading_Click(object sender, RoutedEventArgs e)
    {
        Sel.Expand(TextRangeUnit.Paragraph);
        var isHeading = Sel.CharacterFormat.Size >= 16;
        Sel.CharacterFormat.Size = isHeading ? 14 : 17;
        Sel.CharacterFormat.Bold = isHeading ? FormatEffect.Off : FormatEffect.On;
    }

    private void Bullets_Click(object sender, RoutedEventArgs e) =>
        Sel.ParagraphFormat.ListType = Sel.ParagraphFormat.ListType == MarkerType.Bullet ? MarkerType.None : MarkerType.Bullet;

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "https://…" };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Insert link", Content = box, PrimaryButtonText = "Insert", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        var url = box.Text.Trim();
        if (Sel.Length == 0) Sel.Text = url;
        Sel.Link = "\"" + url.Replace("\"", "") + "\"";
    }

    private void InsertSummary_Click(object sender, RoutedEventArgs e)
    {
        var end = Editor.Document.GetRange(int.MaxValue, int.MaxValue);
        end.Text = (end.StartPosition > 0 ? "\r" : "") + _summaryText;
    }
}
