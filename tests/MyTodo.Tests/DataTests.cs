using System.Reflection;
using MyTodo.Core.Models;
using MyTodo.Data;

namespace MyTodo.Tests;

public sealed class DataTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mytodo-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly DataStore _store;

    public DataTests()
    {
        _db = new Database(_path);
        _store = new DataStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) try { File.Delete(f); } catch { }
    }

    [Fact]
    public void New_database_has_inbox_work_and_personal() =>
        Assert.Equal(new[] { "Inbox", "Work", "Personal" }, _store.GetLists().Select(l => l.Name));

    [Fact]
    public void Task_with_tags_round_trips_and_logs_a_change()
    {
        var t = new TodoTask { Title = "Review deck", ListId = _store.Inbox().Id, Priority = 1, Tags = { "q4", "planning" }, DueUtc = DateTime.UtcNow.AddHours(3), DueHasTime = true };
        _store.SaveTask(t);
        var back = _store.GetTask(t.Id)!;
        Assert.Equal("Review deck", back.Title);
        Assert.Equal(new[] { "planning", "q4" }, back.Tags.OrderBy(x => x));
        Assert.True(Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM change_log WHERE row_id = $id;", ("$id", t.Id.ToString()))) >= 1);
    }

    [Fact]
    public void Completing_a_recurring_task_keeps_history_and_moves_the_due_date()
    {
        var due = DateTime.UtcNow.Date.AddDays(1).AddHours(9);
        var t = new TodoTask { Title = "Weekly status", ListId = _store.Inbox().Id, DueUtc = due, DueHasTime = true, Rrule = "FREQ=WEEKLY" };
        _store.SaveTask(t);
        var done = _store.CompleteTask(t);
        Assert.NotEqual(t.Id, done.Id);
        Assert.True(done.IsCompleted);
        Assert.Equal(due.AddDays(7), _store.GetTask(t.Id)!.DueUtc);
        Assert.False(_store.GetTask(t.Id)!.IsCompleted);
    }

    [Fact]
    public void Old_completed_tasks_are_archived_but_still_searchable()
    {
        var t = new TodoTask { Title = "Roadmap kickoff notes", ListId = _store.Inbox().Id, Status = TaskState.Completed, CompletedUtc = DateTime.UtcNow.AddDays(-31) };
        _store.SaveTask(t);
        Assert.Equal(1, _store.ArchiveCompleted(30));
        Assert.Single(_store.GetArchivedTasks());
        Assert.Contains(_store.Search("roadm"), h => h.Id == t.Id);
    }

    [Fact]
    public void One_timer_runs_at_a_time()
    {
        var a = new TodoTask { Title = "A", ListId = _store.Inbox().Id };
        var b = new TodoTask { Title = "B", ListId = _store.Inbox().Id };
        _store.SaveTask(a); _store.SaveTask(b);
        var start = DateTime.UtcNow.AddMinutes(-20);
        _store.StartTimer(a.Id, start);
        _store.StartTimer(b.Id, start.AddMinutes(10));
        Assert.Equal(b.Id, _store.GetRunningTimer()!.TaskId);
        Assert.Equal(10, _store.MinutesSpent(a.Id));
    }

    [Fact]
    public void Recurring_event_expands_and_skips_exceptions()
    {
        var start = DateTime.UtcNow.Date.AddHours(9);
        var e = new CalendarEvent { Title = "Stand-up", StartUtc = start, EndUtc = start.AddMinutes(15), Rrule = "FREQ=DAILY" };
        e.ExceptionsUtc.Add(start.AddDays(1));
        _store.SaveEvent(e);
        var occ = _store.GetOccurrences(start, start.AddDays(3));
        Assert.Equal(2, occ.Count);
    }

    [Fact]
    public void Diary_entry_is_found_by_search()
    {
        _store.SaveDiary(new DiaryEntry { Date = new DateOnly(2026, 11, 12), PlainText = "The morning went to the Q4 roadmap review." });
        Assert.Contains(_store.Search("roadmap"), h => h.Kind == "diary");
    }

    [Fact]
    public void Search_input_is_escaped() =>
        Assert.Equal("\"q4\"* \"road-map\"*", DataStore.ToFtsQuery("q4 \"road-map\" ;"));

    [Fact]
    public void Settings_keep_the_agreed_defaults()
    {
        var s = _store.LoadSettings();
        Assert.Equal(new[] { 60, 30, 15 }, s.DefaultOffsetsMinutes);
        Assert.Equal(30, s.ArchiveAfterDays);
        s.ArchiveAfterDays = 14;
        _store.SaveSettings(s);
        Assert.Equal(14, _store.LoadSettings().ArchiveAfterDays);
        Assert.Equal(2, s.DateOnlyReminders.Count);
    }

    [Fact]
    public void Export_contains_every_table()
    {
        var json = _store.ExportJson();
        foreach (var key in new[] { "\"lists\"", "\"tasks\"", "\"events\"", "\"diary\"", "\"attachments\"" }) Assert.Contains(key, json);
    }

    /// <summary>1.0.0.0 makes no network calls: neither library may reference System.Net.Http.</summary>
    [Fact]
    public void Core_and_data_do_not_reference_http()
    {
        foreach (var asm in new[] { typeof(TodoTask).Assembly, typeof(DataStore).Assembly })
            Assert.DoesNotContain(asm.GetReferencedAssemblies(), a => a.Name != null && a.Name.StartsWith("System.Net.Http", StringComparison.Ordinal));
    }
}
