using System.Globalization;
using System.Text;

namespace MyTodo.Core.Recurrence;

public enum Frequency { Daily, Weekly, Monthly, Yearly }

/// <summary>A weekday with an optional ordinal for monthly rules ("2MO" = second Monday, "-1FR" = last Friday).</summary>
public readonly record struct WeekdayNum(DayOfWeek Day, int Ordinal = 0);

/// <summary>
/// The subset of RFC 5545 RRULE the app needs (TSK-05, CAL-06): FREQ, INTERVAL, BYDAY (with ordinals for MONTHLY),
/// BYMONTHDAY, COUNT and UNTIL. Occurrences are expanded in local wall-clock time so they stay at the same time
/// of day across daylight-saving changes.
/// </summary>
public sealed class RecurrenceRule
{
    public Frequency Freq { get; set; } = Frequency.Daily;
    public int Interval { get; set; } = 1;
    public List<WeekdayNum> ByDay { get; set; } = new();
    public int? ByMonthDay { get; set; }
    public int? Count { get; set; }
    public DateTime? UntilUtc { get; set; }

    private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    // ---------- presets used by the UI ----------
    public static RecurrenceRule Daily(int every = 1) => new() { Freq = Frequency.Daily, Interval = every };
    public static RecurrenceRule Weekdays() => new()
    {
        Freq = Frequency.Weekly,
        ByDay = { new(DayOfWeek.Monday), new(DayOfWeek.Tuesday), new(DayOfWeek.Wednesday), new(DayOfWeek.Thursday), new(DayOfWeek.Friday) }
    };
    public static RecurrenceRule Weekly(params DayOfWeek[] days)
    {
        var r = new RecurrenceRule { Freq = Frequency.Weekly };
        foreach (var d in days) r.ByDay.Add(new(d));
        return r;
    }
    public static RecurrenceRule MonthlyByDate(int day) => new() { Freq = Frequency.Monthly, ByMonthDay = day };
    public static RecurrenceRule MonthlyByWeekday(int ordinal, DayOfWeek day) => new() { Freq = Frequency.Monthly, ByDay = { new(day, ordinal) } };
    public static RecurrenceRule Yearly() => new() { Freq = Frequency.Yearly };

    // ---------- text form ----------
    public static RecurrenceRule Parse(string text)
    {
        var rule = new RecurrenceRule();
        var body = text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? text[6..] : text;
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToUpperInvariant();
            var value = kv[1].Trim().ToUpperInvariant();
            switch (key)
            {
                case "FREQ":
                    rule.Freq = value switch
                    {
                        "DAILY" => Frequency.Daily,
                        "WEEKLY" => Frequency.Weekly,
                        "MONTHLY" => Frequency.Monthly,
                        "YEARLY" => Frequency.Yearly,
                        _ => throw new FormatException($"Unsupported FREQ '{value}'.")
                    };
                    break;
                case "INTERVAL":
                    rule.Interval = Math.Max(1, int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "COUNT":
                    rule.Count = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "BYMONTHDAY":
                    rule.ByMonthDay = int.Parse(value.Split(',')[0], CultureInfo.InvariantCulture);
                    break;
                case "UNTIL":
                    rule.UntilUtc = ParseUntil(value);
                    break;
                case "BYDAY":
                    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var code = token[^2..];
                        var idx = Array.IndexOf(DayCodes, code);
                        if (idx < 0) throw new FormatException($"Unsupported BYDAY '{token}'.");
                        var ord = token.Length > 2 ? int.Parse(token[..^2], CultureInfo.InvariantCulture) : 0;
                        rule.ByDay.Add(new((DayOfWeek)idx, ord));
                    }
                    break;
            }
        }
        return rule;
    }

    private static DateTime ParseUntil(string value)
    {
        var formats = new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd" };
        var dt = DateTime.ParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        // A date-only UNTIL includes that whole day.
        return value.Length == 8 ? dt.AddDays(1).AddTicks(-1) : dt;
    }

    public override string ToString()
    {
        var sb = new StringBuilder("FREQ=").Append(Freq.ToString().ToUpperInvariant());
        if (Interval > 1) sb.Append(";INTERVAL=").Append(Interval);
        if (ByDay.Count > 0)
            sb.Append(";BYDAY=").Append(string.Join(",", ByDay.Select(d => (d.Ordinal != 0 ? d.Ordinal.ToString(CultureInfo.InvariantCulture) : "") + DayCodes[(int)d.Day])));
        if (ByMonthDay is int md) sb.Append(";BYMONTHDAY=").Append(md);
        if (Count is int c) sb.Append(";COUNT=").Append(c);
        if (UntilUtc is DateTime u) sb.Append(";UNTIL=").Append(u.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Plain-English description for the UI ("Every weekday", "2nd Monday of every month").</summary>
    public string Describe()
    {
        string every(string unit) => Interval == 1 ? $"Every {unit}" : $"Every {Interval} {unit}s";
        switch (Freq)
        {
            case Frequency.Daily: return every("day");
            case Frequency.Weekly:
                var set = ByDay.Select(d => d.Day).ToHashSet();
                if (set.SetEquals(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }) && Interval == 1)
                    return "Every weekday";
                return ByDay.Count == 0 ? every("week") : $"{every("week")} on {string.Join(", ", ByDay.Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d.Day)))}";
            case Frequency.Monthly:
                if (ByDay.Count > 0 && ByDay[0].Ordinal != 0)
                    return $"{Ordinal(ByDay[0].Ordinal)} {CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(ByDay[0].Day)} of {(Interval == 1 ? "every month" : $"every {Interval} months")}";
                return ByMonthDay is int d2 ? $"{every("month")} on day {d2}" : every("month");
            default: return every("year");
        }
    }

    private static string Ordinal(int n) => n switch { -1 => "Last", 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    // ---------- expansion ----------

    /// <summary>Occurrence starts (UTC) in [fromUtc, toUtc), skipping exceptions.</summary>
    public IEnumerable<DateTime> Occurrences(DateTime dtStartUtc, DateTime fromUtc, DateTime toUtc,
        IEnumerable<DateTime>? exceptionsUtc = null, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var skip = exceptionsUtc?.Select(e => e.ToUniversalTime()).ToHashSet() ?? new HashSet<DateTime>();
        foreach (var occ in Expand(dtStartUtc, toUtc, tz))
        {
            if (occ < fromUtc) continue;
            if (skip.Contains(occ)) continue;
            yield return occ;
        }
    }

    /// <summary>First occurrence strictly after <paramref name="afterUtc"/>, or null when the series has ended.</summary>
    public DateTime? Next(DateTime dtStartUtc, DateTime afterUtc, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var horizon = afterUtc.AddYears(Freq == Frequency.Yearly ? 12 * Interval : 3 * Interval);
        foreach (var occ in Expand(dtStartUtc, horizon, tz))
            if (occ > afterUtc) return occ;
        return null;
    }

    private IEnumerable<DateTime> Expand(DateTime dtStartUtc, DateTime toUtc, TimeZoneInfo tz)
    {
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dtStartUtc, DateTimeKind.Utc), tz);
        var timeOfDay = startLocal.TimeOfDay;
        var startDate = startLocal.Date;
        var produced = 0;

        for (var k = 0; k < 20000; k++)
        {
            var candidates = CandidatesForPeriod(startDate, k).Where(d => d >= startDate).OrderBy(d => d).ToList();
            var periodStart = PeriodStart(startDate, k);
            var anyBeforeEnd = false;
            foreach (var date in candidates)
            {
                var utc = ToUtc(date + timeOfDay, tz);
                if (utc >= toUtc) yield break;
                if (UntilUtc is DateTime until && utc > until) yield break;
                anyBeforeEnd = true;
                produced++;
                yield return utc;
                if (Count is int c && produced >= c) yield break;
            }
            // Stop once the period itself has passed the end of the range.
            if (!anyBeforeEnd && ToUtc(periodStart, tz) >= toUtc) yield break;
        }
    }

    private DateTime PeriodStart(DateTime startDate, int k) => Freq switch
    {
        Frequency.Daily => startDate.AddDays((long)k * Interval),
        Frequency.Weekly => WeekStart(startDate).AddDays(7L * k * Interval),
        Frequency.Monthly => new DateTime(startDate.Year, startDate.Month, 1).AddMonths(k * Interval),
        _ => new DateTime(startDate.Year, 1, 1).AddYears(k * Interval)
    };

    private IEnumerable<DateTime> CandidatesForPeriod(DateTime startDate, int k)
    {
        var p = PeriodStart(startDate, k);
        switch (Freq)
        {
            case Frequency.Daily:
                if (ByDay.Count == 0 || ByDay.Any(d => d.Day == p.DayOfWeek)) yield return p;
                break;
            case Frequency.Weekly:
                var days = ByDay.Count > 0 ? ByDay.Select(d => d.Day).Distinct() : new[] { startDate.DayOfWeek };
                foreach (var d in days)
                    yield return p.AddDays(((int)d - (int)DayOfWeek.Monday + 7) % 7);
                break;
            case Frequency.Monthly:
                var dim = DateTime.DaysInMonth(p.Year, p.Month);
                if (ByDay.Count > 0 && ByDay[0].Ordinal != 0)
                {
                    var nth = NthWeekday(p.Year, p.Month, ByDay[0].Day, ByDay[0].Ordinal);
                    if (nth != null) yield return nth.Value;
                }
                else
                {
                    var day = ByMonthDay ?? startDate.Day;
                    if (day < 0) day = dim + day + 1;
                    if (day >= 1 && day <= dim) yield return new DateTime(p.Year, p.Month, day);
                }
                break;
            case Frequency.Yearly:
                if (startDate.Month == 2 && startDate.Day == 29 && !DateTime.IsLeapYear(p.Year)) yield break;
                yield return new DateTime(p.Year, startDate.Month, startDate.Day);
                break;
        }
    }

    private static DateTime WeekStart(DateTime d) => d.AddDays(-(((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));

    public static DateTime? NthWeekday(int year, int month, DayOfWeek day, int ordinal)
    {
        if (ordinal > 0)
        {
            var first = new DateTime(year, month, 1);
            var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
            var result = first.AddDays(offset + 7 * (ordinal - 1));
            return result.Month == month ? result : null;
        }
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var back = ((int)last.DayOfWeek - (int)day + 7) % 7;
        var r = last.AddDays(-back - 7 * (-ordinal - 1));
        return r.Month == month ? r : null;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A wall-clock time inside a DST gap does not exist; move it forward by the gap.
        while (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}
