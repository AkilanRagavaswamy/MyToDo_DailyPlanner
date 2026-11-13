using Microsoft.Data.Sqlite;
using MyTodo.Core.Models;
using MyTodo.Core.Recurrence;

namespace MyTodo.Data;

public sealed partial class DataStore
{
    // ---------------- Tasks ----------------

    private static TodoTask MapTask(SqliteDataReader r) => new()
    {
        Id = r.G("id"), ListId = r.G("list_id"), ParentId = r.GN("parent_id"), Title = r.S("title"), Notes = r.S("notes"),
        Priority = r.I("priority"), DueUtc = r.DN("due"), DueHasTime = r.B("due_has_time"), Status = (TaskState)r.I("status"),
        CompletedUtc = r.DN("completed"), ArchivedUtc = r.DN("archived"), SortOrder = r.I("sort_order"), Rrule = r.SN("rrule"),
        RepeatAfterCompletion = r.B("repeat_after"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    private List<TodoTask> QueryTasks(string where, params (string, object?)[] args)
    {
        var tasks = _db.Query($"SELECT * FROM tasks WHERE deleted = 0 AND {where} ORDER BY sort_order, created;", MapTask, args);
        if (tasks.Count == 0) return tasks;
        var byId = tasks.ToDictionary(t => t.Id);
        foreach (var (taskId, tag) in _db.Query("SELECT task_id, tag FROM task_tags;", r => (r.G("task_id"), r.S("tag"))))
            if (byId.TryGetValue(taskId, out var t)) t.Tags.Add(tag);
        return tasks;
    }

    /// <summary>All live (not deleted) top-level and sub tasks. 10,000 tasks load in a few milliseconds.</summary>
    public List<TodoTask> GetAllTasks(bool includeArchived = false) =>
        QueryTasks(includeArchived ? "1 = 1" : "archived IS NULL");

    public List<TodoTask> GetTasksInList(Guid listId) => QueryTasks("list_id = $l AND archived IS NULL", ("$l", listId.ToString()));
    public List<TodoTask> GetSubtasks(Guid parentId) => QueryTasks("parent_id = $p", ("$p", parentId.ToString()));
    public List<TodoTask> GetArchivedTasks() => QueryTasks("archived IS NOT NULL");
    public TodoTask? GetTask(Guid id) => QueryTasks("id = $id", ("$id", id.ToString())).FirstOrDefault();

    public List<TodoTask> GetCompletedBetween(DateTime fromUtc, DateTime toUtc) =>
        QueryTasks("status = 1 AND completed >= $f AND completed < $t", ("$f", Conv.Ms(fromUtc)), ("$t", Conv.Ms(toUtc)));

    public void SaveTask(TodoTask t) => SaveTask(t, raise: true);

    private void SaveTask(TodoTask t, bool raise)
    {
        _db.InTransaction((c, tx) =>
        {
            Upsert(c, tx, "tasks", t,
                "list_id, parent_id, title, notes, priority, due, due_has_time, status, completed, archived, sort_order, rrule, repeat_after",
                "$list, $parent, $title, $notes, $prio, $due, $dht, $status, $completed, $archived, $sort, $rrule, $rep",
                "list_id=excluded.list_id, parent_id=excluded.parent_id, title=excluded.title, notes=excluded.notes, priority=excluded.priority, due=excluded.due, due_has_time=excluded.due_has_time, status=excluded.status, completed=excluded.completed, archived=excluded.archived, sort_order=excluded.sort_order, rrule=excluded.rrule, repeat_after=excluded.repeat_after",
                ("$list", t.ListId.ToString()), ("$parent", t.ParentId?.ToString()), ("$title", t.Title), ("$notes", t.Notes),
                ("$prio", t.Priority), ("$due", Conv.Ms(t.DueUtc)), ("$dht", t.DueHasTime ? 1 : 0), ("$status", (int)t.Status),
                ("$completed", Conv.Ms(t.CompletedUtc)), ("$archived", Conv.Ms(t.ArchivedUtc)), ("$sort", t.SortOrder),
                ("$rrule", t.Rrule), ("$rep", t.RepeatAfterCompletion ? 1 : 0));

            using (var del = Database.Command(c, tx, "DELETE FROM task_tags WHERE task_id = $id;", ("$id", t.Id.ToString()))) del.ExecuteNonQuery();
            foreach (var tag in t.Tags.Select(x => x.Trim().TrimStart('#').ToLowerInvariant()).Where(x => x.Length > 0).Distinct())
                using (var ins = Database.Command(c, tx, "INSERT INTO task_tags (task_id, tag) VALUES ($id, $tag);", ("$id", t.Id.ToString()), ("$tag", tag)))
                    ins.ExecuteNonQuery();

            if (t.Deleted) Unindex(c, tx, t.Id);
            else Index(c, tx, "task", t.Id, t.Title, t.Notes + " " + string.Join(" ", t.Tags.Select(g => "#" + g)));
        });
        if (raise) Raise("tasks");
    }

    public void DeleteTask(Guid id)
    {
        var t = GetTask(id);
        if (t == null) return;
        foreach (var s in GetSubtasks(id)) { s.Deleted = true; SaveTask(s, raise: false); }
        t.Deleted = true;
        SaveTask(t, raise: false);
        SaveReminders(ReminderOwner.Task, id, new List<Reminder>());
        Raise("tasks");
    }

    /// <summary>
    /// Completes a task. A recurring task keeps a completed copy for history and Insights, and moves itself
    /// to its next occurrence (counted from the completion date when RepeatAfterCompletion is set).
    /// Returns the completed row.
    /// </summary>
    public TodoTask CompleteTask(TodoTask t, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (t.Rrule == null)
        {
            t.Status = TaskState.Completed;
            t.CompletedUtc = now;
            SaveTask(t);
            return t;
        }

        var done = new TodoTask
        {
            ListId = t.ListId, ParentId = t.ParentId, Title = t.Title, Notes = t.Notes, Priority = t.Priority,
            DueUtc = t.DueUtc, DueHasTime = t.DueHasTime, Status = TaskState.Completed, CompletedUtc = now, Tags = t.Tags.ToList()
        };
        SaveTask(done, raise: false);

        // Time logged so far belongs to the completed copy.
        foreach (var e in GetTimeEntries(t.Id)) { e.TaskId = done.Id; SaveTimeEntry(e, raise: false); }

        var rule = RecurrenceRule.Parse(t.Rrule);
        var anchor = t.DueUtc ?? now;
        DateTime? next = t.RepeatAfterCompletion
            ? now.Date.Add(anchor.TimeOfDay).AddDays(rule.Interval)
            : rule.Next(anchor, t.DueUtc is DateTime d && d > now ? d : now);
        if (next == null)
        {
            t.Status = TaskState.Completed;
            t.CompletedUtc = now;
        }
        else t.DueUtc = next;
        SaveTask(t);
        return done;
    }

    public void ReopenTask(TodoTask t)
    {
        t.Status = TaskState.Open;
        t.CompletedUtc = null;
        t.ArchivedUtc = null;
        SaveTask(t);
    }

    /// <summary>TSK-11: archive tasks completed more than <paramref name="days"/> days ago.</summary>
    public int ArchiveCompleted(int days, DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-days);
        var due = QueryTasks("status = 1 AND archived IS NULL AND completed < $c", ("$c", Conv.Ms(cutoff)));
        foreach (var t in due) { t.ArchivedUtc = DateTime.UtcNow; SaveTask(t, raise: false); }
        if (due.Count > 0) Raise("tasks");
        return due.Count;
    }

    // ---------------- Time entries (TSK-12) ----------------

    private static TimeEntry MapTime(SqliteDataReader r) => new()
    {
        Id = r.G("id"), TaskId = r.G("task_id"), StartUtc = r.D("start"), EndUtc = r.DN("end"), Minutes = r.I("minutes"),
        Source = (TimeSource)r.I("source"), EventId = r.GN("event_id"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    public List<TimeEntry> GetTimeEntries(Guid taskId) =>
        _db.Query("SELECT * FROM time_entries WHERE deleted = 0 AND task_id = $t ORDER BY start DESC;", MapTime, ("$t", taskId.ToString()));

    public List<TimeEntry> GetAllTimeEntries() => _db.Query("SELECT * FROM time_entries WHERE deleted = 0;", MapTime);

    public List<TimeEntry> GetTimeEntriesBetween(DateTime fromUtc, DateTime toUtc) =>
        _db.Query("SELECT * FROM time_entries WHERE deleted = 0 AND start >= $f AND start < $t;", MapTime, ("$f", Conv.Ms(fromUtc)), ("$t", Conv.Ms(toUtc)));

    public TimeEntry? GetRunningTimer() =>
        _db.Query("SELECT * FROM time_entries WHERE deleted = 0 AND source = 0 AND \"end\" IS NULL LIMIT 1;", MapTime).FirstOrDefault();

    public void SaveTimeEntry(TimeEntry e) => SaveTimeEntry(e, raise: true);

    private void SaveTimeEntry(TimeEntry e, bool raise)
    {
        _db.InTransaction((c, tx) => Upsert(c, tx, "time_entries", e, "task_id, start, \"end\", minutes, source, event_id",
            "$task, $start, $end, $min, $src, $ev",
            "task_id=excluded.task_id, start=excluded.start, \"end\"=excluded.\"end\", minutes=excluded.minutes, source=excluded.source, event_id=excluded.event_id",
            ("$task", e.TaskId.ToString()), ("$start", Conv.Ms(e.StartUtc)), ("$end", Conv.Ms(e.EndUtc)), ("$min", e.Minutes),
            ("$src", (int)e.Source), ("$ev", e.EventId?.ToString())));
        if (raise) Raise("time_entries");
    }

    public void DeleteTimeEntry(TimeEntry e) { e.Deleted = true; SaveTimeEntry(e); }

    /// <summary>Starts a timer on a task; any running timer is stopped first (one timer at a time).</summary>
    public TimeEntry StartTimer(Guid taskId, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        StopTimer(now);
        var e = new TimeEntry { TaskId = taskId, StartUtc = now, Source = TimeSource.Timer };
        SaveTimeEntry(e);
        return e;
    }

    public TimeEntry? StopTimer(DateTime? nowUtc = null)
    {
        var running = GetRunningTimer();
        if (running == null) return null;
        var now = nowUtc ?? DateTime.UtcNow;
        running.EndUtc = now;
        running.Minutes = Math.Max(1, (int)Math.Round((now - running.StartUtc).TotalMinutes));
        SaveTimeEntry(running);
        return running;
    }

    public int MinutesSpent(Guid taskId) => GetTimeEntries(taskId).Where(e => !e.IsRunning).Sum(e => e.Minutes);

    // ---------------- Attachments (TSK-09) ----------------

    private static Attachment MapAttachment(SqliteDataReader r) => new()
    {
        Id = r.G("id"), TaskId = r.G("task_id"), FileName = r.S("file_name"), StoredName = r.S("stored_name"), SizeBytes = r.L("size"),
        Sha256 = r.S("sha256"), MimeType = r.S("mime"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    public List<Attachment> GetAttachments(Guid taskId) =>
        _db.Query("SELECT * FROM attachments WHERE deleted = 0 AND task_id = $t ORDER BY created;", MapAttachment, ("$t", taskId.ToString()));

    public List<Attachment> GetAllAttachments() => _db.Query("SELECT * FROM attachments WHERE deleted = 0;", MapAttachment);

    public Attachment? GetAttachment(Guid id) =>
        _db.Query("SELECT * FROM attachments WHERE id = $id;", MapAttachment, ("$id", id.ToString())).FirstOrDefault();

    /// <summary>True when another live attachment still points at the same stored file.</summary>
    public bool IsStoredFileShared(string storedName, Guid exceptId) =>
        Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM attachments WHERE deleted = 0 AND stored_name = $s AND id <> $id;",
            ("$s", storedName), ("$id", exceptId.ToString()))) > 0;

    public void SaveAttachment(Attachment a)
    {
        _db.InTransaction((c, tx) =>
        {
            Upsert(c, tx, "attachments", a, "task_id, file_name, stored_name, size, sha256, mime", "$task, $name, $stored, $size, $sha, $mime",
                "task_id=excluded.task_id, file_name=excluded.file_name, stored_name=excluded.stored_name, size=excluded.size, sha256=excluded.sha256, mime=excluded.mime",
                ("$task", a.TaskId.ToString()), ("$name", a.FileName), ("$stored", a.StoredName), ("$size", a.SizeBytes), ("$sha", a.Sha256), ("$mime", a.MimeType));
            if (a.Deleted) Unindex(c, tx, a.Id);
            else Index(c, tx, "file", a.Id, a.FileName, "");
        });
        Raise("attachments");
    }
}
