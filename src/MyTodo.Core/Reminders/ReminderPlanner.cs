using MyTodo.Core.Models;

namespace MyTodo.Core.Reminders;

public sealed record PlannedReminder(Guid? ReminderId, DateTime FireUtc, bool IsFallback);

/// <summary>
/// Pure reminder rules (REM-03, REM-04, REM-05), kept free of Windows APIs so they are unit-tested.
/// </summary>
public static class ReminderPlanner
{
    /// <summary>Default reminder rows for a new item, from settings.</summary>
    public static List<Reminder> Defaults(ReminderOwner owner, Guid ownerId, bool hasTime, AppSettings settings)
    {
        var list = new List<Reminder>();
        if (hasTime)
        {
            foreach (var offset in settings.DefaultOffsetsMinutes.Distinct().OrderByDescending(o => o))
                list.Add(new Reminder { OwnerType = owner, OwnerId = ownerId, Kind = ReminderKind.BeforeStart, OffsetMinutes = offset });
        }
        else
        {
            foreach (var d in settings.DateOnlyReminders)
                list.Add(new Reminder { OwnerType = owner, OwnerId = ownerId, Kind = ReminderKind.DateOnlyAt, DayOffset = d.DayOffset, TimeOfDayMinutes = d.Minutes });
        }
        return list;
    }

    /// <summary>
    /// Fire times for one occurrence. Reminders already in the past are skipped. When every reminder was already
    /// in the past at the moment the item was saved and the start is still ahead, one reminder fires at the start
    /// (the short-notice rule).
    /// </summary>
    public static IReadOnlyList<PlannedReminder> Plan(DateTime startUtc, bool hasTime, IEnumerable<Reminder> reminders,
        DateTime nowUtc, DateTime savedUtc, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var all = new List<(Reminder r, DateTime fire)>();
        foreach (var r in reminders.Where(r => r.Enabled && !r.Deleted))
        {
            DateTime? fire = (r.Kind, hasTime) switch
            {
                (ReminderKind.BeforeStart, true) => startUtc.AddMinutes(-r.OffsetMinutes),
                (ReminderKind.AtStart, true) => startUtc,
                (ReminderKind.DateOnlyAt, false) => DateOnlyFire(startUtc, r.DayOffset, r.TimeOfDayMinutes, tz),
                _ => null
            };
            if (fire is DateTime f) all.Add((r, f));
        }

        var result = all.Where(x => x.fire > nowUtc)
            .OrderBy(x => x.fire)
            .Select(x => new PlannedReminder(x.r.Id, x.fire, false))
            .ToList();

        var allPastWhenSaved = all.Count > 0 && all.All(x => x.fire <= savedUtc);
        if (hasTime && result.Count == 0 && allPastWhenSaved && startUtc > nowUtc)
            result.Add(new PlannedReminder(null, startUtc, true));
        return result;
    }

    private static DateTime DateOnlyFire(DateTime dueUtc, int dayOffset, int minutes, TimeZoneInfo tz)
    {
        var dueLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc), tz).Date;
        var local = DateTime.SpecifyKind(dueLocal.AddDays(dayOffset).AddMinutes(minutes), DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// <summary>When a snooze ends. <paramref name="minutes"/> of 0 means "tomorrow at 9:00 AM".</summary>
    public static DateTime SnoozeUntil(int minutes, DateTime nowUtc, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        if (minutes > 0) return nowUtc.AddMinutes(minutes);
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
        var local = DateTime.SpecifyKind(localToday.AddDays(1).AddHours(9), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// <summary>Weekday planning prompts (REM-08) for the next <paramref name="days"/> days.</summary>
    public static IEnumerable<DateTime> PlanningPrompts(DateTime nowUtc, int minutesOfDay, int days = 7, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
        for (var i = 0; i <= days; i++)
        {
            var d = today.AddDays(i);
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.AddMinutes(minutesOfDay), DateTimeKind.Unspecified), tz);
            if (utc > nowUtc) yield return utc;
        }
    }

    /// <summary>True when the gap between two heartbeats means the PC slept or was off (REM-06).</summary>
    public static bool WasAsleep(DateTime? lastTickUtc, DateTime nowUtc, TimeSpan tickInterval) =>
        lastTickUtc is DateTime last && nowUtc - last > tickInterval + TimeSpan.FromMinutes(2);
}
