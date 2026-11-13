using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Insights;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MyTodo.App.Views;

/// <summary>Weekly tasks completed vs time spent, by day, list and tag, with an 8-week trend (INS-01 to INS-05).</summary>
public sealed partial class InsightsPage : Page, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private DateOnly _week;
    private WeeklyReport? _report;

    public InsightsPage()
    {
        InitializeComponent();
        _week = InsightsCalculator.WeekStartOf(Ui.Today, _s.Settings.FirstDayOfWeek);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await RefreshAsync();
    public void OnThemeChanged() => Draw();

    private async Task RefreshAsync()
    {
        var week = _week;
        WeekText.Text = $"Week of {week:d MMM} – {week.AddDays(6):d MMM yyyy}";
        var store = _s.Store;
        _report = await Task.Run(() =>
        {
            var fromUtc = Ui.ToUtc(week.AddDays(-7 * 8).ToDateTime(TimeOnly.MinValue));
            var toUtc = Ui.ToUtc(week.AddDays(7).ToDateTime(TimeOnly.MinValue));
            var lists = store.GetLists().ToDictionary(l => l.Id, l => l.Name);
            var tasks = store.GetAllTasks(includeArchived: true).ToDictionary(t => t.Id);
            string ListOf(Guid id) => tasks.TryGetValue(id, out var t) && lists.TryGetValue(t.ListId, out var n) ? n : "Other";
            IReadOnlyList<string> TagsOf(Guid id) => tasks.TryGetValue(id, out var t) ? t.Tags : Array.Empty<string>();
            var completed = store.GetCompletedBetween(fromUtc, toUtc).Where(t => t.ParentId == null)
                .Select(t => new CompletedItem(t.CompletedUtc!.Value, ListOf(t.Id), t.Tags));
            var spent = store.GetTimeEntriesBetween(fromUtc, toUtc).Where(x => !x.IsRunning)
                .Select(x => new SpentItem(x.StartUtc, x.Minutes, ListOf(x.TaskId), TagsOf(x.TaskId)));
            var planned = store.GetOccurrences(Ui.ToUtc(week.ToDateTime(TimeOnly.MinValue)), toUtc)
                .Where(o => o.Event.TaskId != null || o.Event.Kind == Core.Models.EventKind.Focus)
                .Select(o => new PlannedItem(o.StartUtc, (int)(o.EndUtc - o.StartUtc).TotalMinutes));
            return InsightsCalculator.Build(week, completed, spent, planned);
        });
        Draw();
    }

    private void Draw()
    {
        if (_report is not WeeklyReport r) return;
        var diff = r.TasksCompleted - r.PreviousWeekTasks;
        Cards(new[]
        {
            ("Tasks completed", r.TasksCompleted.ToString(), diff == 0 ? "Same as the week before" : $"{Math.Abs(diff)} {(diff > 0 ? "more" : "fewer")} than the week before"),
            ("Time spent", InsightsCalculator.FormatMinutes(r.MinutesSpent), "Timer, time blocks and manual entries"),
            ("Planned vs actual", $"{InsightsCalculator.FormatMinutes(r.MinutesPlanned)} / {InsightsCalculator.FormatMinutes(r.MinutesSpent)}", r.MinutesPlanned == 0 ? "No time blocked this week" : $"{r.PlannedUsedPercent}% of blocked time used"),
            ("Average per task", InsightsCalculator.FormatMinutes(r.AverageMinutesPerTask), "Completed tasks only")
        });
        string[] days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var labels = Enumerable.Range(0, 7).Select(i => r.WeekStart.AddDays(i).ToString("ddd")).ToArray();
        Bars(TasksChart, labels, r.TasksPerDay.Select(v => (double)v).ToArray(), r.TasksPerDay.Select(v => v.ToString()).ToArray(), Ui.Brush("BrandAccentFillBrush"));
        Bars(HoursChart, labels, r.MinutesPerDay.Select(v => v / 60.0).ToArray(), r.MinutesPerDay.Select(v => v == 0 ? "0" : $"{v / 60}:{v % 60:00}").ToArray(), Ui.Brush("AmberBrush"));
        Bars(TrendChart, r.Trend.Select(w => w.WeekStart.ToString("d MMM") + "\n" + InsightsCalculator.FormatMinutes(w.Minutes)).ToArray(),
            r.Trend.Select(w => (double)w.Tasks).ToArray(), r.Trend.Select(w => w.Tasks.ToString()).ToArray(), Ui.Brush("BrandAccentFillBrush"), highlightLast: true);
        Table(r);
    }

    private void Cards((string label, string value, string note)[] cards)
    {
        Kpis.Children.Clear();
        Kpis.ColumnDefinitions.Clear();
        for (var i = 0; i < cards.Length; i++)
        {
            Kpis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = cards[i].label, FontSize = 12, Foreground = Ui.Brush("Text2Brush") });
            stack.Children.Add(new TextBlock { Text = cards[i].value, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = cards[i].note, FontSize = 12, Foreground = Ui.Brush("Text2Brush"), TextWrapping = TextWrapping.Wrap });
            var card = new Border { Style = (Style)Application.Current.Resources["CardBorderStyle"], Child = stack };
            Grid.SetColumn(card, i);
            Kpis.Children.Add(card);
        }
    }

    /// <summary>Simple native bar chart: value label above each bar, category label below.</summary>
    private static void Bars(Grid host, string[] labels, double[] values, string[] valueText, Brush fill, bool highlightLast = false)
    {
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var max = Math.Max(1, values.DefaultIfEmpty(0).Max());
        var plotHeight = host.Height - 44;
        for (var i = 0; i < values.Length; i++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = valueText[i], FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush("Text2Brush") });
            stack.Children.Add(new Rectangle
            {
                Height = Math.Max(values[i] > 0 ? 2 : 0, values[i] / max * plotHeight), Width = 30, RadiusX = 3, RadiusY = 3,
                Fill = highlightLast && i < values.Length - 1 ? Ui.Brush("AccentSoftBrush") : fill, HorizontalAlignment = HorizontalAlignment.Center
            });
            Grid.SetColumn(stack, i);
            host.Children.Add(stack);
            var label = new TextBlock { Text = labels[i], FontSize = 11, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush("Text2Brush"), Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 1);
            host.Children.Add(label);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, string.Join(", ", labels.Select((l, i) => $"{l.Replace("\n", " ")}: {valueText[i]}")));
    }

    private void Table(WeeklyReport r)
    {
        Breakdown.Children.Clear();
        Breakdown.RowDefinitions.Clear();
        Breakdown.ColumnDefinitions.Clear();
        Breakdown.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Breakdown.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Breakdown.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rows = new List<(string, string, string)> { ("Name", "Tasks", "Time spent") };
        rows.AddRange(r.ByList.Concat(r.ByTag).Select(g => (g.Name, g.Tasks.ToString(), InsightsCalculator.FormatMinutes(g.Minutes))));
        if (rows.Count == 1) rows.Add(("No completed tasks or time this week", "", ""));
        for (var i = 0; i < rows.Count; i++)
        {
            Breakdown.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = new[] { rows[i].Item1, rows[i].Item2, rows[i].Item3 };
            for (var c = 0; c < 3; c++)
            {
                var tb = new TextBlock { Text = cells[c], FontSize = 13, HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
                if (i == 0) { tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; tb.Foreground = Ui.Brush("Text2Brush"); }
                Grid.SetRow(tb, i);
                Grid.SetColumn(tb, c);
                Breakdown.Children.Add(tb);
            }
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) { _week = _week.AddDays(-7); await RefreshAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { _week = _week.AddDays(7); await RefreshAsync(); }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        var picker = new FileSavePicker { SuggestedFileName = $"My To-Do insights {_report.WeekStart:yyyy-MM-dd}" };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Instance!.MainWindow!.Hwnd);
        if (await picker.PickSaveFileAsync() is StorageFile file) await FileIO.WriteTextAsync(file, InsightsCalculator.ToCsv(_report));
    }
}
