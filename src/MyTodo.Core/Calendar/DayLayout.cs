namespace MyTodo.Core.Calendar;

public sealed record LaidOutBlock<T>(T Item, DateTime StartUtc, DateTime EndUtc, int Column, int ColumnCount, bool Overlaps);

/// <summary>
/// Side-by-side layout of overlapping blocks within one day column (section 9.2 of the requirements),
/// plus the 15-minute snapping used when moving and resizing (CAL-04).
/// </summary>
public static class DayLayout
{
    public static IReadOnlyList<LaidOutBlock<T>> Layout<T>(IEnumerable<(T item, DateTime start, DateTime end)> blocks)
    {
        var sorted = blocks.OrderBy(b => b.start).ThenByDescending(b => b.end - b.start).ToList();
        var result = new List<LaidOutBlock<T>>();
        var cluster = new List<(T item, DateTime start, DateTime end, int col)>();
        var clusterEnd = DateTime.MinValue;

        void Flush()
        {
            if (cluster.Count == 0) return;
            var cols = cluster.Max(c => c.col) + 1;
            foreach (var c in cluster)
                result.Add(new LaidOutBlock<T>(c.item, c.start, c.end, c.col, cols, cols > 1));
            cluster.Clear();
        }

        foreach (var b in sorted)
        {
            if (cluster.Count > 0 && b.start >= clusterEnd) Flush();
            var used = cluster.Where(c => c.end > b.start).Select(c => c.col).ToHashSet();
            var col = 0;
            while (used.Contains(col)) col++;
            cluster.Add((b.item, b.start, b.end, col));
            if (b.end > clusterEnd || cluster.Count == 1) clusterEnd = cluster.Max(c => c.end);
        }
        Flush();
        return result;
    }

    public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) => aStart < bEnd && bStart < aEnd;

    /// <summary>Round minutes-from-midnight to the nearest step (15 by default).</summary>
    public static int Snap(double minutes, int step = 15) => (int)(Math.Round(minutes / step) * step);
}
