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
        // No lists are seeded any more; the app creates the first one during first run.
        _store.SaveList(new TodoList { Name = "Work", Color = TodoList.ColorFor(0), SortOrder = 0 });
    }

    private TodoList List => _store.DefaultList()!;

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) try { File.Delete(f); } catch { }
    }

    [Fact]
    public void New_database_has_no_lists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mytodo-empty-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(path);
            Assert.Empty(new DataStore(db).GetLists());
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void The_last_remaining_list_cannot_be_deleted()
    {
        var only = _store.DefaultList()!;
        _store.DeleteList(only.Id);
        Assert.Single(_store.GetLists());
    }

    [Fact]
    public void Task_with_tags_round_trips_and_logs_a_change()
    {
        var t = new TodoTask { Title = "Review deck", ListId = List.Id, Priority = 1, Tags = { "q4", "planning" }, DueUtc = DateTime.UtcNow.AddHours(3), DueHasTime = true };
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
        var t = new TodoTask { Title = "Weekly status", ListId = List.Id, DueUtc = due, DueHasTime = true, Rrule = "FREQ=WEEKLY" };
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
        var t = new TodoTask { Title = "Roadmap kickoff notes", ListId = List.Id, Status = TaskState.Completed, CompletedUtc = DateTime.UtcNow.AddDays(-31) };
        _store.SaveTask(t);
        Assert.Equal(1, _store.ArchiveCompleted(30));
        Assert.Single(_store.GetArchivedTasks());
        Assert.Contains(_store.Search("roadm"), h => h.Id == t.Id);
    }

    [Fact]
    public void A_task_runs_through_its_stages_and_the_timer_follows()
    {
        var t = new TodoTask { Title = "Draft the brief", ListId = List.Id };
        _store.SaveTask(t);
        Assert.Equal(TaskState.Open, t.Status);

        var start = DateTime.UtcNow.AddMinutes(-50);
        _store.StartTask(t, start);
        Assert.Equal(TaskState.InProgress, _store.GetTask(t.Id)!.Status);
        Assert.Equal(t.Id, _store.GetRunningTimer()!.TaskId);

        _store.HoldTask(t, start.AddMinutes(20));
        Assert.Equal(TaskState.Hold, _store.GetTask(t.Id)!.Status);
        Assert.Null(_store.GetRunningTimer());
        Assert.Equal(20, _store.MinutesSpent(t.Id));

        // Resuming adds a second stretch rather than restarting the count.
        _store.ResumeTask(t, start.AddMinutes(30));
        Assert.Equal(TaskState.InProgress, _store.GetTask(t.Id)!.Status);
        _store.CompleteTask(t, start.AddMinutes(45));
        Assert.Equal(TaskState.Completed, _store.GetTask(t.Id)!.Status);
        Assert.Null(_store.GetRunningTimer());
        Assert.Equal(35, _store.MinutesSpent(t.Id));
    }

    [Fact]
    public void Starting_a_second_task_puts_the_first_one_on_hold()
    {
        var a = new TodoTask { Title = "A", ListId = List.Id };
        var b = new TodoTask { Title = "B", ListId = List.Id };
        _store.SaveTask(a); _store.SaveTask(b);
        var start = DateTime.UtcNow.AddMinutes(-30);
        _store.StartTask(a, start);
        _store.StartTask(b, start.AddMinutes(15));
        Assert.Equal(TaskState.Hold, _store.GetTask(a.Id)!.Status);
        Assert.Equal(TaskState.InProgress, _store.GetTask(b.Id)!.Status);
        Assert.Equal(b.Id, _store.GetRunningTimer()!.TaskId);
        Assert.Equal(15, _store.MinutesSpent(a.Id));
    }

    [Fact]
    public void Reopening_an_in_progress_task_stops_its_timer()
    {
        var t = new TodoTask { Title = "Spike", ListId = List.Id };
        _store.SaveTask(t);
        var start = DateTime.UtcNow.AddMinutes(-10);
        _store.StartTask(t, start);
        _store.ReopenTask(t, start.AddMinutes(10));
        Assert.Equal(TaskState.Open, _store.GetTask(t.Id)!.Status);
        Assert.Null(_store.GetRunningTimer());
    }

    [Fact]
    public void A_deleted_task_stays_findable_for_the_board_and_can_be_restored()
    {
        var t = new TodoTask { Title = "Cancel the venue", ListId = List.Id, Status = TaskState.Hold };
        _store.SaveTask(t);
        var sub = new TodoTask { Title = "Email them", ListId = List.Id, ParentId = t.Id };
        _store.SaveTask(sub);

        _store.DeleteTask(t.Id);
        Assert.Null(_store.GetTask(t.Id));
        Assert.Contains(_store.GetDeletedTasks(), x => x.Id == t.Id);
        Assert.True(_store.FindTask(t.Id)!.Deleted);

        _store.RestoreTask(t.Id);
        var back = _store.GetTask(t.Id)!;
        Assert.False(back.Deleted);
        // The stage it was deleted in survives the round trip, and the subtask comes back with it.
        Assert.Equal(TaskState.Hold, back.Status);
        Assert.Single(_store.GetSubtasks(t.Id));
        Assert.Empty(_store.GetDeletedTasks());
    }

    [Fact]
    public void Saving_a_task_creates_the_tags_it_mentions()
    {
        var t = new TodoTask { Title = "Plan the quarter", ListId = List.Id, Tags = { "Q4", "#planning" } };
        _store.SaveTask(t);

        // Normalised to lowercase without the hash, and present in the master even though nothing
        // created them there by hand.
        Assert.Equal(new[] { "planning", "q4" }, _store.GetTags().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(1, _store.TagUsage()["q4"]);
    }

    [Fact]
    public void A_tag_can_exist_before_any_task_uses_it()
    {
        _store.SaveTag(new Tag { Name = "someday", Color = Tag.ColorFor(0) });
        Assert.Contains(_store.GetTags(), x => x.Name == "someday");
        Assert.False(_store.TagUsage().ContainsKey("someday"));
    }

    [Fact]
    public void Renaming_a_tag_moves_every_task_with_it()
    {
        var a = new TodoTask { Title = "A", ListId = List.Id, Tags = { "ops" } };
        var b = new TodoTask { Title = "B", ListId = List.Id, Tags = { "ops" } };
        _store.SaveTask(a); _store.SaveTask(b);

        var tag = _store.GetTags().Single(x => x.Name == "ops");
        _store.RenameTag(tag.Id, "Operations");

        Assert.Equal("operations", _store.GetTag(tag.Id)!.Name);
        Assert.All(new[] { a.Id, b.Id }, id => Assert.Contains("operations", _store.GetTask(id)!.Tags));
        Assert.DoesNotContain(_store.GetTags(), x => x.Name == "ops");
    }

    [Fact]
    public void Renaming_onto_an_existing_tag_merges_the_two()
    {
        var t = new TodoTask { Title = "A", ListId = List.Id, Tags = { "ops", "infra" } };
        _store.SaveTask(t);
        var ops = _store.GetTags().Single(x => x.Name == "ops");

        _store.RenameTag(ops.Id, "infra");

        // One tag left, and the task carries it once rather than twice.
        Assert.Single(_store.GetTags(), x => x.Name == "infra");
        Assert.Equal(new[] { "infra" }, _store.GetTask(t.Id)!.Tags);
    }

    [Fact]
    public void Deleting_a_tag_takes_it_off_tasks_but_keeps_them()
    {
        var t = new TodoTask { Title = "A", ListId = List.Id, Tags = { "ops", "infra" } };
        _store.SaveTask(t);
        var ops = _store.GetTags().Single(x => x.Name == "ops");

        _store.DeleteTag(ops.Id);

        Assert.NotNull(_store.GetTask(t.Id));
        Assert.Equal(new[] { "infra" }, _store.GetTask(t.Id)!.Tags);
        Assert.DoesNotContain(_store.GetTags(), x => x.Name == "ops");
    }

    [Fact]
    public void Tags_already_on_tasks_are_backfilled_into_the_master()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mytodo-tagmig-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new Database(path))
            {
                // Stand in for a database written before the tag master existed: a task_tags row with
                // no tag row behind it.
                var store = new DataStore(db);
                store.SaveList(new TodoList { Name = "Work" });
                var t = new TodoTask { Title = "Legacy", ListId = store.DefaultList()!.Id, Tags = { "legacy" } };
                store.SaveTask(t);
                db.Exec("DELETE FROM tags;");
                Assert.Empty(store.GetTags());
            }
            using (var db = new Database(path))
            {
                Assert.Contains(new DataStore(db).GetTags(), x => x.Name == "legacy");
            }
        }
        finally
        {
            foreach (var x in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(x); } catch { }
        }
    }

    [Fact]
    public void One_timer_runs_at_a_time()
    {
        var a = new TodoTask { Title = "A", ListId = List.Id };
        var b = new TodoTask { Title = "B", ListId = List.Id };
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
