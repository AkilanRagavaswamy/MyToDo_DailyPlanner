using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MyTodo.App.Helpers;

namespace MyTodo.App.Views;

/// <summary>
/// "Name and colour" — the edit dialog both masters use. Lists and tags differ only in their wording
/// and palette, so sharing it keeps the two pages from drifting apart visually.
/// </summary>
public static class NameColourDialog
{
    public sealed record Result(bool Confirmed, string Name, string Colour);

    public static async Task<Result> ShowAsync(XamlRoot root, string title, string action, string name,
        string colour, IReadOnlyList<string> palette, string placeholder)
    {
        var box = new TextBox { Text = name, PlaceholderText = placeholder, MaxLength = 40, Header = "Name" };
        var chosen = colour;

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var buttons = new List<Button>();
        foreach (var hex in palette)
        {
            var value = hex;
            var button = new Button
            {
                Content = new Ellipse { Width = 20, Height = 20, Fill = Ui.Hex(value) },
                Width = 36, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(18),
                Tag = value, BorderBrush = Ui.Brush("TextBrush"),
                BorderThickness = new Thickness(value == chosen ? 2 : 0)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Colour " + value);
            button.Click += (_, _) =>
            {
                chosen = value;
                foreach (var b in buttons) b.BorderThickness = new Thickness((string)b.Tag! == chosen ? 2 : 0);
            };
            buttons.Add(button);
            swatches.Children.Add(button);
        }

        var panel = new StackPanel { Spacing = 14, Width = 360 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "Colour", FontSize = 12, Foreground = Ui.Brush("Text2Brush") });
        panel.Children.Add(swatches);

        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = panel,
            PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        var ok = await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text);
        return new Result(ok, box.Text.Trim(), chosen);
    }
}
