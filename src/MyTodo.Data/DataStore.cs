using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyTodo.Core.Models;

namespace MyTodo.Data;

/// <summary>
/// All reads and writes. Every write bumps updated/version, records a change_log row (for 1.1.0.0 sync)
/// and refreshes the search index, in one transaction.
/// </summary>
public sealed partial class DataStore
{
    private readonly Database _db;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Raised after a write, with the table name. The app refreshes views and reminders from it.</summary>
    public event Action<string>? Changed;

    public DataStore(Database db)
    {
        _db = db;
        EnsureInbox();
    }

    public Database Database => _db;

    private static (string, object?)[] Common(Entity e) =>
    [
        ("$id", e.Id.ToString()), ("$created", Conv.Ms(e.CreatedUtc)), ("$updated", Conv.Ms(e.UpdatedUtc)),
        ("$deleted", e.Deleted ? 1 : 0), ("$version", e.Version)
    ];

    private const string CommonCols = "id, created, updated, deleted, version";
    private const string CommonVals = "$id, $created, $updated, $deleted, $version";
    private const string CommonSet = "updated=excluded.updated, deleted=excluded.deleted, version=excluded.version";

    private void Upsert(SqliteConnection c, SqliteTransaction tx, string table, Entity e, string cols, string vals, string set, params (string, object?)[] fields)
    {
        e.UpdatedUtc = DateTime.UtcNow;
        e.Version++;
        var sql = $"INSERT INTO {table} ({CommonCols}, {cols}) VALUES ({CommonVals}, {vals}) ON CONFLICT(id) DO UPDATE SET {CommonSet}, {set};";
        using (var cmd = Database.Command(c, tx, sql, Common(e).Concat(fields).ToArray())) cmd.ExecuteNonQuery();
        LogChange(c, tx, table, e.Id, e.Deleted ? "delete" : "upsert", e.Version);
    }

    private static void LogChange(SqliteConnection c, SqliteTransaction tx, string table, Guid id, string op, int version)
    {
        using var cmd = Database.Command(c, tx, "INSERT INTO change_log (tbl, row_id, op, version, at) VALUES ($t, $r, $o, $v, $a);",
            ("$t", table), ("$r", id.ToString()), ("$o", op), ("$v", version), ("$a", Conv.Ms(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
    }

    private void Raise(string table) => Changed?.Invoke(table);

    // ---------------- Lists ----------------

    private void EnsureInbox()
    {
        if (Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM lists WHERE is_inbox = 1 AND deleted = 0;")) > 0) return;
        SaveList(new TodoList { Name = "Inbox", Color = "#7C8A82", IsInbox = true, SortOrder = 0 });
        SaveList(new TodoList { Name = "Work", Color = "#1F8A5B", SortOrder = 1 });
        SaveList(new TodoList { Name = "Personal", Color = "#F4A62A", SortOrder = 2 });
    }

    public List<TodoList> GetLists() =>
        _db.Query("SELECT * FROM lists WHERE deleted = 0 ORDER BY is_inbox DESC, sort_order, name;", MapList);

    public TodoList Inbox() => GetLists().First(l => l.IsInbox);

    private static TodoList MapList(SqliteDataReader r) => new()
    {
        Id = r.G("id"), Name = r.S("name"), Color = r.S("color"), SortOrder = r.I("sort_order"), IsInbox = r.B("is_inbox"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    public void SaveList(TodoList l)
    {
        _db.InTransaction((c, tx) => Upsert(c, tx, "lists", l, "name, color, sort_order, is_inbox", "$name, $color, $sort, $inbox",
            "name=excluded.name, color=excluded.color, sort_order=excluded.sort_order, is_inbox=excluded.is_inbox",
            ("$name", l.Name), ("$color", l.Color), ("$sort", l.SortOrder), ("$inbox", l.IsInbox ? 1 : 0)));
        Raise("lists");
    }

    /// <summary>Deletes a list and its tasks (soft delete; tombstones kept for 1.1.0.0 sync).</summary>
    public void DeleteList(Guid id)
    {
        var list = GetLists().FirstOrDefault(l => l.Id == id);
        if (list == null || list.IsInbox) return;
        foreach (var t in GetTasksInList(id)) { t.Deleted = true; SaveTask(t, raise: false); }
        list.Deleted = true;
        SaveList(list);
        Raise("tasks");
    }

    // ---------------- Settings ----------------

    public AppSettings LoadSettings()
    {
        var json = _db.Scalar("SELECT value FROM settings WHERE key = 'app';") as string;
        if (string.IsNullOrEmpty(json)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings(); }
        catch (JsonException) { return new AppSettings(); }
    }

    public void SaveSettings(AppSettings s)
    {
        _db.Exec("INSERT INTO settings (key, value) VALUES ('app', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            ("$v", JsonSerializer.Serialize(s, Json)));
        Raise("settings");
    }

    /// <summary>Small app-internal values (snoozes, window size) outside the user settings object.</summary>
    public string? GetValue(string key) => _db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", key)) as string;

    public void SetValue(string key, string value) =>
        _db.Exec("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", ("$k", key), ("$v", value));

    // ---------------- Export (SET-03) ----------------

    public string ExportJson()
    {
        var payload = new
        {
            format = "my-todo-export",
            version = 1,
            exportedUtc = DateTime.UtcNow,
            lists = GetLists(),
            tasks = GetAllTasks(includeArchived: true),
            events = GetAllEvents(),
            reminders = GetAllReminders(),
            timeEntries = GetAllTimeEntries(),
            attachments = GetAllAttachments(),
            diary = GetAllDiary(),
            settings = LoadSettings()
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
