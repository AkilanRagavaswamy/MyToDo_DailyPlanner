using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MyTodo.App.Helpers;

namespace MyTodo.App.Services;

/// <summary>Tray icon with Quick add, Open, Today, Exit (WIN-01). Sync now arrives in 1.1.0.0.</summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _icon;

    public void Create(Action open, Action today, Action quickAdd, Action exit, string hotkey)
    {
        // Rendered by Windows as a native popup menu, not as a XAML flyout. ContextMenuMode.SecondWindow
        // draws the nicer WinUI menu, but H.NotifyIcon hosts it in a small window of its own and the
        // menu is clipped to that window: the labels were cut off across, and the list was cut off
        // below the third item. The library documents that mode as preview and untested, and offers no
        // way to size the host. A native menu measures itself against the screen, so it cannot be
        // clipped, and it scrolls by itself if it ever grows taller than the display.
        var menu = new MenuFlyout();

        // A native menu right-aligns whatever follows a tab, which is how the shortcut is shown.
        MenuFlyoutItem Item(string text, Action run) => new() { Text = text, Command = new RelayCommand(run) };

        menu.Items.Add(Item($"Quick add\t{hotkey}", quickAdd));
        menu.Items.Add(Item("Open My ToDo", open));
        menu.Items.Add(Item("Today", today));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Exit", exit));

        _icon = new TaskbarIcon
        {
            ToolTipText = "My ToDo : Daily Planner",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcon.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(open),
            NoLeftClickDelay = true
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}

/// <summary>Global quick-add hotkey (WIN-03) through RegisterHotKey on the main window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4D54; // "MT"
    private IntPtr _hwnd;
    private Win32.SubclassProc? _proc; // kept alive for the lifetime of the subclass
    private bool _registered;

    public event Action? Pressed;
    public string? Current { get; private set; }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        Win32.SetWindowSubclass(hwnd, _proc, 1, IntPtr.Zero);
    }

    /// <summary>Registers "Ctrl+Alt+T" style combinations. Returns false when another app or Windows owns it.</summary>
    public bool Register(string combo)
    {
        Unregister();
        if (_hwnd == IntPtr.Zero || !TryParse(combo, out var mods, out var vk)) return false;
        _registered = Win32.RegisterHotKey(_hwnd, HotkeyId, mods | Win32.MOD_NOREPEAT, vk);
        Current = _registered ? combo : null;
        if (!_registered) Log.Info($"Hotkey {combo} is in use.");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) Win32.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "win": mods |= Win32.MOD_WIN; break;
                case "ctrl": mods |= Win32.MOD_CONTROL; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "space": vk = 0x20; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0])) vk = char.ToUpperInvariant(part[0]);
                    else return false;
                    break;
            }
        }
        return mods != 0 && vk != 0;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            return IntPtr.Zero;
        }
        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
        if (_proc != null && _hwnd != IntPtr.Zero) Win32.RemoveWindowSubclass(_hwnd, _proc, 1);
    }
}
