using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Calendar;
using MyTodo.Core.Models;
using MyTodo.Core.Recurrence;
using MyTodo.Core.Reminders;

namespace MyTodo.App.Views;

public enum SeriesScope { Cancel, This, Following, All }

/// <summary>Create, edit, move and delete events, including "this / all following / all" for series (CAL-06).</summary>
public static class CalendarActions
{
    /// <summary>CAL-03: a task dropped on the calendar becomes a linked focus block of the default length.</summary>
    public static CalendarEvent CreateBlockFromTask(AppServices s, Guid taskId, DateTime startUtc)
    {
        var task = s.Store.GetTask(taskId);
        var e = new CalendarEvent
        {
            Title = task?.Title ?? "Focus block", Kind = EventKind.Focus, TaskId = taskId,
            StartUtc = startUtc, EndUtc = startUtc.AddMinutes(s.Settings.DefaultBlockMinutes)
        };
        s.Store.SaveEvent(e);
        s.Store.SaveReminders(ReminderOwner.Event, e.Id, ReminderPlanner.Defaults(ReminderOwner.Event, e.Id, true, s.Settings));
        WarnIfOverlap(s, e.Id, e.StartUtc, e.EndUtc);
        return e;
    }

    /// <summary>CAL-07: tells the user when a block lands on top of another event.</summary>
    public static void WarnIfOverlap(AppServices s, Guid id, DateTime startUtc, DateTime endUtc)
    {
        var clash = s.Store.GetOccurrences(startUtc, endUtc).FirstOrDefault(o => o.Event.Id != id && !o.Event.AllDay && DayLayout.Overlaps(o.StartUtc, o.EndUtc, startUtc, endUtc));
        if (clash != null)
            App.Instance?.MainWindow?.ShowInfo($"This overlaps \u201C{clash.Event.Title}\u201D ({Ui.Local(clash.StartUtc):HH:mm} – {Ui.Local(clash.EndUtc):HH:mm}).");
    }

    public static async Task<SeriesScope> AskScopeAsync(XamlRoot root, string verb)
    {
        var radios = new RadioButtons { Header = $"{verb} which events?", SelectedIndex = 0 };
        radios.Items.Add("This event");
        radios.Items.Add("This and all following events");
        radios.Items.Add("All events in the series");
        var dialog = new ContentDialog { XamlRoot = root, Title = "Repeating event", Content = radios, PrimaryButtonText = verb, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return SeriesScope.Cancel;
        return radios.SelectedIndex switch { 1 => SeriesScope.Following, 2 => SeriesScope.All, _ => SeriesScope.This };
    }

    public static async Task MoveAsync(XamlRoot root, AppServices s, EventOccurrence occ, DateTime newStartUtc, DateTime newEndUtc)
    {
        var e = occ.Event;
        if (e.Rrule == null)
        {
            e.StartUtc = newStartUtc;
            e.EndUtc = newEndUtc;
            s.Store.SaveEvent(e);
            WarnIfOverlap(s, e.Id, newStartUtc, newEndUtc);
            return;
        }
        var scope = await AskScopeAsync(root, "Move");
        ApplyToSeries(s, occ, scope, copy => { copy.StartUtc = newStartUtc; copy.EndUtc = newEndUtc; }, shift: newStartUtc - occ.StartUtc, newDuration: newEndUtc - newStartUtc);
    }

    /// <summary>Applies an edit to one occurrence, the occurrences from here on, or the whole series.</summary>
    private static void ApplyToSeries(AppServices s, EventOccurrence occ, SeriesScope scope, Action<CalendarEvent> editOccurrence, TimeSpan shift, TimeSpan newDuration)
    {
        var e = occ.Event;
        switch (scope)
        {
            case SeriesScope.This:
                e.ExceptionsUtc.Add(occ.StartUtc);
                s.Store.SaveEvent(e);
                var single = Clone(e);
                single.Rrule = null;
                single.SeriesId = e.Id;
                single.StartUtc = occ.StartUtc;
                single.EndUtc = occ.EndUtc;
                editOccurrence(single);
                s.Store.SaveEvent(single);
                CopyReminders(s, e.Id, single.Id);
                break;
            case SeriesScope.Following:
                var rule = RecurrenceRule.Parse(e.Rrule!);
                var tail = Clone(e);
                tail.StartUtc = occ.StartUtc;
                tail.EndUtc = occ.EndUtc;
                tail.ExceptionsUtc = e.ExceptionsUtc.Where(x => x > occ.StartUtc).ToList();
                if (rule.Count != null) rule.Count = null;
                tail.Rrule = rule.ToString();
                editOccurrence(tail);
                var head = RecurrenceRule.Parse(e.Rrule!);
                head.Count = null;
                head.UntilUtc = occ.StartUtc.AddSeconds(-1);
                e.Rrule = head.ToString();
                s.Store.SaveEvent(e);
                s.Store.SaveEvent(tail);
                CopyReminders(s, e.Id, tail.Id);
                break;
            case SeriesScope.All:
                e.StartUtc += shift;
                e.EndUtc = e.StartUtc + newDuration;
                e.ExceptionsUtc = e.ExceptionsUtc.Select(x => x + shift).ToList();
                s.Store.SaveEvent(e);
                break;
        }
    }

    private static CalendarEvent Clone(CalendarEvent e) => new()
    {
        Title = e.Title, Notes = e.Notes, Location = e.Location, StartUtc = e.StartUtc, EndUtc = e.EndUtc, AllDay = e.AllDay,
        Kind = e.Kind, TaskId = e.TaskId, Rrule = e.Rrule, ExceptionsUtc = e.ExceptionsUtc.ToList()
    };

    private static void CopyReminders(AppServices s, Guid from, Guid to) =>
        s.Store.SaveReminders(ReminderOwner.Event, to, s.Store.GetReminders(from).Select(r => new Reminder
        {
            Kind = r.Kind, OffsetMinutes = r.OffsetMinutes, DayOffset = r.DayOffset, TimeOfDayMinutes = r.TimeOfDayMinutes, Enabled = r.Enabled
        }).ToList());

    public static async Task DeleteAsync(XamlRoot root, AppServices s, EventOccurrence occ)
    {
        var e = occ.Event;
        if (e.Rrule == null)
        {
            var confirm = new ContentDialog { XamlRoot = root, Title = $"Delete \u201C{e.Title}\u201D?", PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary) s.Store.DeleteEvent(e.Id);
            return;
        }
        switch (await AskScopeAsync(root, "Delete"))
        {
            case SeriesScope.This: e.ExceptionsUtc.Add(occ.StartUtc); s.Store.SaveEvent(e); break;
            case SeriesScope.Following:
                var rule = RecurrenceRule.Parse(e.Rrule);
                rule.Count = null;
                rule.UntilUtc = occ.StartUtc.AddSeconds(-1);
                e.Rrule = rule.ToString();
                s.Store.SaveEvent(e);
                break;
            case SeriesScope.All: s.Store.DeleteEvent(e.Id); break;
        }
    }

    /// <summary>Event editor (CAL-02, CAL-05, CAL-06, CAL-08). Pass an occurrence to edit, or a start time to create.</summary>
    public static async Task EditAsync(XamlRoot root, AppServices s, EventOccurrence? occ, DateTime? newStartUtc)
    {
        var isNew = occ == null;
        var e = occ?.Event ?? new CalendarEvent { Kind = EventKind.Meeting };
        var start = Ui.Local(occ?.StartUtc ?? newStartUtc ?? DateTime.UtcNow);
        var end = Ui.Local(occ?.EndUtc ?? (newStartUtc ?? DateTime.UtcNow).AddMinutes(s.Settings.DefaultBlockMinutes * 2));

        var title = new TextBox { Header = "Title", Text = e.Title, PlaceholderText = "Meeting or focus block" };
        var kind = new ComboBox { Header = "Type", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "Meeting", "Focus block", "Personal" }, SelectedIndex = (int)e.Kind };
        var date = new CalendarDatePicker { Header = "Date", Date = new DateTimeOffset(start.Date), HorizontalAlignment = HorizontalAlignment.Stretch };
        var allDay = new CheckBox { Content = "All day", IsChecked = e.AllDay };
        var from = new TimePicker { Header = "Start", ClockIdentifier = "24HourClock", MinuteIncrement = 15, SelectedTime = start.TimeOfDay };
        var to = new TimePicker { Header = "End", ClockIdentifier = "24HourClock", MinuteIncrement = 15, SelectedTime = end.TimeOfDay };
        var location = new TextBox { Header = "Location", Text = e.Location };
        var notes = new TextBox { Header = "Notes", Text = e.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        var repeatLabels = new[] { "Does not repeat", "Every day", "Every weekday", $"Every week on {start:dddd}", $"Every month on day {start.Day}", "Every year" };
        var repeatRules = new string?[] { null, RecurrenceRule.Daily().ToString(), RecurrenceRule.Weekdays().ToString(), RecurrenceRule.Weekly(start.DayOfWeek).ToString(), RecurrenceRule.MonthlyByDate(start.Day).ToString(), RecurrenceRule.Yearly().ToString() };
        var repeatIndex = Array.IndexOf(repeatRules, e.Rrule);
        var repeatItems = repeatLabels.ToList();
        if (repeatIndex < 0 && e.Rrule != null) { repeatItems.Add("Custom: " + RecurrenceRule.Parse(e.Rrule).Describe()); repeatIndex = repeatItems.Count - 1; }
        var repeat = new ComboBox { Header = "Repeat", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = repeatItems, SelectedIndex = Math.Max(0, repeatIndex), IsEnabled = isNew || e.Rrule == null || occ!.IsRecurring };
        var done = new CheckBox { Content = e.TaskId != null ? "Block done (adds its time to the task)" : "Done", IsChecked = e.DoneUtc != null, Visibility = isNew ? Visibility.Collapsed : Visibility.Visible };

        var times = new Grid { ColumnSpacing = 12 };
        times.ColumnDefinitions.Add(new ColumnDefinition());
        times.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(to, 1);
        times.Children.Add(from);
        times.Children.Add(to);
        allDay.Checked += (_, _) => times.Visibility = Visibility.Collapsed;
        allDay.Unchecked += (_, _) => times.Visibility = Visibility.Visible;
        times.Visibility = e.AllDay ? Visibility.Collapsed : Visibility.Visible;

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        foreach (var c in new UIElement[] { title, kind, date, allDay, times, repeat, location, notes, done }) panel.Children.Add(c);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = isNew ? "New event" : "Edit event", Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", SecondaryButtonText = isNew ? "" : "Delete", DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && occ != null) { await DeleteAsync(root, s, occ); return; }
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title.Text)) return;

        var day = (date.Date ?? DateTimeOffset.Now).Date;
        DateTime newStart, newEnd;
        if (allDay.IsChecked == true) { newStart = Ui.ToUtc(day); newEnd = Ui.ToUtc(day.AddDays(1)); }
        else
        {
            newStart = Ui.ToUtc(day + (from.SelectedTime ?? TimeSpan.FromHours(9)));
            newEnd = Ui.ToUtc(day + (to.SelectedTime ?? TimeSpan.FromHours(10)));
            if (newEnd <= newStart) newEnd = newStart.AddMinutes(s.Settings.DefaultBlockMinutes);
        }

        void Fill(CalendarEvent target)
        {
            target.Title = title.Text.Trim();
            target.Kind = (EventKind)Math.Max(0, kind.SelectedIndex);
            target.AllDay = allDay.IsChecked == true;
            target.Location = location.Text.Trim();
            target.Notes = notes.Text;
        }

        var markDone = done.IsChecked == true && e.DoneUtc == null && !isNew;
        if (isNew || e.Rrule == null)
        {
            var wasTimed = !e.AllDay;
            Fill(e);
            e.StartUtc = newStart;
            e.EndUtc = newEnd;
            e.Rrule = repeat.SelectedIndex < repeatRules.Length ? repeatRules[repeat.SelectedIndex] : e.Rrule;
            if (done.IsChecked != true) e.DoneUtc = null;
            s.Store.SaveEvent(e);
            if (isNew || wasTimed == e.AllDay)
                s.Store.SaveReminders(ReminderOwner.Event, e.Id, ReminderPlanner.Defaults(ReminderOwner.Event, e.Id, !e.AllDay, s.Settings));
            if (markDone) s.Reminders.CompleteBlock(e, e.StartUtc);
            WarnIfOverlap(s, e.Id, e.StartUtc, e.EndUtc);
            return;
        }

        var scope = await AskScopeAsync(root, "Save");
        if (scope == SeriesScope.Cancel) return;
        ApplyToSeries(s, occ!, scope, copy => { Fill(copy); copy.StartUtc = newStart; copy.EndUtc = newEnd; }, newStart - occ!.StartUtc, newEnd - newStart);
        if (scope == SeriesScope.All) { Fill(e); s.Store.SaveEvent(e); }
        if (markDone) s.Reminders.CompleteBlock(e, occ.StartUtc);
    }
}
