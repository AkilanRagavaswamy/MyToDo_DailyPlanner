using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MyTodo.App.Helpers;
using MyTodo.Core.Calendar;
using MyTodo.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MyTodo.App.Controls;

/// <summary>
/// Day / work-week / week timeline (section 9.2): a time gutter and one Canvas per day. Events are positioned by
/// minutes × pixels-per-minute, overlapping events sit side by side, and moving or resizing snaps to 15 minutes.
/// Only the visible range is loaded by the page.
/// </summary>
public sealed class WeekTimeline : UserControl
{
    public double HourHeight { get; set; } = 44;
    public int SnapMinutes { get; set; } = 15;
    public bool ShowHeader { get; set; } = true;
    public int WorkStartMinutes { get; set; } = 9 * 60;
    public int WorkEndMinutes { get; set; } = 18 * 60;

    public DateOnly StartDate { get; private set; } = Ui.Today;
    public int Days { get; private set; } = 7;

    /// <summary>(occurrence, new start UTC, new end UTC)</summary>
    public event Action<EventOccurrence, DateTime, DateTime>? EventMoved;
    public event Action<EventOccurrence>? EventInvoked;
    public event Action<EventOccurrence>? EventDeleteRequested;
    public event Action<DateTime>? CreateRequested;
    /// <summary>(task id, block start UTC)</summary>
    public event Action<Guid, DateTime>? TaskDropped;

    private const double Gutter = 52;
    private readonly Grid _root = new();
    private readonly Grid _header = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Grid _body = new();
    private readonly List<Canvas> _dayCanvases = new();
    private IReadOnlyList<EventOccurrence> _occurrences = Array.Empty<EventOccurrence>();
    private bool _scrolledOnce;

    // drag state
    private Button? _dragBlock;
    private EventOccurrence? _dragOcc;
    private bool _resizing, _dragged;
    private double _pressY, _origTop, _origHeight;
    private int _dragDay;

    public WeekTimeline()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_scroll, 1);
        _scroll.Content = _body;
        _root.Children.Add(_header);
        _root.Children.Add(_scroll);
        Content = _root;
    }

    public void Show(DateOnly start, int days, IReadOnlyList<EventOccurrence> occurrences)
    {
        StartDate = start;
        Days = days;
        _occurrences = occurrences;
        Build();
    }

    public void Refresh() => Build();

    private DateOnly DayAt(int i) => StartDate.AddDays(i);

    private void Build()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        _body.Children.Clear();
        _body.ColumnDefinitions.Clear();
        _dayCanvases.Clear();
        _body.Height = 24 * HourHeight + 1;
        _header.Visibility = ShowHeader ? Visibility.Visible : Visibility.Collapsed;

        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
        for (var i = 0; i < Days; i++)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Header: weekday + date, today highlighted.
        for (var i = 0; i < Days; i++)
        {
            var d = DayAt(i);
            var isToday = d == Ui.Today;
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 8, 0, 8), Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = d.ToString("ddd"), FontSize = 12, Foreground = Ui.Brush("Text2Brush"), HorizontalAlignment = HorizontalAlignment.Center });
            var num = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), HorizontalAlignment = HorizontalAlignment.Center,
                Background = isToday ? Ui.Brush("BrandAccentFillBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = new TextBlock
                {
                    Text = d.Day.ToString(), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isToday ? Ui.Brush("OnAccentBrush") : Ui.Brush("TextBrush")
                }
            };
            stack.Children.Add(num);
            Grid.SetColumn(stack, i + 1);
            _header.Children.Add(stack);
        }

        // Gutter with hour labels.
        var gutter = new Canvas();
        for (var h = 1; h < 24; h++)
        {
            var label = new TextBlock { Text = $"{h}:00", FontSize = 11, Foreground = Ui.Brush("Text2Brush"), Width = Gutter - 8, TextAlignment = TextAlignment.Right };
            Canvas.SetTop(label, h * HourHeight - 8);
            gutter.Children.Add(label);
        }
        _body.Children.Add(gutter);

        for (var i = 0; i < Days; i++)
        {
            var day = DayAt(i);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var canvas = new Canvas
            {
                Background = weekend ? Ui.Brush("WeekendBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                AllowDrop = true, Tag = i
            };
            canvas.SizeChanged += (_, _) => RenderDay(canvas);
            canvas.DoubleTapped += Canvas_DoubleTapped;
            canvas.DragOver += Canvas_DragOver;
            canvas.Drop += Canvas_Drop;
            Grid.SetColumn(canvas, i + 1);
            _body.Children.Add(canvas);
            _dayCanvases.Add(canvas);
            var line = new Rectangle { Width = 1, Fill = Ui.Brush("LineBrush"), HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(line, i + 1);
            _body.Children.Add(line);
        }

        if (!_scrolledOnce)
        {
            _scrolledOnce = true;
            _scroll.Loaded += (_, _) => _scroll.ChangeView(null, Math.Max(0, WorkStartMinutes / 60.0 * HourHeight - 24), null, true);
        }
    }

    private void RenderDay(Canvas canvas)
    {
        canvas.Children.Clear();
        var i = (int)canvas.Tag;
        var width = canvas.ActualWidth;
        if (width <= 0) return;
        var dayStartUtc = Ui.ToUtc(DayAt(i).ToDateTime(TimeOnly.MinValue));
        var dayEndUtc = Ui.ToUtc(DayAt(i).AddDays(1).ToDateTime(TimeOnly.MinValue));

        for (var h = 0; h <= 24; h++)
        {
            var line = new Rectangle { Width = width, Height = 1, Fill = Ui.Brush("LineBrush"), Opacity = h * 60 >= WorkStartMinutes && h * 60 <= WorkEndMinutes ? 1 : 0.5, IsHitTestVisible = false };
            Canvas.SetTop(line, h * HourHeight);
            canvas.Children.Add(line);
        }

        var dayOccurrences = _occurrences.Where(o => !o.Event.AllDay && o.StartUtc < dayEndUtc && o.EndUtc > dayStartUtc)
            .Select(o => (o, o.StartUtc < dayStartUtc ? dayStartUtc : o.StartUtc, o.EndUtc > dayEndUtc ? dayEndUtc : o.EndUtc));
        foreach (var block in DayLayout.Layout(dayOccurrences))
        {
            var top = (Ui.Local(block.StartUtc) - Ui.Local(dayStartUtc)).TotalMinutes / 60 * HourHeight;
            var height = Math.Max(18, (block.EndUtc - block.StartUtc).TotalMinutes / 60 * HourHeight - 2);
            var colWidth = (width - 6) / block.ColumnCount;
            var button = MakeBlock(block.Item, height, block.Overlaps);
            button.Width = Math.Max(20, colWidth - 2);
            Canvas.SetLeft(button, 3 + block.Column * colWidth);
            Canvas.SetTop(button, top + 1);
            canvas.Children.Add(button);
        }

        // Now line.
        if (DayAt(i) == Ui.Today)
        {
            var now = DateTime.Now;
            var y = now.TimeOfDay.TotalMinutes / 60 * HourHeight;
            var nowLine = new Rectangle { Width = width, Height = 2, Fill = Ui.Brush("NowLineBrush"), IsHitTestVisible = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nowLine, $"Now, {now:HH:mm}");
            Canvas.SetTop(nowLine, y);
            canvas.Children.Add(nowLine);
        }
    }

    private Button MakeBlock(EventOccurrence occ, double height, bool overlaps)
    {
        var (fill, text) = Ui.EventBrushes(occ.Event.Kind);
        var s = Ui.Local(occ.StartUtc);
        var e = Ui.Local(occ.EndUtc);
        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock { Text = occ.Event.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (height > 30)
            stack.Children.Add(new TextBlock { Text = $"{s:HH:mm} – {e:HH:mm}" + (occ.IsRecurring ? " · repeats" : "") + (overlaps ? " · overlaps" : ""), FontSize = 11, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Content = stack, Height = height, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
            Background = fill, BorderBrush = overlaps ? Ui.Brush("DangerBrush") : fill, BorderThickness = new Thickness(overlaps ? 2 : 1),
            Opacity = occ.Event.DoneUtc != null ? 0.6 : 1, Tag = occ
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{occ.Event.Title}, {s:dddd HH:mm} to {e:HH:mm}{(overlaps ? ", overlaps another event" : "")}");
        if (overlaps) ToolTipService.SetToolTip(button, "This overlaps another event.");

        button.AddHandler(PointerPressedEvent, new PointerEventHandler(Block_PointerPressed), true);
        button.AddHandler(PointerMovedEvent, new PointerEventHandler(Block_PointerMoved), true);
        button.AddHandler(PointerReleasedEvent, new PointerEventHandler(Block_PointerReleased), true);
        button.Click += (_, _) => { if (!_dragged) EventInvoked?.Invoke(occ); };
        button.KeyDown += Block_KeyDown;
        return button;
    }

    // ---------------- drag to move / resize (CAL-04) ----------------

    private void Block_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not EventOccurrence occ) return;
        var p = e.GetCurrentPoint(b);
        if (!p.Properties.IsLeftButtonPressed) return;
        _dragBlock = b;
        _dragOcc = occ;
        _dragged = false;
        _resizing = p.Position.Y > b.ActualHeight - 8;
        _pressY = e.GetCurrentPoint(_body).Position.Y;
        _origTop = Canvas.GetTop(b);
        _origHeight = b.Height;
        _dragDay = b.Parent is Canvas c ? (int)c.Tag : 0;
        b.CapturePointer(e.Pointer);
    }

    private void Block_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragBlock == null || sender != _dragBlock) return;
        var pos = e.GetCurrentPoint(_body).Position;
        var dy = pos.Y - _pressY;
        if (!_dragged && Math.Abs(dy) < 4) return;
        _dragged = true;
        var snapPx = SnapMinutes / 60.0 * HourHeight;
        if (_resizing)
            _dragBlock.Height = Math.Max(snapPx, Math.Round((_origHeight + dy) / snapPx) * snapPx - 2);
        else
            Canvas.SetTop(_dragBlock, Math.Clamp(Math.Round((_origTop + dy) / snapPx) * snapPx + 1, 0, 24 * HourHeight - _dragBlock.Height));
        var dayWidth = (_body.ActualWidth - Gutter) / Math.Max(1, Days);
        _dragDay = Math.Clamp((int)((pos.X - Gutter) / dayWidth), 0, Days - 1);
        ProtectedCursor = InputSystemCursor.Create(_resizing ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeAll);
    }

    private void Block_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragBlock == null || _dragOcc == null) return;
        _dragBlock.ReleasePointerCapture(e.Pointer);
        var occ = _dragOcc;
        var block = _dragBlock;
        _dragBlock = null;
        _dragOcc = null;
        ProtectedCursor = null;
        if (!_dragged) return;

        var startMin = DayLayout.Snap((Canvas.GetTop(block) - 1) / HourHeight * 60, SnapMinutes);
        var day = DayAt(_dragDay).ToDateTime(TimeOnly.MinValue);
        if (_resizing)
        {
            var origStartLocal = Ui.Local(occ.StartUtc);
            var endMin = DayLayout.Snap((Canvas.GetTop(block) + block.Height + 2) / HourHeight * 60, SnapMinutes);
            var newEnd = Ui.ToUtc(origStartLocal.Date.AddMinutes(endMin));
            if (newEnd > occ.StartUtc) EventMoved?.Invoke(occ, occ.StartUtc, newEnd);
        }
        else
        {
            var newStart = Ui.ToUtc(day.AddMinutes(startMin));
            EventMoved?.Invoke(occ, newStart, newStart + (occ.EndUtc - occ.StartUtc));
        }
    }

    // Alt+Up/Down moves by 15 minutes, Shift+Alt+Up/Down resizes, Delete removes.
    private void Block_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button { Tag: EventOccurrence occ }) return;
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = TimeSpan.FromMinutes(SnapMinutes);
        if (e.Key == VirtualKey.Delete) { EventDeleteRequested?.Invoke(occ); e.Handled = true; return; }
        if (!alt || (e.Key != VirtualKey.Up && e.Key != VirtualKey.Down)) return;
        var sign = e.Key == VirtualKey.Up ? -1 : 1;
        if (shift)
        {
            var end = occ.EndUtc + sign * step;
            if (end > occ.StartUtc) EventMoved?.Invoke(occ, occ.StartUtc, end);
        }
        else EventMoved?.Invoke(occ, occ.StartUtc + sign * step, occ.EndUtc + sign * step);
        e.Handled = true;
    }

    // ---------------- create and drop (CAL-03) ----------------

    private DateTime SlotAt(Canvas canvas, double y)
    {
        var minutes = Math.Clamp(Math.Floor(y / HourHeight * 60 / SnapMinutes) * SnapMinutes, 0, 24 * 60 - SnapMinutes);
        return Ui.ToUtc(DayAt((int)canvas.Tag).ToDateTime(TimeOnly.MinValue).AddMinutes(minutes));
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is Canvas c && e.OriginalSource == c) CreateRequested?.Invoke(SlotAt(c, e.GetPosition(c).Y));
    }

    private void Canvas_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (sender is Canvas c) e.DragUIOverride.Caption = $"Block time at {Ui.Local(SlotAt(c, e.GetPosition(c).Y)):ddd HH:mm}";
    }

    private async void Canvas_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Canvas c || !e.DataView.Contains(StandardDataFormats.Text)) return;
        var text = await e.DataView.GetTextAsync();
        if (text.StartsWith("task:") && Guid.TryParse(text[5..], out var id))
            TaskDropped?.Invoke(id, SlotAt(c, e.GetPosition(c).Y));
    }
}
