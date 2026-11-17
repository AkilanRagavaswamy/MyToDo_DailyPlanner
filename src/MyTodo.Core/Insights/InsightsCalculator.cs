namespace MyTodo.Core.Insights;

public sealed record CompletedItem(DateTime CompletedUtc, string ListName, IReadOnlyList<string> Tags);
public sealed record SpentItem(DateTime StartUtc, int Minutes, string ListName, IReadOnlyList<string> Tags);
public sealed record PlannedItem(DateTime StartUtc, int Minutes);

public sealed record GroupRow(string Name, int Tasks, int Minutes);
public sealed record WeekPoint(DateOnly WeekStart, int Tasks, int Minutes);

public sealed class WeeklyReport
{
    public DateOnly WeekStart { get; init; }
    public int TasksCompleted { get; init; }
    public int MinutesSpent { get; init; }
    public int MinutesPlanned { get; init; }
    public int[] TasksPerDay { get; init; } = new int[7];
    public int[] MinutesPerDay { get; init; } = new int[7];
    public IReadOnlyList<GroupRow> ByList { get; init; } = Array.Empty<GroupRow>();
    public IReadOnlyList<GroupRow> ByTag { get; init; } = Array.Empty<GroupRow>();
    public IReadOnlyList<WeekPoint> Trend { get; init; } = Array.Empty<WeekPoint>();
    public int PreviousWeekTasks { get; init; }

    public int AverageMinutesPerTask => TasksCompleted == 0 ? 0 : (int)Math.Round(MinutesSpent / (double)TasksCompleted);
    public int PlannedUsedPercent => MinutesPlanned == 0 ? 0 : (int)Math.Round(100.0 * MinutesSpent / MinutesPlanned);
}

/// <summary>INS-01 to INS-05: weekly tasks completed vs time spent.</summary>
public static class InsightsCalculator
{
    public static DateOnly WeekStartOf(DateOnly day, DayOfWeek firstDay = DayOfWeek.Monday) =>
        day.AddDays(-(((int)day.DayOfWeek - (int)firstDay + 7) % 7));

    public static WeeklyReport Build(DateOnly weekStart, IEnumerable<CompletedItem> completed, IEnumerable<SpentItem> spent,
        IEnumerable<PlannedItem> planned, TimeZoneInfo? tz = null, int trendWeeks = 8)
    {
        tz ??= TimeZoneInfo.Local;
        var done = completed.ToList();
        var time = spent.ToList();
        var plan = planned.ToList();
        DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));
        bool InWeek(DateTime utc, DateOnly start) { var d = Local(utc); return d >= start && d < start.AddDays(7); }

        var weekDone = done.Where(c => InWeek(c.CompletedUtc, weekStart)).ToList();
        var weekTime = time.Where(s => InWeek(s.StartUtc, weekStart)).ToList();

        var tasksPerDay = new int[7];
        var minutesPerDay = new int[7];
        foreach (var c in weekDone) tasksPerDay[Local(c.CompletedUtc).DayNumber - weekStart.DayNumber]++;
        foreach (var s in weekTime) minutesPerDay[Local(s.StartUtc).DayNumber - weekStart.DayNumber] += s.Minutes;

        var byList = weekDone.Select(c => c.ListName).Concat(weekTime.Select(s => s.ListName)).Distinct()
            .Select(n => new GroupRow(n, weekDone.Count(c => c.ListName == n), weekTime.Where(s => s.ListName == n).Sum(s => s.Minutes)))
            .OrderByDescending(r => r.Minutes).ThenByDescending(r => r.Tasks).ToList();
        var byTag = weekDone.SelectMany(c => c.Tags).Concat(weekTime.SelectMany(s => s.Tags)).Distinct()
            .Select(t => new GroupRow("#" + t, weekDone.Count(c => c.Tags.Contains(t)), weekTime.Where(s => s.Tags.Contains(t)).Sum(s => s.Minutes)))
            .OrderByDescending(r => r.Minutes).ThenByDescending(r => r.Tasks).ToList();

        var trend = Enumerable.Range(0, trendWeeks).Select(i => weekStart.AddDays(-7 * (trendWeeks - 1 - i)))
            .Select(ws => new WeekPoint(ws, done.Count(c => InWeek(c.CompletedUtc, ws)), time.Where(s => InWeek(s.StartUtc, ws)).Sum(s => s.Minutes)))
            .ToList();

        return new WeeklyReport
        {
            WeekStart = weekStart,
            TasksCompleted = weekDone.Count,
            MinutesSpent = weekTime.Sum(s => s.Minutes),
            MinutesPlanned = plan.Where(p => InWeek(p.StartUtc, weekStart)).Sum(p => p.Minutes),
            TasksPerDay = tasksPerDay,
            MinutesPerDay = minutesPerDay,
            ByList = byList,
            ByTag = byTag,
            Trend = trend,
            PreviousWeekTasks = done.Count(c => InWeek(c.CompletedUtc, weekStart.AddDays(-7)))
        };
    }

    public static string FormatMinutes(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60:00} min";

    public static string ToCsv(WeeklyReport r)
    {
        var lines = new List<string> { "Section,Name,Tasks,Minutes" };
        string[] days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        for (var i = 0; i < 7; i++) lines.Add($"Day,{r.WeekStart.AddDays(i):yyyy-MM-dd} {days[i]},{r.TasksPerDay[i]},{r.MinutesPerDay[i]}");
        foreach (var g in r.ByList) lines.Add($"List,{Csv(g.Name)},{g.Tasks},{g.Minutes}");
        foreach (var g in r.ByTag) lines.Add($"Tag,{Csv(g.Name)},{g.Tasks},{g.Minutes}");
        lines.Add($"Total,Week of {r.WeekStart:yyyy-MM-dd},{r.TasksCompleted},{r.MinutesSpent}");
        lines.Add($"Planned,Time blocked,,{r.MinutesPlanned}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
