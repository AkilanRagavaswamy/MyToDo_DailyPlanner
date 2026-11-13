using Microsoft.Data.Sqlite;

namespace MyTodo.Data;

/// <summary>
/// One SQLite file in WAL mode. Every write commits immediately (no data loss on crash, section 5.2).
/// Access is serialised through a single connection and a lock; SQLite answers these queries in well under a millisecond.
/// </summary>
public sealed class Database : IDisposable
{
    public const int SchemaVersion = 1;
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public string Path { get; }

    public Database(string path)
    {
        Path = path;
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private };
        _conn = new SqliteConnection(builder.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public T Locked<T>(Func<SqliteConnection, T> work) { lock (_gate) return work(_conn); }
    public void Locked(Action<SqliteConnection> work) { lock (_gate) work(_conn); }

    /// <summary>Runs <paramref name="work"/> in one transaction.</summary>
    public void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            work(_conn, tx);
            tx.Commit();
        }
    }

    public int Exec(string sql, params (string name, object? value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(_conn, null, sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string name, object? value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(_conn, null, sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    public object? Scalar(string sql, params (string name, object? value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(_conn, null, sql, args);
            return cmd.ExecuteScalar();
        }
    }

    public static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string name, object? value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Migrate()
    {
        var current = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (current >= SchemaVersion) return;
        InTransaction((c, tx) =>
        {
            if (current < 1)
            {
                foreach (var sql in Schema.V1)
                    using (var cmd = Command(c, tx, sql)) cmd.ExecuteNonQuery();
            }
            using var v = Command(c, tx, $"PRAGMA user_version = {SchemaVersion};");
            v.ExecuteNonQuery();
        });
    }

    public void Dispose() => _conn.Dispose();
}

internal static class Schema
{
    // Common columns on every syncable table: id, created, updated (UTC ms), deleted (tombstone), version.
    private const string Common = "created INTEGER NOT NULL, updated INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0, version INTEGER NOT NULL DEFAULT 1";

    public static readonly string[] V1 =
    {
        $"CREATE TABLE lists (id TEXT PRIMARY KEY, name TEXT NOT NULL, color TEXT NOT NULL, sort_order INTEGER NOT NULL, is_inbox INTEGER NOT NULL DEFAULT 0, {Common});",
        $"CREATE TABLE tasks (id TEXT PRIMARY KEY, list_id TEXT NOT NULL, parent_id TEXT, title TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', priority INTEGER NOT NULL DEFAULT 0, due INTEGER, due_has_time INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 0, completed INTEGER, archived INTEGER, sort_order INTEGER NOT NULL DEFAULT 0, rrule TEXT, repeat_after INTEGER NOT NULL DEFAULT 0, {Common});",
        "CREATE INDEX ix_tasks_list ON tasks(list_id, status);",
        "CREATE INDEX ix_tasks_due ON tasks(due);",
        "CREATE TABLE task_tags (task_id TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY (task_id, tag));",
        $"CREATE TABLE events (id TEXT PRIMARY KEY, title TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '', start INTEGER NOT NULL, \"end\" INTEGER NOT NULL, all_day INTEGER NOT NULL DEFAULT 0, kind INTEGER NOT NULL DEFAULT 0, task_id TEXT, rrule TEXT, exceptions TEXT NOT NULL DEFAULT '', series_id TEXT, done INTEGER, {Common});",
        "CREATE INDEX ix_events_start ON events(start);",
        $"CREATE TABLE reminders (id TEXT PRIMARY KEY, owner_type INTEGER NOT NULL, owner_id TEXT NOT NULL, kind INTEGER NOT NULL, offset_min INTEGER NOT NULL DEFAULT 0, day_offset INTEGER NOT NULL DEFAULT 0, tod_min INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 1, {Common});",
        "CREATE INDEX ix_reminders_owner ON reminders(owner_id);",
        $"CREATE TABLE time_entries (id TEXT PRIMARY KEY, task_id TEXT NOT NULL, start INTEGER NOT NULL, \"end\" INTEGER, minutes INTEGER NOT NULL DEFAULT 0, source INTEGER NOT NULL, event_id TEXT, {Common});",
        "CREATE INDEX ix_time_task ON time_entries(task_id);",
        $"CREATE TABLE attachments (id TEXT PRIMARY KEY, task_id TEXT NOT NULL, file_name TEXT NOT NULL, stored_name TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT NOT NULL, mime TEXT NOT NULL DEFAULT '', {Common});",
        $"CREATE TABLE diary_entries (id TEXT PRIMARY KEY, date TEXT NOT NULL UNIQUE, body TEXT NOT NULL DEFAULT '', plain TEXT NOT NULL DEFAULT '', {Common});",
        "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);",
        // Outbox kept from 1.0.0.0 so 1.1.0.0 can sync existing data.
        "CREATE TABLE change_log (seq INTEGER PRIMARY KEY AUTOINCREMENT, tbl TEXT NOT NULL, row_id TEXT NOT NULL, op TEXT NOT NULL, version INTEGER NOT NULL, at INTEGER NOT NULL, synced INTEGER NOT NULL DEFAULT 0);",
        // One row per reminder occurrence that has been shown, snoozed, dismissed or completed.
        "CREATE TABLE reminder_log (key TEXT PRIMARY KEY, state INTEGER NOT NULL, at INTEGER NOT NULL);",
        "CREATE VIRTUAL TABLE search_index USING fts5(kind UNINDEXED, ref_id UNINDEXED, title, body, tokenize='unicode61 remove_diacritics 2');"
    };
}

internal static class Conv
{
    public static long? Ms(DateTime? d)
    {
        if (d is not DateTime v) return null;
        var u = v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc);
        return new DateTimeOffset(u).ToUnixTimeMilliseconds();
    }
    public static long Ms(DateTime d) => Ms((DateTime?)d)!.Value;
    public static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    public static DateTime? FromMs(object o) => o is DBNull or null ? null : FromMs(Convert.ToInt64(o));

    public static string S(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? "" : r.GetString(i); }
    public static string? SN(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }
    public static int I(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : r.GetInt32(i); }
    public static long L(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : r.GetInt64(i); }
    public static bool B(this SqliteDataReader r, string col) => r.I(col) != 0;
    public static Guid G(this SqliteDataReader r, string col) => Guid.Parse(r.S(col));
    public static Guid? GN(this SqliteDataReader r, string col) { var s = r.SN(col); return s == null ? null : Guid.Parse(s); }
    public static DateTime D(this SqliteDataReader r, string col) => FromMs(r.L(col));
    public static DateTime? DN(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : FromMs(r.GetInt64(i)); }
}
