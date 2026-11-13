using MyTodo.Core.Calendar;
using MyTodo.Core.Insights;
using MyTodo.Core.Models;
using MyTodo.Core.Parsing;
using MyTodo.Core.Recurrence;
using MyTodo.Core.Reminders;

namespace MyTodo.Tests;

public class RecurrenceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static DateTime U(int y, int m, int d, int h = 9, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Weekdays_skip_the_weekend()
    {
        var occ = RecurrenceRule.Weekdays().Occurrences(U(2026, 11, 13), U(2026, 11, 13), U(2026, 11, 20), tz: Utc).ToList();
        Assert.Equal(new[] { 13, 16, 17, 18, 19 }, occ.Select(o => o.Day));
    }

    [Fact]
    public void Second_monday_of_each_month()
    {
        var occ = RecurrenceRule.MonthlyByWeekday(2, DayOfWeek.Monday).Occurrences(U(2026, 11, 9), U(2026, 11, 1), U(2027, 2, 1), tz: Utc).ToList();
        Assert.Equal(new[] { (11, 9), (12, 14), (1, 11) }, occ.Select(o => (o.Month, o.Day)));
    }

    [Fact]
    public void Last_friday_of_the_month()
    {
        var rule = RecurrenceRule.Parse("FREQ=MONTHLY;BYDAY=-1FR");
        var occ = rule.Occurrences(U(2026, 11, 27), U(2026, 11, 1), U(2027, 1, 1), tz: Utc).ToList();
        Assert.Equal(new[] { 27, 25 }, occ.Select(o => o.Day));
    }

    [Fact]
    public void Day_31_skips_short_months()
    {
        var occ = RecurrenceRule.MonthlyByDate(31).Occurrences(U(2027, 1, 31), U(2027, 1, 1), U(2027, 6, 1), tz: Utc).ToList();
        Assert.Equal(new[] { 1, 3, 5 }, occ.Select(o => o.Month));
    }

    [Fact]
    public void Count_and_until_stop_the_series()
    {
        Assert.Equal(3, RecurrenceRule.Parse("FREQ=DAILY;COUNT=3").Occurrences(U(2026, 1, 1), U(2026, 1, 1), U(2026, 2, 1), tz: Utc).Count());
        Assert.Equal(5, RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20260105").Occurrences(U(2026, 1, 1), U(2026, 1, 1), U(2026, 2, 1), tz: Utc).Count());
    }

    [Fact]
    public void Text_round_trips()
    {
        var text = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE";
        Assert.Equal(text, RecurrenceRule.Parse(text).ToString());
        Assert.Equal("Every weekday", RecurrenceRule.Weekdays().Describe());
    }

    [Fact]
    public void Keeps_local_time_across_daylight_saving()
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
        // 9:00 local on 20 Oct 2026 (BST, 08:00 UTC); after 25 Oct the clocks go back, so 9:00 local is 09:00 UTC.
        var start = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
        var occ = RecurrenceRule.Daily().Occurrences(start, start, start.AddDays(10), tz: tz).ToList();
        Assert.All(occ, o => Assert.Equal(9, TimeZoneInfo.ConvertTimeFromUtc(o, tz).Hour));
    }

    [Fact]
    public void Exceptions_are_skipped()
    {
        var start = U(2026, 11, 2);
        var occ = RecurrenceRule.Daily().Occurrences(start, start, start.AddDays(3), new[] { start.AddDays(1) }, Utc).ToList();
        Assert.Equal(2, occ.Count);
    }
}

public class QuickAddParserTests
{
    private static readonly DateOnly Thu = new(2026, 11, 12);

    [Fact]
    public void Parses_date_time_priority_tag_and_list()
    {
        var p = QuickAddParser.Parse("Prepare sprint demo fri 10am p2 #work", Thu, new[] { "Inbox", "Work" });
        Assert.Equal("Prepare sprint demo", p.Title);
        Assert.Equal(new DateOnly(2026, 11, 13), p.Date);
        Assert.Equal(new TimeOnly(10, 0), p.Time);
        Assert.Equal(2, p.Priority);
        Assert.Contains("work", p.Tags);
        Assert.Equal("Work", p.ListName);
    }

    [Fact]
    public void Tomorrow_and_24_hour_time()
    {
        var p = QuickAddParser.Parse("Review PR tomorrow 15:30", Thu);
        Assert.Equal(Thu.AddDays(1), p.Date);
        Assert.Equal(new TimeOnly(15, 30), p.Time);
        Assert.Equal("Review PR", p.Title);
    }

    [Fact]
    public void Recurrence_phrases()
    {
        Assert.Equal(RecurrenceRule.Weekdays().ToString(), QuickAddParser.Parse("Stand-up notes every weekday", Thu).Rrule);
        var p = QuickAddParser.Parse("Water plants every 3 days after completion", Thu);
        Assert.True(p.RepeatAfterCompletion);
        Assert.Equal("Water plants", p.Title);
    }

    [Fact]
    public void Plain_text_stays_a_title()
    {
        var p = QuickAddParser.Parse("Monitor the satisfaction survey", Thu);
        Assert.Equal("Monitor the satisfaction survey", p.Title);
        Assert.Null(p.Date);
    }

    [Fact]
    public void Day_month_dates()
    {
        Assert.Equal(new DateOnly(2026, 12, 3), QuickAddParser.Parse("Invoice 3 dec", Thu).Date);
        Assert.Equal(new DateOnly(2027, 1, 5), QuickAddParser.Parse("Plan jan 5", Thu).Date);
    }
}

public class ReminderPlannerTests
{
    private static readonly AppSettings Defaults = new();
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Timed_item_gets_one_hour_thirty_and_fifteen_minutes()
    {
        var start = Now.AddHours(2);
        var r = ReminderPlanner.Defaults(ReminderOwner.Task, Guid.NewGuid(), true, Defaults);
        var plan = ReminderPlanner.Plan(start, true, r, Now, Now, TimeZoneInfo.Utc);
        Assert.Equal(new[] { 60, 30, 15 }, plan.Select(p => (int)(start - p.FireUtc).TotalMinutes));
    }

    [Fact]
    public void Twenty_minutes_ahead_keeps_only_the_fifteen_minute_reminder()
    {
        var start = Now.AddMinutes(20);
        var plan = ReminderPlanner.Plan(start, true, ReminderPlanner.Defaults(ReminderOwner.Task, Guid.NewGuid(), true, Defaults), Now, Now, TimeZoneInfo.Utc);
        Assert.Single(plan);
        Assert.Equal(start.AddMinutes(-15), plan[0].FireUtc);
    }

    [Fact]
    public void Five_minutes_ahead_fires_once_at_start()
    {
        var start = Now.AddMinutes(5);
        var plan = ReminderPlanner.Plan(start, true, ReminderPlanner.Defaults(ReminderOwner.Task, Guid.NewGuid(), true, Defaults), Now, Now, TimeZoneInfo.Utc);
        Assert.Single(plan);
        Assert.True(plan[0].IsFallback);
        Assert.Equal(start, plan[0].FireUtc);
    }

    [Fact]
    public void Fallback_does_not_repeat_after_earlier_reminders_fired()
    {
        // Saved long ago; at 13:50 all reminders for a 14:00 task are past but they did fire, so no extra one.
        var start = new DateTime(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc);
        var plan = ReminderPlanner.Plan(start, true, ReminderPlanner.Defaults(ReminderOwner.Task, Guid.NewGuid(), true, Defaults), start.AddMinutes(-10), start.AddDays(-1), TimeZoneInfo.Utc);
        Assert.Empty(plan);
    }

    [Fact]
    public void Date_only_task_reminds_3pm_day_before_and_9am_on_the_day()
    {
        var due = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var plan = ReminderPlanner.Plan(due, false, ReminderPlanner.Defaults(ReminderOwner.Task, Guid.NewGuid(), false, Defaults), Now, Now, TimeZoneInfo.Utc);
        Assert.Equal(new[] { new DateTime(2026, 11, 13, 15, 0, 0), new DateTime(2026, 11, 14, 9, 0, 0) }, plan.Select(p => DateTime.SpecifyKind(p.FireUtc, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void Snooze_tomorrow_is_nine_am()
    {
        var until = ReminderPlanner.SnoozeUntil(0, Now, TimeZoneInfo.Utc);
        Assert.Equal(new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc), until);
        Assert.Equal(Now.AddMinutes(15), ReminderPlanner.SnoozeUntil(15, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Planning_prompts_skip_weekends()
    {
        var prompts = ReminderPlanner.PlanningPrompts(Now, 9 * 60, 7, TimeZoneInfo.Utc).ToList();
        Assert.DoesNotContain(prompts, p => p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public void Detects_sleep_from_the_heartbeat_gap() =>
        Assert.True(ReminderPlanner.WasAsleep(Now.AddMinutes(-30), Now, TimeSpan.FromMinutes(1)));
}

public class InsightsAndLayoutTests
{
    [Fact]
    public void Weekly_totals_by_day_list_and_tag()
    {
        var week = new DateOnly(2026, 11, 2);
        var mon = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        var done = new[] { new CompletedItem(mon, "Work", new[] { "q4" }), new CompletedItem(mon.AddDays(1), "Personal", Array.Empty<string>()) };
        var spent = new[] { new SpentItem(mon, 90, "Work", new[] { "q4" }), new SpentItem(mon.AddDays(1), 30, "Personal", Array.Empty<string>()) };
        var r = InsightsCalculator.Build(week, done, spent, new[] { new PlannedItem(mon, 120) }, TimeZoneInfo.Utc);
        Assert.Equal(2, r.TasksCompleted);
        Assert.Equal(120, r.MinutesSpent);
        Assert.Equal(120, r.MinutesPlanned);
        Assert.Equal(1, r.TasksPerDay[0]);
        Assert.Equal(90, r.ByList.First(g => g.Name == "Work").Minutes);
        Assert.Equal(1, r.ByTag.Single().Tasks);
        Assert.Equal(8, r.Trend.Count);
        Assert.Equal("1 h 30 min", InsightsCalculator.FormatMinutes(90));
    }

    [Fact]
    public void Overlapping_blocks_sit_side_by_side()
    {
        var t = new DateTime(2026, 11, 11, 11, 0, 0, DateTimeKind.Utc);
        var laid = DayLayout.Layout(new[] { ("a", t, t.AddMinutes(90)), ("b", t.AddMinutes(30), t.AddMinutes(90)), ("c", t.AddHours(3), t.AddHours(4)) });
        Assert.Equal(2, laid.First(x => x.Item == "a").ColumnCount);
        Assert.NotEqual(laid.First(x => x.Item == "a").Column, laid.First(x => x.Item == "b").Column);
        Assert.False(laid.First(x => x.Item == "c").Overlaps);
        Assert.Equal(45, DayLayout.Snap(52));
    }
}
