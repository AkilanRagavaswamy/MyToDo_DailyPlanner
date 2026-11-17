namespace MyTodo.Core.Models;

/// <summary>Columns every syncable row carries (requirements doc, section 8), kept in 1.0.0.0 so 1.1.0.0 can sync existing data.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class TodoList : Entity
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#1F8A5B";
    public int SortOrder { get; set; }

    /// <summary>Colours handed out to new lists, in order, so the first few lists look distinct.</summary>
    public static readonly string[] Palette = { "#1F8A5B", "#4A7FD6", "#F4A62A", "#B45AC7", "#D9534F", "#2AA6A6" };

    public static string ColorFor(int index) => Palette[index % Palette.Length];
}

/// <summary>
/// The stages a task moves through: Open → In progress → (On hold → In progress) → Completed.
/// Open and Completed keep their 1.0.0.0 numbers, so rows written by the first release and the
/// "status = 1" queries in the data layer keep their meaning; the two new stages were appended.
/// </summary>
public enum TaskState { Open = 0, Completed = 1, InProgress = 2, Hold = 3 }

/// <summary>
/// A tag in the tag master. Tags used to exist only where a task mentioned one; they are now rows of
/// their own, so one can be created, renamed or recoloured before anything uses it. The link table
/// still stores the lowercase name, which keeps every existing query and the quick-add parser working.
/// </summary>
public sealed class Tag : Entity
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4A7FD6";
    public int SortOrder { get; set; }

    /// <summary>Tag names are matched and stored lowercase, so "#Q4" and "#q4" are one tag.</summary>
    public static string Normalise(string raw) => raw.Trim().TrimStart('#').Trim().ToLowerInvariant();

    public static readonly string[] Palette = { "#4A7FD6", "#1F8A5B", "#F4A62A", "#B45AC7", "#D9534F", "#2AA6A6", "#7A6FF0" };

    public static string ColorFor(int index) => Palette[index % Palette.Length];
}

public sealed class TodoTask : Entity
{
    public Guid ListId { get; set; }
    public Guid? ParentId { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>0 = none, 1 = P1 (urgent) â¦ 4 = P4.</summary>
    public int Priority { get; set; }
    public DateTime? DueUtc { get; set; }
    public bool DueHasTime { get; set; }
    public TaskState Status { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? ArchivedUtc { get; set; }
    public int SortOrder { get; set; }
    /// <summary>RFC 5545 RRULE text (without the "RRULE:" prefix), or null.</summary>
    public string? Rrule { get; set; }
    /// <summary>When true the next occurrence counts from the completion date.</summary>
    public bool RepeatAfterCompletion { get; set; }
    public List<string> Tags { get; set; } = new();

    public bool IsCompleted => Status == TaskState.Completed;
    public bool IsInProgress => Status == TaskState.InProgress;
    public bool IsOnHold => Status == TaskState.Hold;
    /// <summary>Open, In progress or On hold: work still outstanding. Every "not done" filter uses this.</summary>
    public bool IsActive => Status != TaskState.Completed;
    public bool IsArchived => ArchivedUtc != null;

    /// <summary>The stage name shown on a row or board card.</summary>
    public string StageText => Status switch
    {
        TaskState.InProgress => "In progress",
        TaskState.Hold => "On hold",
        TaskState.Completed => "Completed",
        _ => "Open"
    };
}

public enum EventKind { Meeting = 0, Focus = 1, Personal = 2 }

public sealed class CalendarEvent : Entity
{
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public bool AllDay { get; set; }
    public EventKind Kind { get; set; }
    /// <summary>Set when this event is a time block for a task.</summary>
    public Guid? TaskId { get; set; }
    public string? Rrule { get; set; }
    /// <summary>Occurrence starts (UTC) removed from a recurring series.</summary>
    public List<DateTime> ExceptionsUtc { get; set; } = new();
    /// <summary>For a one-off edit of a series occurrence: the series it came from.</summary>
    public Guid? SeriesId { get; set; }
    public DateTime? DoneUtc { get; set; }

    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>One expanded occurrence of an event (a recurring series yields many).</summary>
public sealed record EventOccurrence(CalendarEvent Event, DateTime StartUtc, DateTime EndUtc)
{
    public bool IsRecurring => Event.Rrule != null;
}

public enum ReminderOwner { Task = 0, Event = 1 }
public enum ReminderKind { BeforeStart = 0, DateOnlyAt = 1, AtStart = 2 }

public sealed class Reminder : Entity
{
    public ReminderOwner OwnerType { get; set; }
    public Guid OwnerId { get; set; }
    public ReminderKind Kind { get; set; }
    /// <summary>BeforeStart: minutes before the start or due time.</summary>
    public int OffsetMinutes { get; set; }
    /// <summary>DateOnlyAt: days relative to the due date (-1 = day before).</summary>
    public int DayOffset { get; set; }
    /// <summary>DateOnlyAt: local minutes after midnight (900 = 3:00 PM).</summary>
    public int TimeOfDayMinutes { get; set; }
    public bool Enabled { get; set; } = true;

    public string Label => Kind switch
    {
        ReminderKind.AtStart => "At start time",
        ReminderKind.BeforeStart when OffsetMinutes >= 60 && OffsetMinutes % 60 == 0 =>
            OffsetMinutes == 60 ? "1 hour before" : $"{OffsetMinutes / 60} hours before",
        ReminderKind.BeforeStart => $"{OffsetMinutes} min before",
        _ => (DayOffset == 0 ? "On the day" : DayOffset == -1 ? "Day before" : $"{-DayOffset} days before")
             + ", " + DateTime.Today.AddMinutes(TimeOfDayMinutes).ToString("h:mm tt")
    };
}

public enum TimeSource { Timer = 0, Block = 1, Manual = 2 }

public sealed class TimeEntry : Entity
{
    public Guid TaskId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public int Minutes { get; set; }
    public TimeSource Source { get; set; }
    /// <summary>For Block entries: the event that produced it.</summary>
    public Guid? EventId { get; set; }

    public bool IsRunning => Source == TimeSource.Timer && EndUtc == null;
}

public sealed class Attachment : Entity
{
    public const long MaxBytes = 10L * 1024 * 1024;
    public Guid TaskId { get; set; }
    public string FileName { get; set; } = "";
    public string StoredName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string MimeType { get; set; } = "";

    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";
}

public sealed class DiaryEntry : Entity
{
    public DateOnly Date { get; set; }
    public string BodyRtf { get; set; } = "";
    public string PlainText { get; set; } = "";
}

public sealed record SearchHit(string Kind, Guid RefId, string Title, string Snippet, DateTime? WhenUtc);
