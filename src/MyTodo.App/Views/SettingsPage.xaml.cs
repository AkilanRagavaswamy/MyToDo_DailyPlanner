using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Export;
using MyTodo.Core.Models;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace MyTodo.App.Views;

/// <summary>
/// All 1.0.0.0 settings with their defaults (SET-04). Account and sync and Backup sections arrive in 1.1.0.0.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly AppServices _s = App.Services;
    private AppSettings S => _s.Settings;

    public SettingsPage()
    {
        InitializeComponent();
        Build();
    }

    private void Changed(bool reschedule = true)
    {
        _s.SaveSettings();
        if (reschedule) _s.Reminders.Reschedule();
    }

    private void Note(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    // ---------------- building blocks ----------------

    private StackPanel Section(string title)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        Sections.Children.Add(panel);
        return panel;
    }

    private static void Row(StackPanel section, string label, string description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0) text.Children.Add(new TextBlock { Text = description, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
        control.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        section.Children.Add(new Border { Style = (Style)Application.Current.Resources["SettingsRowStyle"], Child = grid });
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsOn = value, MinWidth = 0, OnContent = "On", OffContent = "Off" };
        t.Toggled += (_, _) => set(t.IsOn);
        return t;
    }

    private static ComboBox Combo<T>(IEnumerable<(string label, T value)> options, T current, Action<T> set)
    {
        var items = options.ToList();
        var c = new ComboBox { MinWidth = 200, ItemsSource = items.Select(o => o.label).ToList() };
        c.SelectedIndex = Math.Max(0, items.FindIndex(o => EqualityComparer<T>.Default.Equals(o.value, current)));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(items[c.SelectedIndex].value); };
        return c;
    }

    private static TimePicker Time(int minutes, Action<int> set)
    {
        var p = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 15, SelectedTime = TimeSpan.FromMinutes(minutes) };
        p.SelectedTimeChanged += (_, e) => { if (e.NewTime is TimeSpan t) set((int)t.TotalMinutes); };
        return p;
    }

    private static Button ActionButton(string text, Func<Task> action)
    {
        var b = new Button { Content = text };
        b.Click += async (_, _) => await action();
        return b;
    }

    // ---------------- sections ----------------

    private void Build()
    {
        var rem = Section("Reminders and notifications");
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (label, minutes) in new[] { ("1 hour", 60), ("30 min", 30), ("15 min", 15) })
        {
            var chip = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = label, IsChecked = S.DefaultOffsetsMinutes.Contains(minutes), CornerRadius = new CornerRadius(12) };
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked == true) { if (!S.DefaultOffsetsMinutes.Contains(minutes)) S.DefaultOffsetsMinutes.Add(minutes); }
                else S.DefaultOffsetsMinutes.Remove(minutes);
                Changed(false);
            };
            chips.Children.Add(chip);
        }
        Row(rem, "Timed tasks and events", "Default reminders before the start time, for new items", chips);
        var dateOnly = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dayBefore = S.DateOnlyReminders.FirstOrDefault(d => d.DayOffset == -1) ?? new DateOnlyReminderSetting { DayOffset = -1, Minutes = 900 };
        var onDay = S.DateOnlyReminders.FirstOrDefault(d => d.DayOffset == 0) ?? new DateOnlyReminderSetting { DayOffset = 0, Minutes = 540 };
        dateOnly.Children.Add(new TextBlock { Text = "Day before", VerticalAlignment = VerticalAlignment.Center });
        dateOnly.Children.Add(Time(dayBefore.Minutes, m => { dayBefore.Minutes = m; SaveDateOnly(dayBefore, onDay); }));
        dateOnly.Children.Add(new TextBlock { Text = "On the day", VerticalAlignment = VerticalAlignment.Center });
        dateOnly.Children.Add(Time(onDay.Minutes, m => { onDay.Minutes = m; SaveDateOnly(dayBefore, onDay); }));
        Row(rem, "Tasks with a date but no time", "Defaults: 3:00 PM the day before and 9:00 AM on the day", dateOnly);
        Row(rem, "Reminder sound", "", Toggle(S.ReminderSound, v => { S.ReminderSound = v; Changed(); }));
        Row(rem, "Sound", "", Combo(new[]
        {
            ("Windows Notify (default)", "ms-winsoundevent:Notification.Default"), ("Reminder", "ms-winsoundevent:Notification.Reminder"),
            ("Mail", "ms-winsoundevent:Notification.Mail"), ("Instant message", "ms-winsoundevent:Notification.IM"), ("Text message", "ms-winsoundevent:Notification.SMS")
        }, S.ReminderSoundUri, v => { S.ReminderSoundUri = v; Changed(); }));
        Row(rem, "Snooze options", "Offered on every reminder", new TextBlock { Text = "5 min, 15 min, 1 hour, tomorrow 9:00 AM", Foreground = Ui.Brush("Text2Brush") });
        var planning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        planning.Children.Add(Time(S.DailyPlanningMinutes, m => { S.DailyPlanningMinutes = m; Changed(); }));
        planning.Children.Add(Toggle(S.DailyPlanningPrompt, v => { S.DailyPlanningPrompt = v; Changed(); }));
        Row(rem, "Daily planning prompt", "Every weekday", planning);
        Row(rem, "Windows notification settings", "Focus and Do Not Disturb are respected", ActionButton("Open Windows settings", async () => { await Launcher.LaunchUriAsync(new Uri("ms-settings:notifications")); }));

        var look = Section("Appearance");
        var themes = new RadioButtons { MaxColumns = 3 };
        foreach (var t in new[] { "Light", "Dark", "Use system setting" }) themes.Items.Add(t);
        themes.SelectedIndex = S.Theme switch { ThemeChoice.Light => 0, ThemeChoice.Dark => 1, _ => 2 };
        themes.SelectionChanged += (_, _) =>
        {
            S.Theme = themes.SelectedIndex switch { 0 => ThemeChoice.Light, 1 => ThemeChoice.Dark, _ => ThemeChoice.System };
            Changed(false);
            if (App.Instance?.MainWindow?.Content is FrameworkElement root) ThemeService.Apply(root, S.Theme);
        };
        Row(look, "App theme", "", themes);
        Row(look, "Accent color", "Applies after the app restarts", Combo(new[] { ("Planner green", false), ("Windows accent color", true) }, S.UseWindowsAccent, v =>
        {
            S.UseWindowsAccent = v;
            Changed(false);
            Note("The new accent color appears the next time the app starts (Ctrl+Q, then open it again).");
        }));
        Row(look, "Text size", "Follows the Windows text size setting", ActionButton("Open Windows settings", async () => { await Launcher.LaunchUriAsync(new Uri("ms-settings:easeofaccess-display")); }));

        var cal = Section("Calendar");
        var hours = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        hours.Children.Add(Time(S.WorkStartMinutes, m => { S.WorkStartMinutes = m; Changed(false); }));
        hours.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center });
        hours.Children.Add(Time(S.WorkEndMinutes, m => { S.WorkEndMinutes = m; Changed(false); }));
        Row(cal, "Working hours", "Monday to Friday", hours);
        Row(cal, "First day of the week", "", Combo(new[] { ("Monday", DayOfWeek.Monday), ("Sunday", DayOfWeek.Sunday), ("Saturday", DayOfWeek.Saturday) }, S.FirstDayOfWeek, v => { S.FirstDayOfWeek = v; Changed(false); }));
        Row(cal, "Length of a new time block", "When you drag a task onto the calendar", Combo(new[] { ("15 minutes", 15), ("30 minutes", 30), ("45 minutes", 45), ("1 hour", 60), ("2 hours", 120) }, S.DefaultBlockMinutes, v => { S.DefaultBlockMinutes = v; Changed(false); }));
        Row(cal, "Show weekends in week view", "", Toggle(S.ShowWeekends, v => { S.ShowWeekends = v; Changed(false); }));

        var tasks = Section("Tasks and attachments");
        Row(tasks, "Archive completed tasks", "Archived tasks stay searchable and can be restored", Combo(new[] { ("After 7 days", 7), ("After 14 days", 14), ("After 30 days", 30), ("After 60 days", 60), ("After 90 days", 90) }, S.ArchiveAfterDays, v =>
        {
            S.ArchiveAfterDays = v;
            Changed(false);
            _s.Store.ArchiveCompleted(v);
        }));
        Row(tasks, "Attachment size limit", "Larger files are rejected with a message", new TextBlock { Text = "10 MB per file", Foreground = Ui.Brush("Text2Brush") });
        Row(tasks, "Deleted items", "Kept as hidden records so a later sync can remove them everywhere", new TextBlock { Text = "Removed 30 days after sync (from 1.1.0.0)", Foreground = Ui.Brush("Text2Brush") });

        var start = Section("Startup and tray");
        Row(start, "Launch at Windows startup", "So reminders arrive even if you forget to open the app", Toggle(S.LaunchAtStartup, async v =>
        {
            var error = await StartupService.SetEnabledAsync(v);
            if (error != null) Note(error, InfoBarSeverity.Warning);
            S.LaunchAtStartup = v && error == null;
            Changed(false);
        }));
        Row(start, "Start minimized to the tray", "", Toggle(S.StartMinimized, v => { S.StartMinimized = v; Changed(false); }));
        Row(start, "Close button keeps the app in the tray", "Reminders keep working either way", Toggle(S.CloseToTray, v => { S.CloseToTray = v; Changed(false); }));
        Row(start, "Quick add shortcut", "Works from anywhere in Windows", Combo(AppSettings.HotkeyChoices.Select(h => (h.Replace("+", " + "), h)), S.QuickAddHotkey, v =>
        {
            if (App.Instance!.Hotkey.Register(v)) { S.QuickAddHotkey = v; Changed(false); Note($"Quick add now opens with {v}."); }
            else
            {
                Note($"{v} is used by Windows or another app. Pick another shortcut.", InfoBarSeverity.Warning);
                App.Instance.Hotkey.Register(S.QuickAddHotkey);
            }
        }));
        var link = new HyperlinkButton { Content = "View all shortcuts" };
        link.Click += (_, _) => App.Instance!.MainWindow!.Navigate(typeof(ShortcutsPage), null);
        Row(start, "Keyboard shortcuts", "", link);

        var data = Section("Your data");
        var exports = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        exports.Children.Add(ActionButton("Export JSON", () => SaveTextAsync("My To-Do export", ".json", "JSON", _s.Store.ExportJson())));
        exports.Children.Add(ActionButton("Export calendar (.ics)", () => SaveTextAsync("My To-Do calendar", ".ics", "iCalendar", IcsWriter.Write(_s.Store.GetAllEvents()))));
        Row(data, "Export", "All data as JSON, calendar as .ics. Backup and restore arrive in 1.1.0.0.", exports);

        var about = Section("About");
        Row(about, "Version", "", new TextBlock { Text = "1.0.0.0", Foreground = Ui.Brush("Text2Brush") });
        Row(about, "Data collection", "None: no telemetry, analytics or ads. This version makes no network connections.", new TextBlock { Text = "" });
        Row(about, "Log file", "A local log, only for troubleshooting", ActionButton("Open log folder", async () =>
        {
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("logs", CreationCollisionOption.OpenIfExists);
            await Launcher.LaunchFolderAsync(folder);
        }));
    }

    private void SaveDateOnly(DateOnlyReminderSetting dayBefore, DateOnlyReminderSetting onDay)
    {
        S.DateOnlyReminders = new List<DateOnlyReminderSetting> { dayBefore, onDay };
        Changed(false);
    }

    private async Task SaveTextAsync(string name, string ext, string kind, string content)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"{name} {DateTime.Now:yyyy-MM-dd}" };
        picker.FileTypeChoices.Add(kind, new List<string> { ext });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Instance!.MainWindow!.Hwnd);
        if (await picker.PickSaveFileAsync() is not StorageFile file) return;
        await FileIO.WriteTextAsync(file, content);
        Note($"Saved {file.Name}.", InfoBarSeverity.Success);
    }
}
