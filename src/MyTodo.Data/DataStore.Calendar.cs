using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using MyTodo.Core.Models;
using MyTodo.Core.Recurrence;

namespace MyTodo.Data;

public sealed partial class DataStore
{
    // ---------------- Events ----------------

    private static CalendarEvent MapEvent(SqliteDataReader r) => new()
    {
        Id = r.G("id"), Title = r.S("title"), Notes = r.S("notes"), Location = r.S("location"), StartUtc = r.D("start"), EndUtc = r.D("end"),
        AllDay = r.B("all_day"), Kind = (EventKind)r.I("kind"), TaskId = r.GN("task_id"), Rrule = r.SN("rrule"),
        ExceptionsUtc = r.S("exceptions").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Conv.FromMs(long.Parse(s, CultureInfo.InvariantCulture))).ToList(),
        SeriesId = r.GN("series_id"), DoneUtc = r.DN("done"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    public List<CalendarEvent> GetAllEvents() => _db.Query("SELECT * FROM events WHERE deleted = 0 ORDER BY start;", MapEvent);
    public CalendarEvent? GetEvent(Guid id) => _db.Query("SELECT * FROM events WHERE id = $id;", MapEvent, ("$id", id.ToString())).FirstOrDefault();
    public List<CalendarEvent> GetEventsForTask(Guid taskId) =>
        _db.Query("SELECT * FROM events WHERE deleted = 0 AND task_id = $t ORDER BY start;", MapEvent, ("$t", taskId.ToString()));

    /// <summary>Every occurrence overlapping [fromUtc, toUtc), recurring series expanded in local time.</summary>
    public List<EventOccurrence> GetOccurrences(DateTime fromUtc, DateTime toUtc)
    {
        var rows = _db.Query("SELECT * FROM events WHERE deleted = 0 AND ((rrule IS NULL AND start < $to AND \"end\" > $from) OR (rrule IS NOT NULL AND start < $to));",
            MapEvent, ("$from", Conv.Ms(fromUtc)), ("$to", Conv.Ms(toUtc)));
        var result = new List<EventOccurrence>();
        foreach (var e in rows)
        {
            if (e.Rrule == null) { result.Add(new EventOccurrence(e, e.StartUtc, e.EndUtc)); continue; }
            var rule = RecurrenceRule.Parse(e.Rrule);
            // Start the expansion one duration earlier so an occurrence running into the range is included.
            foreach (var s in rule.Occurrences(e.StartUtc, fromUtc - e.Duration, toUtc, e.ExceptionsUtc))
                if (s + e.Duration > fromUtc) result.Add(new EventOccurrence(e, s, s + e.Duration));
        }
        return result.OrderBy(o => o.StartUtc).ToList();
    }

    public void SaveEvent(CalendarEvent e)
    {
        _db.InTransaction((c, tx) =>
        {
            Upsert(c, tx, "events", e, "title, notes, location, start, \"end\", all_day, kind, task_id, rrule, exceptions, series_id, done",
                "$title, $notes, $loc, $start, $end, $allday, $kind, $task, $rrule, $ex, $series, $done",
                "title=excluded.title, notes=excluded.notes, location=excluded.location, start=excluded.start, \"end\"=excluded.\"end\", all_day=excluded.all_day, kind=excluded.kind, task_id=excluded.task_id, rrule=excluded.rrule, exceptions=excluded.exceptions, series_id=excluded.series_id, done=excluded.done",
                ("$title", e.Title), ("$notes", e.Notes), ("$loc", e.Location), ("$start", Conv.Ms(e.StartUtc)), ("$end", Conv.Ms(e.EndUtc)),
                ("$allday", e.AllDay ? 1 : 0), ("$kind", (int)e.Kind), ("$task", e.TaskId?.ToString()), ("$rrule", e.Rrule),
                ("$ex", string.Join(",", e.ExceptionsUtc.Select(x => Conv.Ms(x).ToString(CultureInfo.InvariantCulture)))),
                ("$series", e.SeriesId?.ToString()), ("$done", Conv.Ms(e.DoneUtc)));
            if (e.Deleted) Unindex(c, tx, e.Id);
            else Index(c, tx, "event", e.Id, e.Title, e.Notes + " " + e.Location);
        });
        Raise("events");
    }

    public void DeleteEvent(Guid id)
    {
        var e = GetEvent(id);
        if (e == null) return;
        e.Deleted = true;
        SaveEvent(e);
        SaveReminders(ReminderOwner.Event, id, new List<Reminder>());
    }

    // ---------------- Reminders ----------------

    private static Reminder MapReminder(SqliteDataReader r) => new()
    {
        Id = r.G("id"), OwnerType = (ReminderOwner)r.I("owner_type"), OwnerId = r.G("owner_id"), Kind = (ReminderKind)r.I("kind"),
        OffsetMinutes = r.I("offset_min"), DayOffset = r.I("day_offset"), TimeOfDayMinutes = r.I("tod_min"), Enabled = r.B("enabled"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    public List<Reminder> GetReminders(Guid ownerId) =>
        _db.Query("SELECT * FROM reminders WHERE deleted = 0 AND owner_id = $o ORDER BY kind, offset_min DESC, day_offset, tod_min;", MapReminder, ("$o", ownerId.ToString()));

    public List<Reminder> GetAllReminders() => _db.Query("SELECT * FROM reminders WHERE deleted = 0;", MapReminder);

    /// <summary>Replaces an item's reminders with <paramref name="reminders"/>.</summary>
    public void SaveReminders(ReminderOwner owner, Guid ownerId, List<Reminder> reminders)
    {
        var keep = reminders.Select(r => r.Id).ToHashSet();
        _db.InTransaction((c, tx) =>
        {
            foreach (var old in GetRemindersUnlocked(c, tx, ownerId).Where(o => !keep.Contains(o.Id)))
            {
                old.Deleted = true;
                WriteReminder(c, tx, old);
            }
            foreach (var r in reminders)
            {
                r.OwnerType = owner;
                r.OwnerId = ownerId;
                WriteReminder(c, tx, r);
            }
        });
        Raise("reminders");
    }

    private static List<Reminder> GetRemindersUnlocked(SqliteConnection c, SqliteTransaction tx, Guid ownerId)
    {
        using var cmd = Database.Command(c, tx, "SELECT * FROM reminders WHERE deleted = 0 AND owner_id = $o;", ("$o", ownerId.ToString()));
        using var r = cmd.ExecuteReader();
        var list = new List<Reminder>();
        while (r.Read()) list.Add(MapReminder(r));
        return list;
    }

    private void WriteReminder(SqliteConnection c, SqliteTransaction tx, Reminder r) =>
        Upsert(c, tx, "reminders", r, "owner_type, owner_id, kind, offset_min, day_offset, tod_min, enabled",
            "$ot, $oid, $kind, $off, $day, $tod, $en",
            "owner_type=excluded.owner_type, owner_id=excluded.owner_id, kind=excluded.kind, offset_min=excluded.offset_min, day_offset=excluded.day_offset, tod_min=excluded.tod_min, enabled=excluded.enabled",
            ("$ot", (int)r.OwnerType), ("$oid", r.OwnerId.ToString()), ("$kind", (int)r.Kind), ("$off", r.OffsetMinutes),
            ("$day", r.DayOffset), ("$tod", r.TimeOfDayMinutes), ("$en", r.Enabled ? 1 : 0));

    // ---------------- Reminder log (missed / dismissed tracking) ----------------

    public enum ReminderLogState { Shown = 0, Snoozed = 1, Dismissed = 2, Done = 3 }

    public static string ReminderKey(Guid ownerId, DateTime fireUtc) => ownerId + "|" + Conv.Ms(fireUtc).ToString(CultureInfo.InvariantCulture);

    public void MarkReminder(string key, ReminderLogState state) =>
        _db.Exec("INSERT INTO reminder_log (key, state, at) VALUES ($k, $s, $a) ON CONFLICT(key) DO UPDATE SET state = excluded.state, at = excluded.at;",
            ("$k", key), ("$s", (int)state), ("$a", Conv.Ms(DateTime.UtcNow)));

    public ReminderLogState? GetReminderState(string key)
    {
        var v = _db.Scalar("SELECT state FROM reminder_log WHERE key = $k;", ("$k", key));
        return v is null or DBNull ? null : (ReminderLogState)Convert.ToInt32(v);
    }

    // ---------------- Diary ----------------

    private static DiaryEntry MapDiary(SqliteDataReader r) => new()
    {
        Id = r.G("id"), Date = DateOnly.ParseExact(r.S("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture), BodyRtf = r.S("body"), PlainText = r.S("plain"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    public DiaryEntry? GetDiary(DateOnly date) =>
        _db.Query("SELECT * FROM diary_entries WHERE deleted = 0 AND date = $d;", MapDiary, ("$d", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).FirstOrDefault();

    public List<DiaryEntry> GetAllDiary() => _db.Query("SELECT * FROM diary_entries WHERE deleted = 0 ORDER BY date DESC;", MapDiary);

    public List<DiaryEntry> GetRecentDiary(int count) =>
        _db.Query("SELECT * FROM diary_entries WHERE deleted = 0 AND plain <> '' ORDER BY date DESC LIMIT $n;", MapDiary, ("$n", count));

    public HashSet<DateOnly> GetDiaryDates(DateOnly from, DateOnly to) =>
        _db.Query("SELECT date FROM diary_entries WHERE deleted = 0 AND plain <> '' AND date >= $f AND date <= $t;",
                r => DateOnly.ParseExact(r.S("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ("$f", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$t", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .ToHashSet();

    public void SaveDiary(DiaryEntry d)
    {
        _db.InTransaction((c, tx) =>
        {
            Upsert(c, tx, "diary_entries", d, "date, body, plain", "$date, $body, $plain",
                "date=excluded.date, body=excluded.body, plain=excluded.plain",
                ("$date", d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$body", d.BodyRtf), ("$plain", d.PlainText));
            Index(c, tx, "diary", d.Id, d.Date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture), d.PlainText);
        });
        Raise("diary");
    }

    // ---------------- Search (SET-05) ----------------

    private static void Unindex(SqliteConnection c, SqliteTransaction tx, Guid id)
    {
        using var cmd = Database.Command(c, tx, "DELETE FROM search_index WHERE ref_id = $id;", ("$id", id.ToString()));
        cmd.ExecuteNonQuery();
    }

    private static void Index(SqliteConnection c, SqliteTransaction tx, string kind, Guid id, string title, string body)
    {
        Unindex(c, tx, id);
        using var cmd = Database.Command(c, tx, "INSERT INTO search_index (kind, ref_id, title, body) VALUES ($k, $id, $t, $b);",
            ("$k", kind), ("$id", id.ToString()), ("$t", title), ("$b", body));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Prefix search across tasks (including archived), events, diary and attachment file names.</summary>
    public List<(string Kind, Guid Id, string Title, string Snippet)> Search(string query, int limit = 100)
    {
        var match = ToFtsQuery(query);
        if (match.Length == 0) return new();
        return _db.Query("SELECT kind, ref_id, title, snippet(search_index, 3, '[', ']', '…', 10) AS snip FROM search_index WHERE search_index MATCH $q ORDER BY rank LIMIT $n;",
            r => (r.S("kind"), r.G("ref_id"), r.S("title"), r.S("snip")), ("$q", match), ("$n", limit));
    }

    /// <summary>Turns user text into a safe FTS5 query: every word quoted and prefix-matched.</summary>
    public static string ToFtsQuery(string text)
    {
        var sb = new StringBuilder();
        foreach (var word in text.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = new string(word.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.').ToArray());
            if (clean.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(clean.Replace("\"", "")).Append("\"*");
        }
        return sb.ToString();
    }
}
