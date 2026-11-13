using System.Globalization;
using System.Text;
using MyTodo.Core.Models;

namespace MyTodo.Core.Export;

/// <summary>Writes events as an iCalendar (.ics) file (CAL-09, SET-03). Local only; nothing is sent anywhere.</summary>
public static class IcsWriter
{
    public static string Write(IEnumerable<CalendarEvent> events)
    {
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//My To-Do//Daily Planner 1.0//EN\r\nCALSCALE:GREGORIAN\r\n");
        foreach (var e in events.Where(e => !e.Deleted))
        {
            sb.Append("BEGIN:VEVENT\r\n");
            Line(sb, "UID", e.Id + "@mytodo.local");
            Line(sb, "DTSTAMP", Utc(e.UpdatedUtc));
            if (e.AllDay)
            {
                Line(sb, "DTSTART;VALUE=DATE", e.StartUtc.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                Line(sb, "DTEND;VALUE=DATE", e.EndUtc.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            else
            {
                Line(sb, "DTSTART", Utc(e.StartUtc));
                Line(sb, "DTEND", Utc(e.EndUtc));
            }
            Line(sb, "SUMMARY", Escape(e.Title));
            if (e.Location.Length > 0) Line(sb, "LOCATION", Escape(e.Location));
            if (e.Notes.Length > 0) Line(sb, "DESCRIPTION", Escape(e.Notes));
            Line(sb, "CATEGORIES", e.Kind switch { EventKind.Meeting => "Meeting", EventKind.Focus => "Focus block", _ => "Personal" });
            if (e.Rrule != null) Line(sb, "RRULE", e.Rrule);
            foreach (var ex in e.ExceptionsUtc) Line(sb, "EXDATE", Utc(ex));
            sb.Append("END:VEVENT\r\n");
        }
        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    private static string Utc(DateTime d) => d.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    // RFC 5545: lines longer than 75 octets are folded.
    private static void Line(StringBuilder sb, string name, string value)
    {
        var line = name + ":" + value;
        while (Encoding.UTF8.GetByteCount(line) > 75)
        {
            var cut = 74;
            while (Encoding.UTF8.GetByteCount(line[..cut]) > 74) cut--;
            sb.Append(line[..cut]).Append("\r\n ");
            line = line[cut..];
        }
        sb.Append(line).Append("\r\n");
    }
}
