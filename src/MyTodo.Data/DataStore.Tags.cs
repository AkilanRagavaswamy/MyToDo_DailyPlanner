using Microsoft.Data.Sqlite;
using MyTodo.Core.Models;

namespace MyTodo.Data;

/// <summary>
/// The tag master. A tag is a row here and a lowercase name in task_tags; the two are kept in step by
/// <see cref="EnsureTags"/>, which runs inside every task save, so a tag typed into quick-add shows up
/// in the master without a separate step.
/// </summary>
public sealed partial class DataStore
{
    private static Tag MapTag(SqliteDataReader r) => new()
    {
        Id = r.G("id"), Name = r.S("name"), Color = r.S("color"), SortOrder = r.I("sort_order"),
        CreatedUtc = r.D("created"), UpdatedUtc = r.D("updated"), Deleted = r.B("deleted"), Version = r.I("version")
    };

    public List<Tag> GetTags() =>
        _db.Query("SELECT * FROM tags WHERE deleted = 0 ORDER BY sort_order, name;", MapTag);

    public Tag? GetTag(Guid id) =>
        _db.Query("SELECT * FROM tags WHERE id = $id;", MapTag, ("$id", id.ToString())).FirstOrDefault();

    /// <summary>How many live tasks carry each tag, for the master's usage column.</summary>
    public Dictionary<string, int> TagUsage() =>
        _db.Query("SELECT tt.tag AS tag, COUNT(*) AS n FROM task_tags tt JOIN tasks t ON t.id = tt.task_id " +
                  "WHERE t.deleted = 0 GROUP BY tt.tag;", r => (r.S("tag"), r.I("n")))
           .ToDictionary(x => x.Item1, x => x.Item2);

    public void SaveTag(Tag tag)
    {
        tag.Name = Tag.Normalise(tag.Name);
        if (tag.Name.Length == 0) return;
        _db.InTransaction((c, tx) => Upsert(c, tx, "tags", tag, "name, color, sort_order", "$name, $color, $sort",
            "name=excluded.name, color=excluded.color, sort_order=excluded.sort_order",
            ("$name", tag.Name), ("$color", tag.Color), ("$sort", tag.SortOrder)));
        Raise("tags");
    }

    /// <summary>
    /// Renames a tag and every task that carries it, in one transaction. Merging into an existing name
    /// is allowed: the rows are rewritten and the duplicate assignments collapse, because task_tags has
    /// a composite primary key.
    /// </summary>
    public void RenameTag(Guid id, string newName)
    {
        var tag = GetTag(id);
        var name = Tag.Normalise(newName);
        if (tag == null || name.Length == 0 || name == tag.Name) return;

        var merging = GetTags().FirstOrDefault(t => t.Id != id && t.Name == name);
        _db.InTransaction((c, tx) =>
        {
            using (var cmd = Database.Command(c, tx, "UPDATE OR REPLACE task_tags SET tag = $new WHERE tag = $old;",
                       ("$new", name), ("$old", tag.Name))) cmd.ExecuteNonQuery();
            if (merging != null)
            {
                merging.Deleted = true;
                Upsert(c, tx, "tags", merging, "name, color, sort_order", "$name, $color, $sort",
                    "name=excluded.name, color=excluded.color, sort_order=excluded.sort_order",
                    ("$name", merging.Name), ("$color", merging.Color), ("$sort", merging.SortOrder));
            }
            tag.Name = name;
            Upsert(c, tx, "tags", tag, "name, color, sort_order", "$name, $color, $sort",
                "name=excluded.name, color=excluded.color, sort_order=excluded.sort_order",
                ("$name", tag.Name), ("$color", tag.Color), ("$sort", tag.SortOrder));
        });
        Raise("tags");
        Raise("tasks");
    }

    /// <summary>Deletes a tag and takes it off every task. The tasks themselves are untouched.</summary>
    public void DeleteTag(Guid id)
    {
        var tag = GetTag(id);
        if (tag == null) return;
        _db.InTransaction((c, tx) =>
        {
            using (var cmd = Database.Command(c, tx, "DELETE FROM task_tags WHERE tag = $t;", ("$t", tag.Name)))
                cmd.ExecuteNonQuery();
            tag.Deleted = true;
            Upsert(c, tx, "tags", tag, "name, color, sort_order", "$name, $color, $sort",
                "name=excluded.name, color=excluded.color, sort_order=excluded.sort_order",
                ("$name", tag.Name), ("$color", tag.Color), ("$sort", tag.SortOrder));
        });
        Raise("tags");
        Raise("tasks");
    }

    /// <summary>Creates the tag if it is new, and returns it either way.</summary>
    public Tag EnsureTag(string rawName)
    {
        var name = Tag.Normalise(rawName);
        var existing = GetTags().FirstOrDefault(t => t.Name == name);
        if (existing != null) return existing;
        var all = GetTags();
        var tag = new Tag { Name = name, Color = Tag.ColorFor(all.Count), SortOrder = all.Count };
        SaveTag(tag);
        return tag;
    }

    /// <summary>
    /// Adds master rows for any of <paramref name="names"/> that have none. Called inside the task-save
    /// transaction, so a tag can never be on a task without existing in the master.
    /// </summary>
    private void EnsureTags(SqliteConnection c, SqliteTransaction tx, IEnumerable<string> names)
    {
        var wanted = names.Select(Tag.Normalise).Where(x => x.Length > 0).Distinct().ToList();
        if (wanted.Count == 0) return;

        // Read through the caller's transaction. _db.Query opens its own command, which SQLite rejects
        // while a local transaction is pending on the same connection.
        var known = new HashSet<string>(StringComparer.Ordinal);
        using (var read = Database.Command(c, tx, "SELECT name FROM tags WHERE deleted = 0;"))
        using (var r = read.ExecuteReader())
            while (r.Read()) known.Add(r.GetString(0));

        var next = known.Count;
        foreach (var name in wanted)
        {
            if (!known.Add(name)) continue;
            var tag = new Tag { Name = name, Color = Tag.ColorFor(next), SortOrder = next };
            next++;
            Upsert(c, tx, "tags", tag, "name, color, sort_order", "$name, $color, $sort",
                "name=excluded.name, color=excluded.color, sort_order=excluded.sort_order",
                ("$name", tag.Name), ("$color", tag.Color), ("$sort", tag.SortOrder));
        }
    }

    /// <summary>
    /// Backfills the master from tags already on tasks. Databases written before the tag master existed
    /// have task_tags rows with nothing behind them; this gives each one a row, once, on first open.
    /// </summary>
    internal void SyncTagsFromTasks()
    {
        var orphans = _db.Query(
            "SELECT DISTINCT tt.tag AS tag FROM task_tags tt WHERE NOT EXISTS " +
            "(SELECT 1 FROM tags g WHERE g.deleted = 0 AND g.name = tt.tag);", r => r.S("tag"));
        if (orphans.Count == 0) return;
        _db.InTransaction((c, tx) => EnsureTags(c, tx, orphans));
        Raise("tags");
    }
}
