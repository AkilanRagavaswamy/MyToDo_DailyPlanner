using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyTodo.App.Helpers;

namespace MyTodo.App.Views;

/// <summary>Every keyboard shortcut, grouped as in the screen designs.</summary>
public sealed partial class ShortcutsPage : Page
{
    public ShortcutsPage()
    {
        InitializeComponent();
        var hotkey = App.Services.Settings.QuickAddHotkey.Split('+');
        var groups = new (string title, string note, (string label, string[] keys)[] items)[]
        {
            ("Anywhere in Windows", "Change it in Settings › Startup and tray.", new[] { ("Quick add a task", hotkey), ("Windows notification centre", new[] { "Win", "N" }) }),
            ("Navigation", "", new[] { ("Today", new[] { "Ctrl", "1" }), ("Lists", new[] { "Ctrl", "2" }), ("Calendar", new[] { "Ctrl", "3" }), ("Diary", new[] { "Ctrl", "4" }), ("Insights", new[] { "Ctrl", "5" }), ("Search", new[] { "Ctrl", "F" }), ("Settings", new[] { "Ctrl", "," }), ("Keyboard shortcuts", new[] { "Ctrl", "/" }), ("Go back", new[] { "Alt", "←" }) }),
            ("Tasks", "", new[] { ("New task", new[] { "Ctrl", "N" }), ("Complete or reopen", new[] { "Ctrl", "Enter" }), ("Set due date", new[] { "Ctrl", "D" }), ("Start or stop timer", new[] { "Ctrl", "T" }), ("Attach a file", new[] { "Ctrl", "Shift", "A" }), ("Move up or down (manual sort)", new[] { "Alt", "↑ / ↓" }), ("Delete", new[] { "Delete" }), ("Undo (bar shown for 10 s)", new[] { "Undo button" }) }),
            ("Calendar", "", new[] { ("Go to today", new[] { "Home" }), ("Previous or next period", new[] { "Ctrl", "← / →" }), ("Day view", new[] { "Alt", "1" }), ("Work week view", new[] { "Alt", "2" }), ("Week view", new[] { "Alt", "3" }), ("Month view", new[] { "Alt", "4" }), ("New event", new[] { "Ctrl", "Shift", "N" }), ("Move event by 15 minutes", new[] { "Alt", "↑ / ↓" }), ("Resize event by 15 minutes", new[] { "Shift", "Alt", "↑ / ↓" }), ("Delete event", new[] { "Delete" }) }),
            ("Diary", "", new[] { ("Bold", new[] { "Ctrl", "B" }), ("Italic", new[] { "Ctrl", "I" }), ("Underline", new[] { "Ctrl", "U" }), ("Heading", new[] { "Ctrl", "Alt", "1" }), ("Bulleted list", new[] { "Ctrl", "Shift", "L" }), ("Insert link", new[] { "Ctrl", "K" }), ("Previous or next day", new[] { "Ctrl", "PgUp / PgDn" }) }),
            ("Window", "", new[] { ("Close to the tray", new[] { "Ctrl", "W" }), ("Exit the app", new[] { "Ctrl", "Q" }) })
        };
        foreach (var g in groups)
        {
            var stack = new StackPanel { Spacing = 0 };
            stack.Children.Add(new TextBlock { Text = g.title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            if (g.note.Length > 0) stack.Children.Add(new TextBlock { Text = g.note, FontSize = 12, Foreground = Ui.Brush("Text2Brush"), Margin = new Thickness(0, 0, 0, 6) });
            foreach (var (label, keys) in g.items)
            {
                var row = new Grid { Padding = new Thickness(0, 7, 0, 7), BorderBrush = Ui.Brush("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                var keysPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                foreach (var k in keys)
                    keysPanel.Children.Add(new Border
                    {
                        Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2),
                        BorderBrush = Ui.Brush("LineBrush"), Background = Ui.Brush("Surface2Brush"),
                        Child = new TextBlock { Text = k, FontSize = 12 }
                    });
                Grid.SetColumn(keysPanel, 1);
                row.Children.Add(keysPanel);
                stack.Children.Add(row);
            }
            Groups.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardBorderStyle"], Margin = new Thickness(0, 0, 14, 14), Width = 366, Child = stack });
        }
    }
}
