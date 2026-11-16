using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.Core.Insights;
using MyTodo.Core.Models;
using MyTodo.Core.Recurrence;
using MyTodo.Core.Reminders;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace MyTodo.App.Views;

/// <summary>Everything about one task: fields, reminders, subtasks, attachments, time, notes (screen 2 of the designs).</summary>
public sealed partial class TaskDetailPanel : UserControl
{
    private readonly AppServices _s = App.Services;
    private TodoTask? _task;
    private bool _loading;
    private List<(string Label, string? Rrule, bool AfterCompletion)> _repeatOptions = new();
    /// <summary>The task's tags while the panel is open. Chips and the picker edit this, then save.</summary>
    private readonly List<string> _tags = new();

    public event Action? CloseRequested;
    public event Action? TaskChanged;

    public TodoTask? Task => _task;

    public TaskDetailPanel()
    {
        InitializeComponent();
        _s.Timer.Tick += () => { if (_task != null && _s.Timer.Running?.TaskId == _task.Id) TimerElapsed.Text = _s.Timer.ElapsedText; };
        _s.Timer.Changed += () => { if (_task != null) LoadTime(); };

        void Acc(VirtualKey key, VirtualKeyModifiers mods, Action a)
        {
            var k = new KeyboardAccelerator { Key = key, Modifiers = mods };
            k.Invoked += (_, e) => { a(); e.Handled = true; };
            KeyboardAccelerators.Add(k);
        }
        Acc(VirtualKey.Escape, VirtualKeyModifiers.None, () => CloseRequested?.Invoke());
        Acc(VirtualKey.Enter, VirtualKeyModifiers.Control, ToggleDone);
        Acc(VirtualKey.D, VirtualKeyModifiers.Control, () => { DuePicker.Focus(FocusState.Keyboard); DuePicker.IsCalendarOpen = true; });
        Acc(VirtualKey.T, VirtualKeyModifiers.Control, () => Timer_Click(this, new RoutedEventArgs()));
        Acc(VirtualKey.A, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Attach_Click(this, new RoutedEventArgs()));
    }

    public void Load(TodoTask task)
    {
        _loading = true;
        _task = task;
        TitleBox.Text = task.Title;
        DoneCheck.IsChecked = task.IsCompleted;
        var lists = _s.Store.GetLists();
        ListCombo.ItemsSource = lists;
        ListCombo.SelectedItem = lists.FirstOrDefault(l => l.Id == task.ListId);
        PriorityCombo.SelectedIndex = Math.Clamp(task.Priority, 0, 4);
        if (task.DueUtc is DateTime due)
        {
            var local = Ui.Local(due);
            DuePicker.Date = new DateTimeOffset(local.Date);
            TimeToggle.IsOn = task.DueHasTime;
            DueTime.SelectedTime = task.DueHasTime ? local.TimeOfDay : new TimeSpan(9, 0, 0);
        }
        else
        {
            DuePicker.Date = null;
            TimeToggle.IsOn = false;
            DueTime.SelectedTime = new TimeSpan(9, 0, 0);
        }
        DueTime.IsEnabled = TimeToggle.IsOn && task.DueUtc != null;
        _tags.Clear();
        _tags.AddRange(task.Tags);
        LoadTags();
        NotesBox.Text = task.Notes;
        CreatedText.Text = $"Created {Ui.Local(task.CreatedUtc):d MMM yyyy}" + (task.IsArchived ? " · Archived" : "");
        BuildRepeatOptions();
        var blocks = _s.Store.GetEventsForTask(task.Id);
        BlocksText.Text = blocks.Count == 0 ? "None. Drag the task onto the calendar to block time."
            : string.Join("\n", blocks.Select(b => $"{Ui.FriendlyDate(Ui.Local(b.StartUtc))} {Ui.Local(b.StartUtc):HH:mm} – {Ui.Local(b.EndUtc):HH:mm}{(b.DoneUtc != null ? " · done" : "")}"));
        LoadReminders();
        LoadSubtasks();
        LoadAttachments();
        LoadTime();
        AttachError.IsOpen = false;
        _loading = false;
    }

    // ---------------- fields ----------------

    private void BuildRepeatOptions()
    {
        var t = _task!;
        var anchor = t.DueUtc is DateTime d ? Ui.Local(d) : DateTime.Now;
        var nth = (anchor.Day - 1) / 7 + 1;
        var ordinal = nth switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{nth}th" };
        _repeatOptions = new()
        {
            ("Does not repeat", null, false),
            ("Every day", RecurrenceRule.Daily().ToString(), false),
            ("Every weekday", RecurrenceRule.Weekdays().ToString(), false),
            ($"Every week on {anchor:dddd}", RecurrenceRule.Weekly(anchor.DayOfWeek).ToString(), false),
            ($"Every month on day {anchor.Day}", RecurrenceRule.MonthlyByDate(anchor.Day).ToString(), false),
            ($"Every month on the {ordinal} {anchor:dddd}", RecurrenceRule.MonthlyByWeekday(Math.Min(nth, 4), anchor.DayOfWeek).ToString(), false),
            ("Every year", RecurrenceRule.Yearly().ToString(), false),
            ("Every day after completion", RecurrenceRule.Daily().ToString(), true),
            ("Every 7 days after completion", RecurrenceRule.Daily(7).ToString(), true)
        };
        var index = _repeatOptions.FindIndex(o => o.Rrule == t.Rrule && o.AfterCompletion == t.RepeatAfterCompletion);
        if (index < 0 && t.Rrule != null)
        {
            string label;
            try { label = "Custom: " + RecurrenceRule.Parse(t.Rrule).Describe() + (t.RepeatAfterCompletion ? " after completion" : ""); }
            catch (FormatException) { label = "Custom"; }
            _repeatOptions.Add((label, t.Rrule, t.RepeatAfterCompletion));
            index = _repeatOptions.Count - 1;
        }
        RepeatCombo.ItemsSource = _repeatOptions.Select(o => o.Label).ToList();
        RepeatCombo.SelectedIndex = Math.Max(0, index);
    }

    private void Field_LostFocus(object sender, RoutedEventArgs e) => Save();
    private void Field_Changed(object sender, SelectionChangedEventArgs e) => Save();
    private void DuePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { DueTime.IsEnabled = TimeToggle.IsOn && sender.Date != null; Save(); }
    private void TimeToggle_Toggled(object sender, RoutedEventArgs e) { DueTime.IsEnabled = TimeToggle.IsOn && DuePicker.Date != null; Save(); }
    private void DueTime_Changed(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) => Save();

    private void Save()
    {
        if (_loading || _task == null) return;
        var t = _task;
        var oldDue = t.DueUtc;
        var oldHasTime = t.DueHasTime;

        if (!string.IsNullOrWhiteSpace(TitleBox.Text)) t.Title = TitleBox.Text.Trim();
        if (ListCombo.SelectedItem is TodoList l) t.ListId = l.Id;
        t.Priority = Math.Max(0, PriorityCombo.SelectedIndex);
        if (DuePicker.Date is DateTimeOffset date)
        {
            var hasTime = TimeToggle.IsOn;
            var local = date.Date + (hasTime ? DueTime.SelectedTime ?? new TimeSpan(9, 0, 0) : TimeSpan.Zero);
            t.DueUtc = Ui.ToUtc(local);
            t.DueHasTime = hasTime;
        }
        else { t.DueUtc = null; t.DueHasTime = false; }
        var repeat = RepeatCombo.SelectedIndex >= 0 && RepeatCombo.SelectedIndex < _repeatOptions.Count ? _repeatOptions[RepeatCombo.SelectedIndex] : default;
        t.Rrule = t.DueUtc == null ? null : repeat.Rrule;
        t.RepeatAfterCompletion = repeat.AfterCompletion;
        t.Tags = _tags.ToList();
        t.Notes = NotesBox.Text;
        _s.Store.SaveTask(t);

        // Switching between timed and date-only (or adding a date) resets reminders to the defaults for that kind.
        if (t.DueUtc != null && (oldDue == null || oldHasTime != t.DueHasTime))
            _s.Store.SaveReminders(ReminderOwner.Task, t.Id, ReminderPlanner.Defaults(ReminderOwner.Task, t.Id, t.DueHasTime, _s.Settings));
        LoadReminders();
        TaskChanged?.Invoke();
    }

    private void DoneCheck_Click(object sender, RoutedEventArgs e) => ToggleDone();

    private void ToggleDone()
    {
        if (_task == null) return;
        if (_task.IsCompleted) _s.Store.ReopenTask(_task);
        else _s.Store.CompleteTask(_task);
        var fresh = _s.Store.GetTask(_task.Id);
        if (fresh != null) Load(fresh);
        TaskChanged?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Delete \u201C{_task.Title}\u201D?",
            Content = "The task, its subtasks and its reminders are deleted.",
            PrimaryButtonText = "Delete task", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _s.Store.DeleteTask(_task.Id);
        _task = null;
        TaskChanged?.Invoke();
        CloseRequested?.Invoke();
    }

    // ---------------- tags ----------------

    /// <summary>
    /// A chip per tag with an X, and a picker listing every tag in the master. Any number can be on one
    /// task; ticking one here is the same as typing #name in quick add, and a name that is not in the
    /// master yet is created by the save.
    /// </summary>
    private void LoadTags()
    {
        TagChips.Items.Clear();
        var known = _s.Store.GetTags().ToDictionary(t => t.Name, t => t.Color);
        foreach (var name in _tags.OrderBy(x => x))
        {
            var tag = name;
            var colour = known.TryGetValue(tag, out var hex) ? Ui.Hex(hex) : Ui.Brush("Text2Brush");
            var label = new TextBlock { Text = "#" + tag, FontSize = 12, Foreground = colour, VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 9 },
                Padding = new Thickness(3), MinWidth = 0, MinHeight = 0,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove tag " + tag);
            remove.Click += (_, _) => { _tags.Remove(tag); Save(); LoadTags(); };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            row.Children.Add(label);
            row.Children.Add(remove);
            TagChips.Items.Add(new Border
            {
                Background = Ui.Brush("Surface2Brush"), CornerRadius = new CornerRadius(11),
                Padding = new Thickness(9, 2, 4, 2), Margin = new Thickness(0, 0, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Left, Child = row
            });
        }

        var menu = new MenuFlyout();
        foreach (var tag in _s.Store.GetTags())
        {
            var name = tag.Name;
            var item = new ToggleMenuFlyoutItem { Text = "#" + name, IsChecked = _tags.Contains(name) };
            item.Click += (_, _) =>
            {
                if (_tags.Contains(name)) _tags.Remove(name); else _tags.Add(name);
                Save();
                LoadTags();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "New tag…", Icon = new SymbolIcon(Symbol.Add) };
        create.Click += async (_, _) => await NewTagAsync();
        menu.Items.Add(create);
        TagPicker.Flyout = menu;
    }

    private async Task NewTagAsync()
    {
        var existing = _s.Store.GetTags();
        var r = await NameColourDialog.ShowAsync(XamlRoot, "New tag", "Create", "",
            Core.Models.Tag.ColorFor(existing.Count), Core.Models.Tag.Palette, "planning");
        if (!r.Confirmed) return;
        var name = Core.Models.Tag.Normalise(r.Name);
        if (name.Length == 0) return;
        if (existing.All(t => t.Name != name))
            _s.Store.SaveTag(new Core.Models.Tag { Name = name, Color = r.Colour, SortOrder = existing.Count });
        if (!_tags.Contains(name)) _tags.Add(name);
        Save();
        LoadTags();
    }

    // ---------------- reminders ----------------

    private void LoadReminders()
    {
        var t = _task!;
        ReminderChips.Items.Clear();
        NoDueReminderText.Visibility = t.DueUtc == null ? Visibility.Visible : Visibility.Collapsed;
        AddReminderButton.Visibility = t.DueUtc == null ? Visibility.Collapsed : Visibility.Visible;
        if (t.DueUtc == null) return;

        var reminders = _s.Store.GetReminders(t.Id);
        foreach (var r in reminders)
        {
            var chip = new ToggleButton { Content = r.Label, IsChecked = r.Enabled, FontSize = 12, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3) };
            var reminder = r;
            chip.Click += (_, _) => { reminder.Enabled = chip.IsChecked == true; _s.Store.SaveReminders(ReminderOwner.Task, t.Id, reminders); };
            var flyout = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = "Remove reminder" };
            remove.Click += (_, _) => { reminders.Remove(reminder); _s.Store.SaveReminders(ReminderOwner.Task, t.Id, reminders); LoadReminders(); };
            flyout.Items.Add(remove);
            chip.ContextFlyout = flyout;
            ReminderChips.Items.Add(chip);
        }

        var menu = new MenuFlyout();
        void Option(string text, Reminder template)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => { template.OwnerId = t.Id; reminders.Add(template); _s.Store.SaveReminders(ReminderOwner.Task, t.Id, reminders); LoadReminders(); };
            menu.Items.Add(item);
        }
        if (t.DueHasTime)
        {
            Option("At start time", new Reminder { Kind = ReminderKind.AtStart });
            foreach (var m in new[] { 5, 10, 60 * 2, 60 * 24 })
                Option(m >= 60 ? (m == 1440 ? "1 day before" : $"{m / 60} hours before") : $"{m} min before", new Reminder { Kind = ReminderKind.BeforeStart, OffsetMinutes = m });
        }
        else
        {
            Option("Day before, 9:00 AM", new Reminder { Kind = ReminderKind.DateOnlyAt, DayOffset = -1, TimeOfDayMinutes = 9 * 60 });
            Option("On the day, 12:00 PM", new Reminder { Kind = ReminderKind.DateOnlyAt, DayOffset = 0, TimeOfDayMinutes = 12 * 60 });
            Option("2 days before, 9:00 AM", new Reminder { Kind = ReminderKind.DateOnlyAt, DayOffset = -2, TimeOfDayMinutes = 9 * 60 });
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var custom = new MenuFlyoutItem { Text = "Custom…" };
        custom.Click += async (_, _) =>
        {
            if (await AskCustomReminderAsync(t.DueHasTime) is not { } made) return;
            made.OwnerId = t.Id;
            reminders.Add(made);
            _s.Store.SaveReminders(ReminderOwner.Task, t.Id, reminders);
            LoadReminders();
        };
        menu.Items.Add(custom);
        AddReminderButton.Flyout = menu;
    }

    /// <summary>
    /// A reminder at a time the presets do not cover. A task with a due time can be reminded a chosen
    /// amount before it; any task can be reminded on a chosen day at a chosen clock time. Returns null
    /// if the dialog was cancelled.
    /// </summary>
    private async Task<Reminder?> AskCustomReminderAsync(bool hasDueTime)
    {
        var beforeRadio = new RadioButton { Content = "Before the due time", IsChecked = hasDueTime, IsEnabled = hasDueTime };
        var amount = new NumberBox { Value = 30, Minimum = 1, Maximum = 999, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 110 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(amount, "How long before");
        var unit = new ComboBox { Width = 120, SelectedIndex = 0 };
        foreach (var u in new[] { "minutes", "hours", "days" }) unit.Items.Add(u);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(unit, "Unit");
        var beforeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(28, 0, 0, 0) };
        beforeRow.Children.Add(amount);
        beforeRow.Children.Add(unit);

        var atRadio = new RadioButton { Content = "On a day, at a time", IsChecked = !hasDueTime };
        var dayOffset = new ComboBox { Width = 150, SelectedIndex = 1 };
        foreach (var d in new[] { "On the day", "1 day before", "2 days before", "3 days before", "1 week before" }) dayOffset.Items.Add(d);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dayOffset, "Which day");
        var clock = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, SelectedTime = new TimeSpan(9, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clock, "Time of day");
        var atRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(28, 0, 0, 0) };
        atRow.Children.Add(dayOffset);
        atRow.Children.Add(clock);

        void Sync()
        {
            var before = beforeRadio.IsChecked == true;
            amount.IsEnabled = unit.IsEnabled = before;
            dayOffset.IsEnabled = clock.IsEnabled = !before;
        }
        beforeRadio.Checked += (_, _) => Sync();
        atRadio.Checked += (_, _) => Sync();
        Sync();

        var panel = new StackPanel { Spacing = 8, Width = 330 };
        panel.Children.Add(beforeRadio);
        panel.Children.Add(beforeRow);
        if (!hasDueTime)
            panel.Children.Add(new TextBlock
            {
                Text = "This task has no due time, so it can only be reminded on a day.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0),
                Foreground = Ui.Brush("Text2Brush")
            });
        panel.Children.Add(atRadio);
        panel.Children.Add(atRow);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Custom reminder", Content = panel,
            PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        if (beforeRadio.IsChecked == true)
        {
            var n = double.IsNaN(amount.Value) ? 0 : (int)amount.Value;
            if (n <= 0) return null;
            var minutes = unit.SelectedIndex switch { 1 => n * 60, 2 => n * 60 * 24, _ => n };
            return new Reminder { Kind = ReminderKind.BeforeStart, OffsetMinutes = minutes };
        }

        var days = dayOffset.SelectedIndex switch { 1 => -1, 2 => -2, 3 => -3, 4 => -7, _ => 0 };
        var time = clock.SelectedTime ?? new TimeSpan(9, 0, 0);
        return new Reminder { Kind = ReminderKind.DateOnlyAt, DayOffset = days, TimeOfDayMinutes = (int)time.TotalMinutes };
    }

    // ---------------- subtasks ----------------

    private void LoadSubtasks()
    {
        SubtaskList.Children.Clear();
        var subs = _s.Store.GetSubtasks(_task!.Id);
        SubtasksTitle.Text = subs.Count == 0 ? "Subtasks" : $"Subtasks · {subs.Count(x => x.IsCompleted)} of {subs.Count}";
        foreach (var sub in subs)
        {
            var item = sub;
            var cb = new CheckBox { Content = sub.Title, IsChecked = sub.IsCompleted };
            cb.Click += (_, _) =>
            {
                if (cb.IsChecked == true) _s.Store.CompleteTask(item); else _s.Store.ReopenTask(item);
                LoadSubtasks();
                TaskChanged?.Invoke();
            };
            var flyout = new MenuFlyout();
            var del = new MenuFlyoutItem { Text = "Delete subtask" };
            del.Click += (_, _) => { _s.Store.DeleteTask(item.Id); LoadSubtasks(); TaskChanged?.Invoke(); };
            flyout.Items.Add(del);
            cb.ContextFlyout = flyout;
            SubtaskList.Children.Add(cb);
        }
    }

    private void NewSubtaskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(NewSubtaskBox.Text) || _task == null) return;
        _s.Store.SaveTask(new TodoTask { Title = NewSubtaskBox.Text.Trim(), ListId = _task.ListId, ParentId = _task.Id });
        NewSubtaskBox.Text = "";
        LoadSubtasks();
        TaskChanged?.Invoke();
        e.Handled = true;
    }

    // ---------------- attachments ----------------

    private void LoadAttachments()
    {
        AttachmentList.Children.Clear();
        foreach (var a in _s.Store.GetAttachments(_task!.Id))
        {
            var att = a;
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 6, 6, 6), CornerRadius = new CornerRadius(6), Background = Ui.Brush("Surface2Brush"), BorderBrush = Ui.Brush("LineBrush"), BorderThickness = new Thickness(1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var open = new HyperlinkButton { Content = a.FileName, Padding = new Thickness(0) };
            open.Click += async (_, _) => await _s.Attachments.OpenAsync(att);
            var size = new TextBlock { Text = a.SizeText, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };
            Grid.SetColumn(size, 1);
            var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 12 }, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"More options for {a.FileName}");
            var flyout = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename" };
            rename.Click += async (_, _) =>
            {
                var box = new TextBox { Text = att.FileName };
                var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = "Rename attachment", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary) { _s.Attachments.Rename(att, box.Text); LoadAttachments(); }
            };
            var remove = new MenuFlyoutItem { Text = "Remove" };
            remove.Click += (_, _) => { _s.Attachments.Remove(att); LoadAttachments(); TaskChanged?.Invoke(); };
            flyout.Items.Add(rename);
            flyout.Items.Add(remove);
            more.Flyout = flyout;
            Grid.SetColumn(more, 2);
            row.Children.Add(open);
            row.Children.Add(size);
            row.Children.Add(more);
            AttachmentList.Children.Add(row);
        }
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Instance!.MainWindow!.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file != null) await AddFileAsync(file);
    }

    private async Task AddFileAsync(StorageFile file)
    {
        var error = await _s.Attachments.AddAsync(_task!.Id, file);
        AttachError.Message = error ?? "";
        AttachError.IsOpen = error != null;
        LoadAttachments();
        TaskChanged?.Invoke();
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        if (_task != null && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Attach to task";
        }
    }

    private async void Panel_Drop(object sender, DragEventArgs e)
    {
        if (_task == null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        foreach (var item in await e.DataView.GetStorageItemsAsync())
            if (item is StorageFile f) await AddFileAsync(f);
    }

    // ---------------- time ----------------

    private void LoadTime()
    {
        var t = _task!;
        var running = _s.Timer.Running?.TaskId == t.Id;
        TimerButton.Content = running ? "Hold" : t.IsOnHold ? "Resume" : "Start";
        TimerButton.IsEnabled = !t.IsCompleted;
        TimerStatus.Text = running ? $"Running since {Ui.Local(_s.Timer.Running!.StartUtc):HH:mm}" : t.StageText;
        TimerElapsed.Text = running ? _s.Timer.ElapsedText : "";
        var entries = _s.Store.GetTimeEntries(t.Id).Where(x => !x.IsRunning).ToList();
        TimeTitle.Text = $"Time spent · {InsightsCalculator.FormatMinutes(entries.Sum(x => x.Minutes))}";
        TimeEntriesList.Children.Clear();
        foreach (var entry in entries.Take(8))
        {
            var en = entry;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = entry.Source switch { TimeSource.Timer => "timer", TimeSource.Block => "time block", _ => "added by hand" };
            row.Children.Add(new TextBlock { Text = $"{Ui.Local(entry.StartUtc):ddd d MMM} · {source}", FontSize = 12, Foreground = Ui.Brush("Text2Brush") });
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            right.Children.Add(new TextBlock { Text = InsightsCalculator.FormatMinutes(entry.Minutes), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var del = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Padding = new Thickness(6, 2, 6, 2), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(del, "Remove this time entry");
            del.Click += (_, _) => { _s.Store.DeleteTimeEntry(en); LoadTime(); TaskChanged?.Invoke(); };
            right.Children.Add(del);
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
            TimeEntriesList.Children.Add(row);
        }
    }

    /// <summary>The timer button is the stage button: a running clock is what "In progress" means, so
    /// starting one moves the task there and stopping one puts it on hold.</summary>
    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        if (_task is not { IsCompleted: false } t) return;
        if (_s.Timer.Running?.TaskId == t.Id) _s.Store.HoldTask(t);
        else _s.Store.StartTask(t);
        TaskChanged?.Invoke();
    }

    private void AddTime_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || double.IsNaN(ManualMinutes.Value) || ManualMinutes.Value < 1) return;
        var minutes = (int)ManualMinutes.Value;
        var now = DateTime.UtcNow;
        _s.Store.SaveTimeEntry(new TimeEntry { TaskId = _task.Id, StartUtc = now.AddMinutes(-minutes), EndUtc = now, Minutes = minutes, Source = TimeSource.Manual });
        ManualMinutes.Value = double.NaN;
        LoadTime();
        TaskChanged?.Invoke();
    }
}
