using System.Globalization;
using System.Text.RegularExpressions;
using MyTodo.Core.Recurrence;

namespace MyTodo.Core.Parsing;

public sealed class ParsedTask
{
    public string Title { get; set; } = "";
    public DateOnly? Date { get; set; }
    public TimeOnly? Time { get; set; }
    public int Priority { get; set; }
    public List<string> Tags { get; } = new();
    public string? ListName { get; set; }
    public string? Rrule { get; set; }
    public bool RepeatAfterCompletion { get; set; }

    /// <summary>Human-readable chips for the live preview (TSK-07).</summary>
    public IEnumerable<string> PreviewChips()
    {
        if (Date is DateOnly d)
            yield return Time is TimeOnly t ? $"{d.ToDateTime(t):ddd d MMM, HH:mm}" : $"{d.ToDateTime(TimeOnly.MinValue):ddd d MMM}";
        if (Priority > 0) yield return $"P{Priority}";
        if (ListName != null) yield return $"List: {ListName}";
        foreach (var tag in Tags) yield return "#" + tag;
        if (Rrule != null) yield return RecurrenceRule.Parse(Rrule).Describe();
    }

    public DateTime? DueUtc(TimeZoneInfo? tz = null)
    {
        if (Date is not DateOnly d) return null;
        tz ??= TimeZoneInfo.Local;
        var local = d.ToDateTime(Time ?? TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}

/// <summary>
/// Rule-based, fully offline parser for quick add: "Review PR tomorrow 3pm p1 #projectX @Work every week".
/// Recognised tokens are removed from the title; everything else stays as typed.
/// </summary>
public static class QuickAddParser
{
    private static readonly string[] WeekdayNames = CultureInfo.InvariantCulture.DateTimeFormat.DayNames.Select(n => n.ToLowerInvariant()).ToArray();

    public static ParsedTask Parse(string input, DateOnly today, IEnumerable<string>? knownLists = null)
    {
        var result = new ParsedTask();
        var lists = knownLists?.ToList() ?? new List<string>();
        var text = " " + input.Trim() + " ";

        // Recurrence first, so "every monday" is not read as a due date.
        text = Take(text, @"\bevery\s+(\d+)\s+days?\s+after\s+(completion|done)\b", m =>
        {
            result.Rrule = RecurrenceRule.Daily(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToString();
            result.RepeatAfterCompletion = true;
        });
        text = Take(text, @"\bevery\s+(\d+)\s+(day|week|month|year)s?\b", m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var rule = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "day" => RecurrenceRule.Daily(n),
                "week" => new RecurrenceRule { Freq = Frequency.Weekly, Interval = n },
                "month" => new RecurrenceRule { Freq = Frequency.Monthly, Interval = n },
                _ => new RecurrenceRule { Freq = Frequency.Yearly, Interval = n }
            };
            result.Rrule = rule.ToString();
        });
        text = Take(text, @"\bevery\s+weekday\b", _ => result.Rrule = RecurrenceRule.Weekdays().ToString());
        text = Take(text, @"\bevery\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|wed|thu|fri|sat|sun)\b", m =>
        {
            var dow = ParseWeekday(m.Groups[1].Value)!.Value;
            result.Rrule = RecurrenceRule.Weekly(dow).ToString();
            result.Date ??= NextWeekday(today, dow, includeToday: true);
        });
        text = Take(text, @"\b(every\s+day|daily)\b", _ => result.Rrule = RecurrenceRule.Daily().ToString());
        text = Take(text, @"\b(every\s+week|weekly)\b", _ => result.Rrule = new RecurrenceRule { Freq = Frequency.Weekly }.ToString());
        text = Take(text, @"\b(every\s+month|monthly)\b", _ => result.Rrule = new RecurrenceRule { Freq = Frequency.Monthly }.ToString());
        text = Take(text, @"\b(every\s+year|yearly|annually)\b", _ => result.Rrule = RecurrenceRule.Yearly().ToString());

        // Priority p1–p4.
        text = Take(text, @"(?<=\s)[pP]([1-4])(?=\s)", m => result.Priority = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));

        // @List and #tag.
        text = Take(text, @"(?<=\s)@([\p{L}\p{N}_\-]+)(?=\s)", m =>
        {
            var name = m.Groups[1].Value;
            result.ListName = lists.FirstOrDefault(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        });
        text = TakeAll(text, @"(?<=\s)#([\p{L}\p{N}_\-]+)(?=\s)", m =>
        {
            var tag = m.Groups[1].Value;
            result.Tags.Add(tag.ToLowerInvariant());
            result.ListName ??= lists.FirstOrDefault(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase));
        });

        // Times: "at 3pm", "10:30am", "15:30", "noon".
        text = Take(text, @"\b(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)\b", m =>
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) % 12;
            if (m.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase)) h += 12;
            var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (min < 60) result.Time = new TimeOnly(h, min);
        });
        if (result.Time == null)
            text = Take(text, @"\b(?:at\s+)?([01]?\d|2[0-3]):([0-5]\d)\b", m =>
                result.Time = new TimeOnly(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        if (result.Time == null)
            text = Take(text, @"\b(?:at\s+)?noon\b", _ => result.Time = new TimeOnly(12, 0));

        // Dates.
        text = Take(text, @"\b(today|tod|tonight)\b", m =>
        {
            result.Date = today;
            if (m.Value.Equals("tonight", StringComparison.OrdinalIgnoreCase)) result.Time ??= new TimeOnly(20, 0);
        });
        text = Take(text, @"\b(tomorrow|tmr|tmrw)\b", _ => result.Date = today.AddDays(1));
        text = Take(text, @"\bnext\s+week\b", _ => result.Date = NextWeekday(today, DayOfWeek.Monday, includeToday: false));
        text = Take(text, @"\bin\s+(\d+)\s+(day|week)s?\b", m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            result.Date = today.AddDays(m.Groups[2].Value.StartsWith("w", StringComparison.OrdinalIgnoreCase) ? n * 7 : n);
        });
        text = Take(text, @"\b(?:on\s+)?(?:next\s+)?(monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun)\b", m =>
        {
            var dow = ParseWeekday(m.Groups[1].Value);
            if (dow != null) result.Date = NextWeekday(today, dow.Value, includeToday: !m.Value.Contains("next", StringComparison.OrdinalIgnoreCase));
        });
        text = Take(text, @"\b(\d{1,2})\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b", m =>
            result.Date = FutureDate(today, MonthIndex(m.Groups[2].Value), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        text = Take(text, @"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\s+(\d{1,2})\b", m =>
            result.Date = FutureDate(today, MonthIndex(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));

        // A time with no date means today, or tomorrow when the time has already passed.
        if (result.Date == null && result.Time is TimeOnly time)
            result.Date = time < TimeOnly.FromDateTime(DateTime.Now) ? today.AddDays(1) : today;
        // A recurring task with no date starts today.
        if (result.Date == null && result.Rrule != null) result.Date = today;

        result.Title = Regex.Replace(text, @"\s+", " ").Trim();
        if (result.Title.Length == 0) result.Title = input.Trim();
        return result;
    }

    private static string Take(string text, string pattern, Action<Match> apply)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!m.Success) return text;
        apply(m);
        return text.Remove(m.Index, m.Length).Insert(m.Index, " ");
    }

    private static string TakeAll(string text, string pattern, Action<Match> apply)
    {
        string before;
        do { before = text; text = Take(text, pattern, apply); } while (text != before);
        return text;
    }

    private static DayOfWeek? ParseWeekday(string token)
    {
        var t = token.ToLowerInvariant();
        for (var i = 0; i < 7; i++)
            if (WeekdayNames[i].StartsWith(t[..Math.Min(3, t.Length)], StringComparison.Ordinal)) return (DayOfWeek)i;
        return null;
    }

    public static DateOnly NextWeekday(DateOnly from, DayOfWeek day, bool includeToday)
    {
        var diff = ((int)day - (int)from.DayOfWeek + 7) % 7;
        if (diff == 0 && !includeToday) diff = 7;
        return from.AddDays(diff);
    }

    private static int MonthIndex(string token) =>
        Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
            m => m.Length > 0 && token.StartsWith(m, StringComparison.OrdinalIgnoreCase)) + 1;

    private static DateOnly? FutureDate(DateOnly today, int month, int day)
    {
        if (month < 1) return null;
        for (var year = today.Year; year <= today.Year + 1; year++)
        {
            if (day > DateTime.DaysInMonth(year, month)) continue;
            var d = new DateOnly(year, month, day);
            if (d >= today) return d;
        }
        return null;
    }
}
