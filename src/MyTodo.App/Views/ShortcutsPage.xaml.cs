using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyTodo.App.Helpers;

namespace MyTodo.App.Views;

/// <summary>Every keyboard shortcut, grouped as in the screen designs.</summary>
public sealed partial class ShortcutsPage : Page
{
    /// <summary>Cards narrower than this read as a cramped column of wrapped labels.</summary>
    private const double MinCardWidth = 340;
    private const int MaxColumns = 3;

    private readonly List<Border> _cards = new();
    private int _columns;

    public ShortcutsPage()
    {
        InitializeComponent();
        var hotkey = App.Services.Settings.QuickAddHotkey.Split('+');
        var groups = new (string title, string note, (string label, string[] keys)[] items)[]
        {
            ("Anywhere in Windows", "Change it in Settings › Startup and tray.", new[] { ("Quick add a task", hotkey), ("Windows notification centre", new[] { "Win", "N" }) }),
            ("Navigation", "", new[] { ("Today", new[] { "Ctrl", "1" }), ("Board", new[] { "Ctrl", "2" }), ("Calendar", new[] { "Ctrl", "3" }), ("Diary", new[] { "Ctrl", "4" }), ("Report", new[] { "Ctrl", "5" }), ("Insights", new[] { "Ctrl", "6" }), ("Lists", new[] { "Ctrl", "7" }), ("Tags", new[] { "Ctrl", "8" }), ("Search", new[] { "Ctrl", "F" }), ("Settings", new[] { "Ctrl", "," }), ("Keyboard shortcuts", new[] { "Ctrl", "/" }), ("Go back", new[] { "Alt", "←" }) }),
            ("Tasks", "", new[] { ("New task", new[] { "Ctrl", "N" }), ("Complete or reopen", new[] { "Ctrl", "Enter" }), ("Set due date", new[] { "Ctrl", "D" }), ("Start or hold (runs the timer)", new[] { "Ctrl", "T" }), ("Attach a file", new[] { "Ctrl", "Shift", "A" }), ("Delete", new[] { "Delete" }), ("Close the details pane", new[] { "Esc" }) }),
            ("Calendar", "", new[] { ("Go to today", new[] { "Home" }), ("Previous or next period", new[] { "Ctrl", "← / →" }), ("Day view", new[] { "Alt", "1" }), ("Work week view", new[] { "Alt", "2" }), ("Week view", new[] { "Alt", "3" }), ("Month view", new[] { "Alt", "4" }), ("New event", new[] { "Ctrl", "Shift", "N" }), ("Move event by 15 minutes", new[] { "Alt", "↑ / ↓" }), ("Resize event by 15 minutes", new[] { "Shift", "Alt", "↑ / ↓" }), ("Delete event", new[] { "Delete" }) }),
            ("Diary", "", new[] { ("Bold", new[] { "Ctrl", "B" }), ("Italic", new[] { "Ctrl", "I" }), ("Underline", new[] { "Ctrl", "U" }), ("Heading", new[] { "Ctrl", "Alt", "1" }), ("Bulleted list", new[] { "Ctrl", "Shift", "L" }), ("Insert link", new[] { "Ctrl", "K" }), ("Previous or next day", new[] { "Ctrl", "PgUp / PgDn" }) }),
            ("Window", "", new[] { ("Close to the tray", new[] { "Ctrl", "W" }), ("Exit the app", new[] { "Ctrl", "Q" }) })
        };

        foreach (var g in groups) _cards.Add(BuildCard(g.title, g.note, g.items));
        Layout(MaxColumns);
    }

    private static Border BuildCard(string title, string note, (string label, string[] keys)[] items)
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        if (note.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = note, FontSize = 12, Foreground = Ui.Brush("Text2Brush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            });

        for (var i = 0; i < items.Length; i++)
        {
            var (label, keys) = items[i];
            var row = new Grid
            {
                Padding = new Thickness(0, 7, 0, 7),
                BorderBrush = Ui.Brush("LineBrush"),
                // No rule under the last row: it sat just above the card's padding and read as a stray line.
                BorderThickness = new Thickness(0, 0, 0, i == items.Length - 1 ? 0 : 1),
                ColumnSpacing = 10
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
            });

            var keysPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (var k in keys)
                keysPanel.Children.Add(new Border
                {
                    Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    BorderBrush = Ui.Brush("LineBrush"), Background = Ui.Brush("Surface2Brush"),
                    Child = new TextBlock { Text = k, FontSize = 12 }
                });
            Grid.SetColumn(keysPanel, 1);
            row.Children.Add(keysPanel);
            stack.Children.Add(row);
        }

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            // Top-aligned, so a short card does not stretch to the height of a tall neighbour.
            VerticalAlignment = VerticalAlignment.Top,
            Child = stack
        };
    }

    private void Groups_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (width <= 0) return;
        var fits = Math.Clamp((int)((width + 14) / (MinCardWidth + 14)), 1, MaxColumns);
        if (fits != _columns) Layout(fits);
    }

    /// <summary>
    /// Deals the cards down the columns rather than across, so each column grows independently and a
    /// long card never sets the height of its row. Column heights are balanced by running total.
    /// </summary>
    private void Layout(int columns)
    {
        _columns = columns;
        // Detach the cards from their old column first: a re-layout reuses the same Border objects,
        // and adding one to a second parent while the first still holds it throws.
        foreach (var host in Groups.Children.OfType<StackPanel>()) host.Children.Clear();
        Groups.Children.Clear();
        Groups.ColumnDefinitions.Clear();

        var hosts = new List<StackPanel>();
        for (var c = 0; c < columns; c++)
        {
            Groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var host = new StackPanel { Spacing = 14 };
            Grid.SetColumn(host, c);
            hosts.Add(host);
            Groups.Children.Add(host);
        }

        // Weight by row count so the columns come out roughly level.
        var weights = new int[columns];
        foreach (var card in _cards)
        {
            var lightest = Array.IndexOf(weights, weights.Min());
            hosts[lightest].Children.Add(card);
            weights[lightest] += ((StackPanel)card.Child).Children.Count;
        }
    }
}
