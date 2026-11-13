using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.App.Views;
using MyTodo.Core.Models;
using MyTodo.Core.Reminders;

namespace MyTodo.App;

public partial class App : Application
{
    public static App? Instance { get; private set; }
    public static AppServices Services => Instance!._services!;

    private AppServices? _services;
    private MainWindow? _window;
    private QuickAddWindow? _quickAdd;
    private readonly TrayService _tray = new();
    public HotkeyService Hotkey { get; } = new();
    private DispatcherQueueTimer? _heartbeat;
    private DateTime _lastHourly = DateTime.MinValue;
    private bool _exiting;

    public MainWindow? MainWindow => _window;
    public bool IsExiting => _exiting;

    public App()
    {
        Instance = this;
        InitializeComponent();
        UnhandledException += (_, e) => { Log.Error("Unhandled", e.Exception); e.Handled = true; };

        _services = new AppServices(DispatcherQueue.GetForCurrentThread());
        var s = _services.Settings;
        if (s.Theme == ThemeChoice.Light) RequestedTheme = ApplicationTheme.Light;
        else if (s.Theme == ThemeChoice.Dark) RequestedTheme = ApplicationTheme.Dark;
        if (s.UseWindowsAccent)
            foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
                Resources.Remove(key);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = Services;
        services.Reminders.RegisterActivation();
        services.Reminders.OpenRequested += (kind, id) => ShowMainWindow(kind switch
        {
            "task" => new NavRequest(typeof(ListsPage), id),
            "event" => new NavRequest(typeof(CalendarPage), id),
            _ => new NavRequest(typeof(TodayPage), null)
        });

        _tray.Create(
            open: () => ShowMainWindow(),
            today: () => ShowMainWindow(new NavRequest(typeof(TodayPage), null)),
            quickAdd: ShowQuickAdd,
            exit: Exit);

        _ = JumpListService.UpdateAsync();
        RunDailyHousekeeping();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var startHidden = activation.Kind == ExtendedActivationKind.StartupTask && services.Settings.StartMinimized
                          || activation.Kind == ExtendedActivationKind.AppNotification;
        EnsureWindow(show: !startHidden);
        HandleActivation(activation);

        // Reminders and missed-reminder check after the window exists.
        services.Reminders.RescheduleNow();
        var last = services.Settings.LastTickUtc;
        if (last != null) services.Reminders.CheckMissed(last.Value, DateTime.UtcNow);
        StartHeartbeat();
    }

    public void OnActivatedFromAnotherInstance(AppActivationArguments e) =>
        Services.Dispatcher.TryEnqueue(() => HandleActivation(e));

    private void HandleActivation(AppActivationArguments a)
    {
        switch (a.Kind)
        {
            case ExtendedActivationKind.AppNotification:
                if (a.Data is AppNotificationActivatedEventArgs n) Services.Reminders.Handle(n.Argument, n.UserInput);
                break;
            case ExtendedActivationKind.Launch:
                var text = (a.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments ?? "";
                if (text.Contains("--quickadd")) ShowQuickAdd();
                else if (text.Contains("--calendar")) ShowMainWindow(new NavRequest(typeof(CalendarPage), null));
                else if (text.Contains("--today")) ShowMainWindow(new NavRequest(typeof(TodayPage), null));
                else ShowMainWindow();
                break;
            case ExtendedActivationKind.StartupTask:
                break;
            default:
                ShowMainWindow();
                break;
        }
    }

    private void EnsureWindow(bool show)
    {
        if (_window != null) { if (show) _window.ShowAndFocus(); return; }
        _window = new MainWindow();
        Hotkey.Attach(_window.Hwnd);
        Hotkey.Pressed += ShowQuickAdd;
        if (!Hotkey.Register(Services.Settings.QuickAddHotkey))
            _window.ShowInfo($"The shortcut {Services.Settings.QuickAddHotkey} is used by Windows or another app. Pick another one in Settings › Startup and tray.");
        if (show) _window.ShowAndFocus();
        else _window.HideToTray();
    }

    public void ShowMainWindow(NavRequest? nav = null)
    {
        EnsureWindow(show: true);
        if (nav != null) _window!.Navigate(nav.Page, nav.Parameter);
    }

    public void ShowQuickAdd()
    {
        if (_quickAdd == null)
        {
            _quickAdd = new QuickAddWindow();
            _quickAdd.Closed += (_, _) => _quickAdd = null;
        }
        _quickAdd.ShowAndFocus();
    }

    public new void Exit()
    {
        _exiting = true;
        Services.Settings.LastTickUtc = DateTime.UtcNow;
        Services.SaveSettings();
        Hotkey.Dispose();
        _tray.Dispose();
        try { AppNotificationManager.Default.Unregister(); } catch { /* not registered */ }
        _quickAdd?.Close();
        _window?.Close();
        Services.Database.Dispose();
        Current.Exit();
    }

    /// <summary>A one-minute heartbeat spots sleep (missed reminders) and keeps the 7-day reminder window rolling.</summary>
    private void StartHeartbeat()
    {
        _heartbeat = Services.Dispatcher.CreateTimer();
        _heartbeat.Interval = TimeSpan.FromMinutes(1);
        _heartbeat.Tick += (_, _) =>
        {
            var s = Services.Settings;
            var now = DateTime.UtcNow;
            if (ReminderPlanner.WasAsleep(s.LastTickUtc, now, _heartbeat.Interval))
            {
                Services.Reminders.CheckMissed(s.LastTickUtc!.Value, now);
                Services.Reminders.RescheduleNow();
            }
            s.LastTickUtc = now;
            Services.SaveSettings();
            if (now - _lastHourly > TimeSpan.FromHours(1))
            {
                _lastHourly = now;
                Services.Reminders.RescheduleNow();
                RunDailyHousekeeping();
            }
        };
        _heartbeat.Start();
    }

    private void RunDailyHousekeeping()
    {
        var s = Services.Settings;
        if (s.LastArchiveRunUtc is DateTime last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;
        try
        {
            var archived = Services.Store.ArchiveCompleted(s.ArchiveAfterDays);
            if (archived > 0) Log.Info($"Archived {archived} completed tasks.");
            s.LastArchiveRunUtc = DateTime.UtcNow;
            Services.SaveSettings();
        }
        catch (Exception ex) { Log.Error("Housekeeping", ex); }
    }
}

public sealed record NavRequest(Type Page, object? Parameter);
