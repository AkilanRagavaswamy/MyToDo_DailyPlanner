using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyTodo.App.Helpers;
using MyTodo.Core.Models;

namespace MyTodo.App.Controls;

/// <summary>Month view (CAL-01): 6 × 7 lightweight cells with up to two event pills and a "+N more" line.</summary>
public sealed class MonthGrid : UserControl
{
    public event Action<DateOnly>? DaySelected;
    public event Action<DateOnly>? DayOpened;
    public DateOnly Selected { get; private set; } = Ui.Today;

    private readonly Grid _grid = new();

    public MonthGrid() { Content = _grid; }

    public void Show(DateOnly month, DayOfWeek firstDay, IReadOnlyList<EventOccurrence> occurrences, DateOnly selected)
    {
        Selected = selected;
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        for (var c = 0; c < 7; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < 6; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 92 });

        var first = new DateOnly(month.Year, month.Month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek - (int)firstDay + 7) % 7));
        for (var c = 0; c < 7; c++)
        {
            var dow = start.AddDays(c);
            var head = new TextBlock { Text = dow.ToString("ddd"), FontSize = 12, Foreground = Ui.Brush("Text2Brush"), Margin = new Thickness(10, 8, 0, 8) };
            Grid.SetColumn(head, c);
            _grid.Children.Add(head);
        }

        var byDay = occurrences.GroupBy(o => DateOnly.FromDateTime(Ui.Local(o.StartUtc))).ToDictionary(g => g.Key, g => g.OrderBy(o => o.StartUtc).ToList());
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var inMonth = day.Month == month.Month;
            var isToday = day == Ui.Today;
            var isSelected = day == selected;
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new TextBlock
            {
                Text = day.Day.ToString(), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 2),
                Foreground = isToday ? Ui.Brush("BrandAccentBrush") : inMonth ? Ui.Brush("TextBrush") : Ui.Brush("Text2Brush"),
                Opacity = inMonth ? 1 : 0.7
            });
            if (byDay.TryGetValue(day, out var items))
            {
                foreach (var o in items.Take(2))
                {
                    var (fill, text) = Ui.EventBrushes(o.Event.Kind);
                    stack.Children.Add(new Border
                    {
                        Background = fill, CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1),
                        Child = new TextBlock { Text = o.Event.Title, FontSize = 11, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis }
                    });
                }
                if (items.Count > 2)
                    stack.Children.Add(new TextBlock { Text = $"+{items.Count - 2} more", FontSize = 11, Foreground = Ui.Brush("Text2Brush"), Margin = new Thickness(4, 0, 0, 0) });
            }

            var cell = new Button
            {
                Content = stack, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(6), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = Ui.Brush("LineBrush"),
                Background = isSelected ? Ui.Brush("AccentSoftBrush") : weekend ? Ui.Brush("WeekendBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Tag = day
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, $"{day:dddd d MMMM}, {(items?.Count ?? 0)} events");
            cell.Click += (_, _) => DaySelected?.Invoke(day);
            cell.DoubleTapped += (_, _) => DayOpened?.Invoke(day);
            Grid.SetRow(cell, 1 + i / 7);
            Grid.SetColumn(cell, i % 7);
            _grid.Children.Add(cell);
        }
    }
}
