using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.App.Views;
using MyTodo.Core.Models;
using MyTodo.Core.Parsing;
using MyTodo.Core.Reminders;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace MyTodo.App;

/// <summary>Small always-on-top window opened by the global hotkey, the tray or the jump list (TSK-07).</summary>
public sealed partial class QuickAddWindow : Window
{
    private readonly AppServices _s = App.Services;

    public QuickAddWindow()
    {
        InitializeComponent();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            // Spotlight-style popup: no title bar, dismissed with Esc or by losing focus. The border stays
            // so Windows still draws the drop shadow and rounds the frame.
            p.SetBorderAndTitleBar(true, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        RoundCorners();
        SizeToContent();
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        else Root.Background = Ui.Brush("SurfaceBrush");
        ThemeService.Apply(Root, _s.Settings.Theme);
        Root.Loaded += (_, _) => { SizeToContent(); CenterOnScreen(); };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && Input.Text.Length == 0) Close(); };
        CenterOnScreen();
    }

    // Design size in DIPs. AppWindow works in physical pixels while XAML lays out in DIPs, so without
    // scaling by the window DPI a 150% display gives the content only two-thirds the room and it clips.
    private const int WidthDip = 580, HeightDip = 150;

    private void RoundCorners()
    {
        var pref = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(WindowNative.GetWindowHandle(this), Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private void SizeToContent()
    {
        var scale = Win32.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        // Measure the real content so the gap below the footer matches the 15px padding on the other three
        // sides; HeightDip is only the fallback for the first call, before the tree has loaded.
        Root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var height = Root.DesiredSize.Height > 0 ? Math.Ceiling(Root.DesiredSize.Height) : HeightDip;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(WidthDip * scale), (int)Math.Round(height * scale)));
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + area.Height / 4));
    }

    public void ShowAndFocus()
    {
        AppWindow.Show();
        Activate();
        Win32.SetForegroundWindow(WindowNative.GetWindowHandle(this));
        Input.Focus(FocusState.Programmatic);
    }

    private ParsedTask Parse() => QuickAddParser.Parse(Input.Text, Ui.Today, _s.Store.GetLists().Select(l => l.Name));

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Input.Text.Trim().Length == 0) { Chips.ItemsSource = null; return; }
        var parsed = Parse();
        var chips = parsed.PreviewChips().ToList();
        if (parsed.ListName == null && _s.Store.DefaultList() is { } fallback) chips.Add("List: " + fallback.Name);
        if (parsed.Date != null)
            chips.Add(parsed.Time != null ? "Reminders 1 h, 30 min, 15 min" : "Reminders 3 PM day before, 9 AM");
        Chips.ItemsSource = chips;
    }

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { Close(); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            Save(openAfter: ctrl);
            e.Handled = true;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Save(openAfter: false);

    private void Save(bool openAfter)
    {
        if (Input.Text.Trim().Length == 0) return;
        var task = TaskCreator.CreateFromText(_s, Input.Text);
        Close();
        if (openAfter) App.Instance!.ShowMainWindow(new NavRequest(typeof(BoardPage), "task:" + task.Id));
    }
}

/// <summary>Creates a task from quick-add text with the default reminders.</summary>
public static class TaskCreator
{
    public static TodoTask CreateFromText(AppServices s, string text, Guid? listId = null, DateOnly? defaultDate = null)
    {
        var lists = s.Store.GetLists();
        var parsed = QuickAddParser.Parse(text, Ui.Today, lists.Select(l => l.Name));
        if (parsed.Date == null && defaultDate != null) parsed.Date = defaultDate;
        var list = parsed.ListName != null ? lists.FirstOrDefault(l => string.Equals(l.Name, parsed.ListName, StringComparison.OrdinalIgnoreCase)) : null;
        var task = new TodoTask
        {
            Title = parsed.Title,
            ListId = list?.Id ?? listId ?? lists[0].Id,
            Priority = parsed.Priority,
            DueUtc = parsed.DueUtc(),
            DueHasTime = parsed.Time != null,
            Rrule = parsed.Rrule,
            RepeatAfterCompletion = parsed.RepeatAfterCompletion,
            Tags = parsed.Tags.ToList(),
            SortOrder = s.Store.GetAllTasks().Count
        };
        s.Store.SaveTask(task);
        if (task.DueUtc != null)
            s.Store.SaveReminders(ReminderOwner.Task, task.Id, ReminderPlanner.Defaults(ReminderOwner.Task, task.Id, task.DueHasTime, s.Settings));
        return task;
    }
}
