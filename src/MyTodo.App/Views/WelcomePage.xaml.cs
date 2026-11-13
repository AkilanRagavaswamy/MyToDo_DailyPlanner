using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyTodo.App.Services;
using MyTodo.Core.Models;

namespace MyTodo.App.Views;

/// <summary>First run: theme, launch at startup and reminder sound. Google sign-in is added in 1.1.0.0.</summary>
public sealed partial class WelcomePage : Page
{
    private readonly AppServices _s = App.Services;

    public WelcomePage()
    {
        InitializeComponent();
        ThemeRadio.SelectedIndex = 2;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        _s.Settings.Theme = ThemeRadio.SelectedIndex switch { 0 => Core.Models.ThemeChoice.Light, 1 => Core.Models.ThemeChoice.Dark, _ => Core.Models.ThemeChoice.System };
        if (App.Instance?.MainWindow?.Content is FrameworkElement root) ThemeService.Apply(root, _s.Settings.Theme);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var s = _s.Settings;
        s.ReminderSound = SoundToggle.IsOn;
        var error = await StartupService.SetEnabledAsync(StartupToggle.IsOn);
        s.LaunchAtStartup = StartupToggle.IsOn && error == null;
        s.FirstRunDone = true;
        _s.SaveSettings();
        _s.Reminders.Reschedule();
        App.Instance!.MainWindow!.Navigate(typeof(TodayPage), null);
    }
}
