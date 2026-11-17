using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyTodo.App.Helpers;
using MyTodo.App.Services;
using MyTodo.App.Views;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace MyTodo.App;

public sealed partial class MainWindow : Window
{
    public IntPtr Hwnd { get; }
    private readonly AppServices _s = App.Services;
    private bool _suppressNav;
    private IReadOnlyList<MissedReminder> _missed = Array.Empty<MissedReminder>();

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try { AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall; } catch { /* Windows 10 */ }
        AppWindow.SetIcon("Assets/TrayIcon.ico");
        AppWindow.Resize(new SizeInt32(1280, 840));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            try { p.PreferredMinimumWidth = 900; p.PreferredMinimumHeight = 600; } catch { /* older runtime */ }
        }

        // Mica on Windows 11, a solid themed background on Windows 10 (WIN-04).
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        ThemeService.ThemeChanged += ApplyBackground;
        ThemeService.Apply(Root, _s.Settings.Theme);
        new Windows.UI.ViewManagement.UISettings().ColorValuesChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(() => { if (_s.Settings.Theme == Core.Models.ThemeChoice.System) ThemeService.Apply(Root, _s.Settings.Theme); });

        AppWindow.Closing += (_, e) =>
        {
            if (App.Instance!.IsExiting) return;
            if (_s.Settings.CloseToTray) { e.Cancel = true; HideToTray(); }
            else { e.Cancel = true; App.Instance.Exit(); }
        };

        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        SearchBox.SizeChanged += (_, _) => UpdateTitleBarRegions();

        BuildAccelerators();
        RebuildListsMenu();
        _s.Store.Changed += t => { if (t is "lists" or "tasks") DispatcherQueue.TryEnqueue(RebuildListsMenu); };
        _s.Reminders.MissedRemindersFound += m => DispatcherQueue.TryEnqueue(() => ShowMissed(m));

        // No lists means first run has not finished: the user must create one before the app is usable.
        if (!_s.Settings.FirstRunDone || _s.Store.GetLists().Count == 0)
        {
            Nav.IsPaneVisible = false;
            ContentFrame.Navigate(typeof(WelcomePage));
        }
        else Navigate(typeof(TodayPage), null);
    }

    private void ApplyBackground()
    {
        Root.Background = MicaController.IsSupported() ? new SolidColorBrush(Colors.Transparent) : Ui.Brush("AppBackgroundBrush");
        var dark = Ui.EffectiveTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x14, 0, 0, 0);
        if (ContentFrame.Content is IThemeAware page) page.OnThemeChanged();
    }

    /// <summary>Makes the search box clickable inside the drag region.</summary>
    private void UpdateTitleBarRegions()
    {
        try
        {
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            var t = SearchBox.TransformToVisual(null);
            var bounds = t.TransformBounds(new Windows.Foundation.Rect(0, 0, SearchBox.ActualWidth, SearchBox.ActualHeight));
            var rect = new RectInt32((int)(bounds.X * scale), (int)(bounds.Y * scale), (int)(bounds.Width * scale), (int)(bounds.Height * scale));
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch (Exception ex) { Log.Error("Title bar regions", ex); }
    }

    public void ShowAndFocus()
    {
        AppWindow.Show();
        Activate();
        Win32.SetForegroundWindow(Hwnd);
    }

    public void HideToTray() => AppWindow.Hide();

    public void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        GlobalInfo.ActionButton = null;
        GlobalInfo.Title = "";
        GlobalInfo.Message = message;
        GlobalInfo.Severity = severity;
        GlobalInfo.IsOpen = true;
    }

    private void ShowMissed(IReadOnlyList<MissedReminder> missed)
    {
        _missed = missed;
        var review = new Button { Content = "Review" };
        review.Click += async (_, _) => await ReviewMissedAsync();
        GlobalInfo.Title = "";
        GlobalInfo.Message = missed.Count == 1 ? "1 reminder was missed while your PC was asleep." : $"{missed.Count} reminders were missed while your PC was asleep.";
        GlobalInfo.Severity = InfoBarSeverity.Warning;
        GlobalInfo.ActionButton = review;
        GlobalInfo.IsOpen = true;
    }

    private async Task ReviewMissedAsync()
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var m in _missed)
            panel.Children.Add(new TextBlock { Text = $"{m.Title}  ·  {Ui.Local(m.FireUtc):ddd HH:mm}", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = _missed.Count == 1 ? "1 reminder was missed" : $"{_missed.Count} reminders were missed",
            Content = panel,
            PrimaryButtonText = "Got it",
            SecondaryButtonText = "Snooze all 15 min",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        _s.Reminders.AcknowledgeMissed(_missed, result == ContentDialogResult.Secondary ? 15 : 0);
        GlobalInfo.IsOpen = false;
    }

    // ---------------- navigation ----------------

    public void Navigate(Type page, object? parameter)
    {
        Nav.IsPaneVisible = true;
        ContentFrame.Navigate(page, parameter);
        _suppressNav = true;
        var tag = page == typeof(TodayPage) ? "today"
            : page == typeof(ListsPage) ? "lists"
            : page == typeof(BoardPage) ? "board"
            : page == typeof(ReportPage) ? "report"
            : page == typeof(TagsPage) ? "tags"
            : page == typeof(CalendarPage) ? "calendar"
            : page == typeof(DiaryPage) ? "diary"
            : page == typeof(InsightsPage) ? "insights"
            : page == typeof(SettingsPage) ? "settings"
            : page == typeof(ShortcutsPage) ? "shortcuts" : null;
        Nav.SelectedItem = FindItem(tag);
        _suppressNav = false;
    }

    private NavigationViewItem? FindItem(string? tag)
    {
        if (tag == null) return null;
        IEnumerable<NavigationViewItem> All(IEnumerable<object> items) =>
            items.OfType<NavigationViewItem>().SelectMany(i => new[] { i }.Concat(All(i.MenuItems)));
        return All(Nav.MenuItems).Concat(All(Nav.FooterMenuItems)).FirstOrDefault(i => (i.Tag as string) == tag);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNav || args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string ?? "";
        switch (tag)
        {
            case "today": ContentFrame.Navigate(typeof(TodayPage)); break;
            case "lists": ContentFrame.Navigate(typeof(ListsPage)); break;
            case "tags": ContentFrame.Navigate(typeof(TagsPage)); break;
            case "board": ContentFrame.Navigate(typeof(BoardPage)); break;
            case "report": ContentFrame.Navigate(typeof(ReportPage)); break;
            case "calendar": ContentFrame.Navigate(typeof(CalendarPage)); break;
            case "diary": ContentFrame.Navigate(typeof(DiaryPage)); break;
            case "insights": ContentFrame.Navigate(typeof(InsightsPage)); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            case "shortcuts": ContentFrame.Navigate(typeof(ShortcutsPage)); break;
            default:
                if (tag.StartsWith("list:")) ContentFrame.Navigate(typeof(BoardPage), tag);
                break;
        }
    }

    /// <summary>The count of open tasks on the Board item. Lists are managed on the list master and the
    /// cross-list views live on Report, so the rail no longer carries a tree of either (TSK-02, TSK-06).</summary>
    private void RebuildListsMenu()
    {
        var open = _s.Store.GetAllTasks().Count(t => t.IsActive && t.ParentId == null);
        BoardItem.InfoBadge = open > 0 ? new InfoBadge { Value = open, Opacity = 0.75 } : null;
    }

    private void NewTask_Click(object sender, RoutedEventArgs e) => App.Instance!.ShowQuickAdd();

    // ---------------- search ----------------

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        if (sender.Text.Trim().Length >= 2) ShowSearch(sender.Text);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => ShowSearch(args.QueryText);

    private void ShowSearch(string query)
    {
        if (ContentFrame.Content is SearchPage sp) sp.Run(query);
        else { ContentFrame.Navigate(typeof(SearchPage), query); _suppressNav = true; Nav.SelectedItem = null; _suppressNav = false; }
    }

    // ---------------- keyboard shortcuts (WIN-05) ----------------

    private void BuildAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, e) => { action(); e.Handled = true; };
            Root.KeyboardAccelerators.Add(acc);
        }
        const VirtualKeyModifiers C = VirtualKeyModifiers.Control;
        Add(VirtualKey.Number1, C, () => Navigate(typeof(TodayPage), null));
        Add(VirtualKey.Number2, C, () => Navigate(typeof(BoardPage), null));
        Add(VirtualKey.Number3, C, () => Navigate(typeof(CalendarPage), null));
        Add(VirtualKey.Number4, C, () => Navigate(typeof(DiaryPage), null));
        Add(VirtualKey.Number5, C, () => Navigate(typeof(ReportPage), null));
        Add(VirtualKey.Number6, C, () => Navigate(typeof(InsightsPage), null));
        Add(VirtualKey.Number7, C, () => Navigate(typeof(ListsPage), null));
        Add(VirtualKey.Number8, C, () => Navigate(typeof(TagsPage), null));
        Add(VirtualKey.F, C, () => SearchBox.Focus(FocusState.Keyboard));
        Add((VirtualKey)188, C, () => Navigate(typeof(SettingsPage), null));      // Ctrl+,
        Add((VirtualKey)191, C, () => Navigate(typeof(ShortcutsPage), null));     // Ctrl+/
        Add(VirtualKey.N, C, () =>
        {
            if (ContentFrame.Content is INewTaskTarget target) target.FocusNewTask();
            else App.Instance!.ShowQuickAdd();
        });
        Add(VirtualKey.W, C, HideToTray);
        Add(VirtualKey.Q, C, () => App.Instance!.Exit());
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => { if (ContentFrame.CanGoBack) ContentFrame.GoBack(); });
    }
}

/// <summary>Pages that rebuild code-drawn visuals when the theme changes.</summary>
public interface IThemeAware { void OnThemeChanged(); }

/// <summary>Pages with their own add-task box (Ctrl+N focuses it).</summary>
public interface INewTaskTarget { void FocusNewTask(); }
