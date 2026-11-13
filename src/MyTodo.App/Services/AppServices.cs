using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MyTodo.App.Helpers;
using MyTodo.Core.Models;
using MyTodo.Data;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.StartScreen;

namespace MyTodo.App.Services;

/// <summary>Everything the pages share. Created once in App.</summary>
public sealed class AppServices
{
    public Database Database { get; }
    public DataStore Store { get; }
    public AppSettings Settings { get; }
    public DispatcherQueue Dispatcher { get; }
    public ReminderService Reminders { get; }
    public TimerService Timer { get; }
    public AttachmentService Attachments { get; }

    public AppServices(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "mytodo.db");
        Database = new Database(path);
        Store = new DataStore(Database);
        Settings = Store.LoadSettings();
        Reminders = new ReminderService(this);
        Timer = new TimerService(this);
        Attachments = new AttachmentService(Store);
    }

    public void SaveSettings() => Store.SaveSettings(Settings);
}

/// <summary>The single running task timer (TSK-12). The start time is stored, so it survives closing the window and restarts.</summary>
public sealed class TimerService
{
    private readonly AppServices _app;
    private readonly DispatcherQueueTimer _tick;

    public event Action? Changed;
    public event Action? Tick;

    public TimeEntry? Running { get; private set; }
    public string RunningTitle { get; private set; } = "";

    public TimerService(AppServices app)
    {
        _app = app;
        _tick = app.Dispatcher.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => Tick?.Invoke();
        Refresh();
    }

    public TimeSpan Elapsed => Running == null ? TimeSpan.Zero : DateTime.UtcNow - Running.StartUtc;
    public string ElapsedText => $"{(int)Elapsed.TotalHours}:{Elapsed.Minutes:00}:{Elapsed.Seconds:00}";

    public void Refresh()
    {
        Running = _app.Store.GetRunningTimer();
        RunningTitle = Running == null ? "" : _app.Store.GetTask(Running.TaskId)?.Title ?? "";
        if (Running != null) _tick.Start(); else _tick.Stop();
        Changed?.Invoke();
    }

    public void Start(Guid taskId) { _app.Store.StartTimer(taskId); Refresh(); }
    public void Stop() { _app.Store.StopTimer(); Refresh(); }
    public void Toggle(Guid taskId)
    {
        if (Running?.TaskId == taskId) Stop(); else Start(taskId);
    }
}

/// <summary>Attachments are copied into the app's own folder (TSK-09), stored once per content hash, max 10 MB.</summary>
public sealed class AttachmentService
{
    private readonly DataStore _store;
    public string Folder { get; }

    public AttachmentService(DataStore store)
    {
        _store = store;
        Folder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "attachments");
        Directory.CreateDirectory(Folder);
    }

    /// <summary>Returns null on success, or a message for the user.</summary>
    public async Task<string?> AddAsync(Guid taskId, StorageFile file)
    {
        var props = await file.GetBasicPropertiesAsync();
        if ((long)props.Size > Attachment.MaxBytes)
            return $"{file.Name} is {props.Size / 1024d / 1024d:0.0} MB. Attachments can be up to 10 MB. Try a smaller file or a link.";

        string sha;
        using (var stream = await file.OpenStreamForReadAsync())
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

        var stored = sha + Path.GetExtension(file.Name).ToLowerInvariant();
        var target = Path.Combine(Folder, stored);
        if (!File.Exists(target))
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(Folder);
            await file.CopyAsync(folder, stored, NameCollisionOption.ReplaceExisting);
        }
        _store.SaveAttachment(new Attachment
        {
            TaskId = taskId, FileName = file.Name, StoredName = stored, SizeBytes = (long)props.Size, Sha256 = sha, MimeType = file.ContentType
        });
        return null;
    }

    public async Task OpenAsync(Attachment a)
    {
        var path = Path.Combine(Folder, a.StoredName);
        if (!File.Exists(path)) return;
        var file = await StorageFile.GetFileFromPathAsync(path);
        await Windows.System.Launcher.LaunchFileAsync(file);
    }

    public void Remove(Attachment a)
    {
        a.Deleted = true;
        _store.SaveAttachment(a);
        if (!_store.IsStoredFileShared(a.StoredName, a.Id))
        {
            try { File.Delete(Path.Combine(Folder, a.StoredName)); } catch (IOException ex) { Log.Error("Attachment delete failed", ex); }
        }
    }

    public void Rename(Attachment a, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        a.FileName = newName.Trim();
        _store.SaveAttachment(a);
    }
}

/// <summary>Launch at Windows startup through the MSIX startup task (WIN-02).</summary>
public static class StartupService
{
    private const string TaskId = "MyTodoStartup";

    /// <summary>Returns null on success, or a message when Windows settings block the change.</summary>
    public static async Task<string?> SetEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (!enabled) { task.Disable(); return null; }
            var state = task.State == StartupTaskState.Enabled ? StartupTaskState.Enabled : await task.RequestEnableAsync();
            return state switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
                StartupTaskState.DisabledByUser => "Startup is turned off for this app in Windows Settings › Apps › Startup. Turn it on there.",
                StartupTaskState.DisabledByPolicy => "Your organisation's policy does not allow this app to start with Windows.",
                _ => "Windows did not allow the app to start at sign-in."
            };
        }
        catch (Exception ex)
        {
            Log.Error("Startup task", ex);
            return "Launch at startup is only available when the app is installed from its package.";
        }
    }
}

/// <summary>Taskbar jump list: New task, Today, Calendar (WIN-06).</summary>
public static class JumpListService
{
    public static async Task UpdateAsync()
    {
        try
        {
            if (!JumpList.IsSupported()) return;
            var list = await JumpList.LoadCurrentAsync();
            list.Items.Clear();
            list.SystemGroupKind = JumpListSystemGroupKind.None;
            list.Items.Add(JumpListItem.CreateWithArguments("--quickadd", "New task"));
            list.Items.Add(JumpListItem.CreateWithArguments("--today", "Today"));
            list.Items.Add(JumpListItem.CreateWithArguments("--calendar", "Calendar"));
            await list.SaveAsync();
        }
        catch (Exception ex) { Log.Error("Jump list", ex); }
    }
}

/// <summary>Applies the Light / Dark / Use system setting (WIN-04).</summary>
public static class ThemeService
{
    public static event Action? ThemeChanged;

    public static void Apply(FrameworkElement root, ThemeChoice choice)
    {
        root.RequestedTheme = choice switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        Ui.EffectiveTheme = choice switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => SystemIsDark() ? ElementTheme.Dark : ElementTheme.Light
        };
        ThemeChanged?.Invoke();
    }

    public static bool SystemIsDark()
    {
        var bg = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        return bg.R < 128;
    }
}
