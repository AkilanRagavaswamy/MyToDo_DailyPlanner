using System.Security;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using MyTodo.App.Helpers;
using MyTodo.Core.Models;
using MyTodo.Core.Reminders;
using MyTodo.Data;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MyTodo.App.Services;

public sealed record MissedReminder(string Kind, Guid Id, string Title, DateTime FireUtc, DateTime StartUtc);

/// <summary>
/// Reminders as Windows-scheduled toasts (section 9.1): Windows fires them even when the app is closed.
/// A rolling 7-day window is re-registered on every change, on start, hourly and after sleep.
/// </summary>
public sealed class ReminderService
{
    private const int WindowDays = 7;
    private readonly AppServices _app;
    private readonly DispatcherQueueTimer _debounce;

    public event Action<IReadOnlyList<MissedReminder>>? MissedRemindersFound;
    /// <summary>Raised when a toast asks to open something: ("task" | "event" | "today", id).</summary>
    public event Action<string, Guid>? OpenRequested;

    private sealed class Snooze
    {
        public string Kind { get; set; } = "";
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public DateTime UntilUtc { get; set; }
        public DateTime StartUtc { get; set; }
    }

    private sealed record Planned(string Kind, Guid Id, string Title, string Body, DateTime FireUtc, DateTime StartUtc);

    public ReminderService(AppServices app)
    {
        _app = app;
        _debounce = app.Dispatcher.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(600);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => RescheduleNow();
        app.Store.Changed += table =>
        {
            if (table is "tasks" or "events" or "reminders" or "settings") app.Dispatcher.TryEnqueue(Reschedule);
        };
    }

    public void RegisterActivation()
    {
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (_, e) => _app.Dispatcher.TryEnqueue(() => Handle(e.Argument, e.UserInput));
        try { manager.Register(); }
        catch (Exception ex) { Log.Error("Notification registration failed", ex); }
    }

    /// <summary>Debounced re-registration of all scheduled toasts.</summary>
    public void Reschedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void RescheduleNow()
    {
        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            foreach (var old in notifier.GetScheduledToastNotifications()) notifier.RemoveFromSchedule(old);

            var now = DateTime.UtcNow;
            var horizon = now.AddDays(WindowDays);
            var count = 0;
            foreach (var p in PlanBetween(now, horizon))
            {
                var key = DataStore.ReminderKey(p.Id, p.FireUtc);
                var state = _app.Store.GetReminderState(key);
                if (state is DataStore.ReminderLogState.Dismissed or DataStore.ReminderLogState.Done or DataStore.ReminderLogState.Snoozed) continue;
                if (p.FireUtc <= now.AddSeconds(5)) continue;
                notifier.AddToSchedule(Scheduled(p.Kind, p.Id, p.Title, p.Body, p.FireUtc, p.StartUtc, p.FireUtc));
                count++;
            }

            var snoozes = LoadSnoozes().Where(s => s.UntilUtc > now).ToList();
            SaveSnoozes(snoozes);
            foreach (var s in snoozes)
                notifier.AddToSchedule(Scheduled(s.Kind, s.Id, s.Title, s.Body, s.UntilUtc, s.StartUtc, s.UntilUtc));

            if (_app.Settings.DailyPlanningPrompt)
                foreach (var at in ReminderPlanner.PlanningPrompts(now, _app.Settings.DailyPlanningMinutes))
                    notifier.AddToSchedule(Scheduled("plan", Guid.Empty, "Plan your day", "Open Today to drag tasks into your schedule.", at, at, at));

            Log.Info($"Scheduled {count} reminders and {snoozes.Count} snoozes.");
        }
        catch (Exception ex)
        {
            Log.Error("Reschedule failed", ex);
        }
    }

    /// <summary>Every reminder firing in (fromUtc, toUtc].</summary>
    private IEnumerable<Planned> PlanBetween(DateTime fromUtc, DateTime toUtc)
    {
        var store = _app.Store;
        var reminders = store.GetAllReminders().ToLookup(r => r.OwnerId);
        var lists = store.GetLists().ToDictionary(l => l.Id, l => l.Name);

        foreach (var t in store.GetAllTasks().Where(t => !t.IsCompleted && t.DueUtc != null))
        {
            var due = t.DueUtc!.Value;
            if (due < fromUtc.AddDays(-1) || due > toUtc.AddDays(2)) continue;
            foreach (var plan in ReminderPlanner.Plan(due, t.DueHasTime, reminders[t.Id], fromUtc, t.UpdatedUtc))
            {
                if (plan.FireUtc > toUtc) continue;
                var list = lists.TryGetValue(t.ListId, out var n) ? n : "";
                yield return new Planned("task", t.Id, t.Title, TaskBody(t, plan.FireUtc, list), plan.FireUtc, due);
            }
        }

        foreach (var occ in store.GetOccurrences(fromUtc, toUtc.AddDays(2)))
        {
            var e = occ.Event;
            if (e.DoneUtc != null && e.Rrule == null) continue;
            foreach (var plan in ReminderPlanner.Plan(occ.StartUtc, !e.AllDay, reminders[e.Id], fromUtc, e.UpdatedUtc))
            {
                if (plan.FireUtc > toUtc) continue;
                yield return new Planned("event", e.Id, e.Title, EventBody(occ, plan.FireUtc), plan.FireUtc, occ.StartUtc);
            }
        }
    }

    private static string TaskBody(TodoTask t, DateTime fireUtc, string list)
    {
        var suffix = list.Length > 0 ? " · " + list : "";
        if (!t.DueHasTime)
        {
            var dueDay = DateOnly.FromDateTime(Ui.Local(t.DueUtc!.Value));
            var fireDay = DateOnly.FromDateTime(Ui.Local(fireUtc));
            return (dueDay == fireDay ? "Due today" : dueDay == fireDay.AddDays(1) ? "Due tomorrow" : $"Due {dueDay:ddd d MMM}") + suffix;
        }
        return $"Due {Ui.Local(t.DueUtc!.Value):HH:mm}{Until(t.DueUtc!.Value, fireUtc)}{suffix}";
    }

    private static string EventBody(EventOccurrence o, DateTime fireUtc)
    {
        var s = Ui.Local(o.StartUtc);
        var e = Ui.Local(o.EndUtc);
        var where = o.Event.Location.Length > 0 ? " · " + o.Event.Location : "";
        return o.Event.AllDay ? $"All day{where}" : $"{s:HH:mm} – {e:HH:mm}{Until(o.StartUtc, fireUtc)}{where}";
    }

    private static string Until(DateTime startUtc, DateTime fireUtc)
    {
        var mins = (int)Math.Round((startUtc - fireUtc).TotalMinutes);
        if (mins <= 0) return " · starting now";
        return mins < 60 ? $" · in {mins} minutes" : mins % 60 == 0 ? $" · in {mins / 60} hour{(mins == 60 ? "" : "s")}" : $" · in {mins / 60} h {mins % 60} min";
    }

    private ScheduledToastNotification Scheduled(string kind, Guid id, string title, string body, DateTime fireUtc, DateTime startUtc, DateTime keyFireUtc)
    {
        var doc = new XmlDocument();
        doc.LoadXml(ToastXml(kind, id, title, body, startUtc, keyFireUtc));
        var fire = new DateTimeOffset(DateTime.SpecifyKind(fireUtc, DateTimeKind.Utc));
        return new ScheduledToastNotification(doc, fire)
        {
            Tag = (id.ToString("N")[..12] + (keyFireUtc.Ticks / TimeSpan.TicksPerMinute % 100000000)).ToString(),
            Group = kind
        };
    }

    private string ToastXml(string kind, Guid id, string title, string body, DateTime startUtc, DateTime fireUtc)
    {
        static string Esc(string s) => SecurityElement.Escape(s) ?? "";
        var baseArgs = $"kind={kind}&id={id}&fire={fireUtc.Ticks}&start={startUtc.Ticks}";
        var sound = _app.Settings.ReminderSound
            ? $"<audio src=\"{Esc(_app.Settings.ReminderSoundUri)}\" />"
            : "<audio silent=\"true\" />";

        if (kind == "plan")
            return $"<toast launch=\"{Esc("action=open&" + baseArgs)}\"><visual><binding template=\"ToastGeneric\"><text>{Esc(title)}</text><text>{Esc(body)}</text></binding></visual>" +
                   $"<actions><action content=\"Open Today\" arguments=\"{Esc("action=open&" + baseArgs)}\" activationType=\"foreground\" /><action content=\"Dismiss\" arguments=\"{Esc("action=dismiss&" + baseArgs)}\" activationType=\"foreground\" /></actions>{sound}</toast>";

        var choices = string.Join("", _app.Settings.SnoozeMinutes.Select(m =>
            $"<selection id=\"{m}\" content=\"{(m >= 60 && m % 60 == 0 ? (m == 60 ? "1 hour" : $"{m / 60} hours") : $"{m} minutes")}\" />"));
        choices += "<selection id=\"0\" content=\"Tomorrow, 9:00 AM\" />";
        var defaultSnooze = _app.Settings.SnoozeMinutes.Contains(15) ? "15" : _app.Settings.SnoozeMinutes.FirstOrDefault().ToString();

        return $"<toast scenario=\"reminder\" launch=\"{Esc("action=open&" + baseArgs)}\">" +
               $"<visual><binding template=\"ToastGeneric\"><text>{Esc(title)}</text><text>{Esc(body)}</text></binding></visual>" +
               $"<actions><input id=\"snooze\" type=\"selection\" defaultInput=\"{defaultSnooze}\">{choices}</input>" +
               $"<action content=\"Snooze\" arguments=\"{Esc("action=snooze&" + baseArgs)}\" activationType=\"foreground\" hint-inputId=\"snooze\" />" +
               $"<action content=\"Mark done\" arguments=\"{Esc("action=done&" + baseArgs)}\" activationType=\"foreground\" />" +
               $"<action content=\"Dismiss\" arguments=\"{Esc("action=dismiss&" + baseArgs)}\" activationType=\"foreground\" /></actions>" +
               $"{sound}</toast>";
    }

    /// <summary>Handles a toast click or button (REM-05). Works whether or not the window is open.</summary>
    public void Handle(string argument, IDictionary<string, string>? userInput)
    {
        var args = argument.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        if (!args.TryGetValue("action", out var action)) return;
        args.TryGetValue("kind", out var kind);
        Guid.TryParse(args.GetValueOrDefault("id"), out var id);
        var fireUtc = long.TryParse(args.GetValueOrDefault("fire"), out var f) ? new DateTime(f, DateTimeKind.Utc) : DateTime.UtcNow;
        var startUtc = long.TryParse(args.GetValueOrDefault("start"), out var s) ? new DateTime(s, DateTimeKind.Utc) : DateTime.UtcNow;
        var key = DataStore.ReminderKey(id, fireUtc);
        var store = _app.Store;

        switch (action)
        {
            case "open":
                store.MarkReminder(key, DataStore.ReminderLogState.Shown);
                OpenRequested?.Invoke(kind == "plan" ? "today" : kind ?? "today", id);
                break;

            case "snooze":
                var minutes = userInput != null && userInput.TryGetValue("snooze", out var v) && int.TryParse(v, out var m) ? m : 15;
                var until = ReminderPlanner.SnoozeUntil(minutes, DateTime.UtcNow);
                store.MarkReminder(key, DataStore.ReminderLogState.Snoozed);
                var (title, body) = Describe(kind, id, startUtc);
                var snoozes = LoadSnoozes();
                snoozes.RemoveAll(x => x.Id == id);
                snoozes.Add(new Snooze { Kind = kind ?? "task", Id = id, Title = title, Body = body, UntilUtc = until, StartUtc = startUtc });
                SaveSnoozes(snoozes);
                RescheduleNow();
                break;

            case "done":
                store.MarkReminder(key, DataStore.ReminderLogState.Done);
                RemoveSnooze(id);
                if (kind == "task" && store.GetTask(id) is TodoTask t && !t.IsCompleted) store.CompleteTask(t);
                else if (kind == "event" && store.GetEvent(id) is CalendarEvent e) CompleteBlock(e, startUtc);
                break;

            case "dismiss":
                store.MarkReminder(key, DataStore.ReminderLogState.Dismissed);
                RemoveSnooze(id);
                break;
        }
    }

    /// <summary>CAL-08: a completed time block adds its minutes to the linked task's time.</summary>
    public void CompleteBlock(CalendarEvent e, DateTime occurrenceStartUtc)
    {
        if (e.Rrule == null) { e.DoneUtc = DateTime.UtcNow; _app.Store.SaveEvent(e); }
        if (e.TaskId is Guid taskId)
            _app.Store.SaveTimeEntry(new TimeEntry
            {
                TaskId = taskId, StartUtc = occurrenceStartUtc, EndUtc = occurrenceStartUtc + e.Duration,
                Minutes = (int)Math.Round(e.Duration.TotalMinutes), Source = TimeSource.Block, EventId = e.Id
            });
    }

    private (string, string) Describe(string? kind, Guid id, DateTime startUtc)
    {
        if (kind == "task" && _app.Store.GetTask(id) is TodoTask t)
            return (t.Title, TaskBody(t, DateTime.UtcNow, ""));
        if (kind == "event" && _app.Store.GetEvent(id) is CalendarEvent e)
            return (e.Title, EventBody(new EventOccurrence(e, startUtc, startUtc + e.Duration), DateTime.UtcNow));
        return ("Reminder", "");
    }

    /// <summary>REM-07: reminders that should have fired while the PC was asleep or off.</summary>
    public void CheckMissed(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc - fromUtc < TimeSpan.FromMinutes(2)) return;
        var missed = PlanBetween(fromUtc, toUtc)
            .Where(p => p.FireUtc <= toUtc)
            .Where(p => _app.Store.GetReminderState(DataStore.ReminderKey(p.Id, p.FireUtc)) == null)
            .GroupBy(p => p.Id).Select(g => g.OrderByDescending(p => p.FireUtc).First())
            .Select(p => new MissedReminder(p.Kind, p.Id, p.Title, p.FireUtc, p.StartUtc))
            .ToList();
        if (missed.Count > 0) MissedRemindersFound?.Invoke(missed);
    }

    public void AcknowledgeMissed(IEnumerable<MissedReminder> items, int snoozeMinutes)
    {
        var snoozes = LoadSnoozes();
        foreach (var m in items)
        {
            _app.Store.MarkReminder(DataStore.ReminderKey(m.Id, m.FireUtc), snoozeMinutes > 0 ? DataStore.ReminderLogState.Snoozed : DataStore.ReminderLogState.Dismissed);
            if (snoozeMinutes <= 0) continue;
            var (title, body) = Describe(m.Kind, m.Id, m.StartUtc);
            snoozes.RemoveAll(x => x.Id == m.Id);
            snoozes.Add(new Snooze { Kind = m.Kind, Id = m.Id, Title = title, Body = body, UntilUtc = DateTime.UtcNow.AddMinutes(snoozeMinutes), StartUtc = m.StartUtc });
        }
        SaveSnoozes(snoozes);
        RescheduleNow();
    }

    private List<Snooze> LoadSnoozes()
    {
        var json = _app.Store.GetValue("snoozes");
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<List<Snooze>>(json) ?? new(); } catch (JsonException) { return new(); }
    }

    private void SaveSnoozes(List<Snooze> list) => _app.Store.SetValue("snoozes", JsonSerializer.Serialize(list));

    private void RemoveSnooze(Guid id)
    {
        var list = LoadSnoozes();
        if (list.RemoveAll(s => s.Id == id) > 0) { SaveSnoozes(list); RescheduleNow(); }
    }
}
