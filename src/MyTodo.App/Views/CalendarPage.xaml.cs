using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Insights;
using MyTodo.Core.Models;
using Windows.System;

namespace MyTodo.App.Views;

/// <summary>Day, work week, week and month views with a pane of unscheduled tasks to drag into slots (CAL-01 to CAL-10).</summary>
public sealed partial class CalendarPage : Page, IThemeAware
{
    private readonly AppServices _s = App.Services;
    private static string _view = "week";
    private static DateOnly _anchor = Ui.Today;

    public CalendarPage()
    {
        InitializeComponent();
        Timeline.WorkStartMinutes = _s.Settings.WorkStartMinutes;
        Timeline.WorkEndMinutes = _s.Settings.WorkEndMinutes;
        Timeline.TaskDropped += (id, start) => CalendarActions.CreateBlockFromTask(_s, id, start);
        Timeline.EventMoved += async (occ, start, end) => await CalendarActions.MoveAsync(XamlRoot, _s, occ, start, end);
        Timeline.EventInvoked += async occ => await CalendarActions.EditAsync(XamlRoot, _s, occ, null);
        Timeline.EventDeleteRequested += async occ => await CalendarActions.DeleteAsync(XamlRoot, _s, occ);
        Timeline.CreateRequested += async start => await CalendarActions.EditAsync(XamlRoot, _s, null, start);
        Month.DaySelected += d => { _anchor = d; Refresh(); };
        Month.DayOpened += d => { _anchor = d; _view = "day"; Refresh(); };
        PoolHint.Text = $"Drag onto the calendar to block time. New blocks are {_s.Settings.DefaultBlockMinutes} minutes.";
        BuildAccelerators();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is Guid id && _s.Store.GetEvent(id) is CalendarEvent ev)
        {
            _anchor = DateOnly.FromDateTime(Ui.Local(ev.StartUtc));
            if (_view == "month") _view = "week";
        }
        _s.Store.Changed += OnStoreChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _s.Store.Changed -= OnStoreChanged;

    private void OnStoreChanged(string table)
    {
        if (table is "events" or "tasks") DispatcherQueue.TryEnqueue(Refresh);
    }

    public void OnThemeChanged() => Refresh();

    private (DateOnly start, int days) Range()
    {
        var first = _s.Settings.FirstDayOfWeek;
        var weekStart = InsightsCalculator.WeekStartOf(_anchor, first);
        return _view switch
        {
            "day" => (_anchor, 1),
            "workweek" => (InsightsCalculator.WeekStartOf(_anchor, DayOfWeek.Monday), 5),
            "week" => (weekStart, _s.Settings.ShowWeekends ? 7 : 5),
            _ => (new DateOnly(_anchor.Year, _anchor.Month, 1), DateTime.DaysInMonth(_anchor.Year, _anchor.Month))
        };
    }

    private void Refresh()
    {
        foreach (var b in new[] { DayButton, WorkWeekButton, WeekButton, MonthButton }) b.IsChecked = (string)b.Tag == _view;
        MonthTitle.Text = _anchor.ToString("MMMM yyyy");
        var (start, days) = Range();

        if (_view == "month")
        {
            Timeline.Visibility = Visibility.Collapsed;
            MonthHost.Visibility = Visibility.Visible;
            AgendaColumn.Width = new GridLength(280);
            var gridStart = start.AddDays(-7);
            var fromUtc = Ui.ToUtc(gridStart.ToDateTime(TimeOnly.MinValue));
            var occ = _s.Store.GetOccurrences(fromUtc, fromUtc.AddDays(49));
            Month.Show(start, _s.Settings.FirstDayOfWeek, occ, _anchor);
            RangeText.Text = "";
            ShowAgenda(_anchor);
        }
        else
        {
            Timeline.Visibility = Visibility.Visible;
            MonthHost.Visibility = Visibility.Collapsed;
            AgendaColumn.Width = new GridLength(0);
            var fromUtc = Ui.ToUtc(start.ToDateTime(TimeOnly.MinValue));
            var toUtc = Ui.ToUtc(start.AddDays(days).ToDateTime(TimeOnly.MinValue));
            Timeline.Show(start, days, _s.Store.GetOccurrences(fromUtc, toUtc));
            RangeText.Text = days == 1 ? start.ToString("dddd d MMM") : $"{start:d MMM} – {start.AddDays(days - 1):d MMM}";
        }

        // Open tasks with no upcoming time block.
        var nowUtc = DateTime.UtcNow;
        var blocked = _s.Store.GetOccurrences(nowUtc.AddDays(-1), nowUtc.AddDays(30)).Where(o => o.Event.TaskId != null).Select(o => o.Event.TaskId!.Value).ToHashSet();
        Pool.ItemsSource = _s.Store.GetAllTasks().Where(t => !t.IsCompleted && t.ParentId == null && !blocked.Contains(t.Id))
            .OrderBy(t => t.DueUtc ?? DateTime.MaxValue).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).Take(60).ToList();
    }

    private void ShowAgenda(DateOnly day)
    {
        var from = Ui.ToUtc(day.ToDateTime(TimeOnly.MinValue));
        var items = _s.Store.GetOccurrences(from, from.AddDays(1));
        AgendaTitle.Text = day.ToString("dddd, d MMMM");
        var blockedMinutes = items.Where(o => !o.Event.AllDay).Sum(o => (o.EndUtc - o.StartUtc).TotalMinutes);
        AgendaSummary.Text = items.Count == 0 ? "No events" : $"{items.Count} event{(items.Count == 1 ? "" : "s")} · {InsightsCalculator.FormatMinutes((int)blockedMinutes)} blocked";
        AgendaItems.Children.Clear();
        foreach (var o in items)
        {
            var (fill, text) = Ui.EventBrushes(o.Event.Kind);
            var occ = o;
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = fill, BorderThickness = new Thickness(0),
                Content = new TextBlock { Text = (o.Event.AllDay ? "All day" : $"{Ui.Local(o.StartUtc):HH:mm} – {Ui.Local(o.EndUtc):HH:mm}") + "   " + o.Event.Title, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis }
            };
            b.Click += async (_, _) => await CalendarActions.EditAsync(XamlRoot, _s, occ, null);
            AgendaItems.Children.Add(b);
        }
        var open = new HyperlinkButton { Content = "Open this day" };
        open.Click += (_, _) => { _view = "day"; Refresh(); };
        AgendaItems.Children.Add(open);
    }

    private void Shift(int direction)
    {
        _anchor = _view switch
        {
            "day" => _anchor.AddDays(direction),
            "month" => _anchor.AddMonths(direction),
            _ => _anchor.AddDays(7 * direction)
        };
        Refresh();
    }

    private void Today_Click(object sender, RoutedEventArgs e) { _anchor = Ui.Today; Refresh(); }
    private void Prev_Click(object sender, RoutedEventArgs e) => Shift(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Shift(1);
    private void View_Click(object sender, RoutedEventArgs e) { if (sender is ToggleButton b) { _view = (string)b.Tag; Refresh(); } }

    private async void NewEvent_Click(object sender, RoutedEventArgs e)
    {
        var start = Ui.ToUtc(_anchor.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Max(_s.Settings.WorkStartMinutes, (DateTime.Now.Hour + 1) * 60 % (24 * 60))));
        await CalendarActions.EditAsync(XamlRoot, _s, null, start);
    }

    private void Pool_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is TodoTask t) e.Data.SetText("task:" + t.Id);
        else e.Cancel = true;
    }

    private void BuildAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action a)
        {
            var k = new KeyboardAccelerator { Key = key, Modifiers = mods };
            k.Invoked += (_, e) => { a(); e.Handled = true; };
            KeyboardAccelerators.Add(k);
        }
        Add(VirtualKey.Number1, VirtualKeyModifiers.Menu, () => { _view = "day"; Refresh(); });
        Add(VirtualKey.Number2, VirtualKeyModifiers.Menu, () => { _view = "workweek"; Refresh(); });
        Add(VirtualKey.Number3, VirtualKeyModifiers.Menu, () => { _view = "week"; Refresh(); });
        Add(VirtualKey.Number4, VirtualKeyModifiers.Menu, () => { _view = "month"; Refresh(); });
        Add(VirtualKey.Left, VirtualKeyModifiers.Control, () => Shift(-1));
        Add(VirtualKey.Right, VirtualKeyModifiers.Control, () => Shift(1));
        Add(VirtualKey.Home, VirtualKeyModifiers.None, () => { _anchor = Ui.Today; Refresh(); });
        Add(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => NewEvent_Click(this, new RoutedEventArgs()));
    }
}
