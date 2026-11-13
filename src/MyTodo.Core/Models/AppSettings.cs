namespace MyTodo.Core.Models;

public enum ThemeChoice { System = 0, Light = 1, Dark = 2 }

public sealed class DateOnlyReminderSetting
{
    public int DayOffset { get; set; }
    public int Minutes { get; set; }
}

/// <summary>All user settings with the agreed 1.0.0.0 defaults (decisions table, requirements doc section 12).</summary>
public sealed class AppSettings
{
    public bool FirstRunDone { get; set; }
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public bool UseWindowsAccent { get; set; }

    // Reminders
    public List<int> DefaultOffsetsMinutes { get; set; } = new() { 60, 30, 15 };
    public List<DateOnlyReminderSetting> DateOnlyReminders { get; set; } = new()
    {
        new() { DayOffset = -1, Minutes = 15 * 60 },
        new() { DayOffset = 0, Minutes = 9 * 60 }
    };
    public bool ReminderSound { get; set; } = true;
    public string ReminderSoundUri { get; set; } = "ms-winsoundevent:Notification.Default";
    public bool DailyPlanningPrompt { get; set; } = true;
    public int DailyPlanningMinutes { get; set; } = 9 * 60;
    public List<int> SnoozeMinutes { get; set; } = new() { 5, 15, 60 };

    // Calendar
    public int WorkStartMinutes { get; set; } = 9 * 60;
    public int WorkEndMinutes { get; set; } = 18 * 60;
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;
    public int DefaultBlockMinutes { get; set; } = 30;
    public bool ShowWeekends { get; set; } = true;

    // Tasks
    public int ArchiveAfterDays { get; set; } = 30;
    public int PurgeDeletedAfterDays { get; set; } = 30;

    // Startup and tray
    public bool LaunchAtStartup { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>"Win+Shift+T" (default) or "Ctrl+Alt+T", "Ctrl+Shift+Space".</summary>
    public string QuickAddHotkey { get; set; } = "Win+Shift+T";

    // Internal bookkeeping
    public DateTime? LastTickUtc { get; set; }
    public DateTime? LastArchiveRunUtc { get; set; }

    public static readonly string[] HotkeyChoices = { "Win+Shift+T", "Ctrl+Alt+T", "Ctrl+Shift+Space" };
}
