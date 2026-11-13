using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MyTodo.Core.Models;
using Windows.UI;

namespace MyTodo.App.Helpers;

public static class Ui
{
    /// <summary>The theme the UI is actually showing (the user's choice, or the system theme).</summary>
    public static ElementTheme EffectiveTheme { get; set; } = ElementTheme.Light;

    private static ResourceDictionary? _brand;

    /// <summary>
    /// Looks a brand brush up in the dictionary for the theme on screen. A plain resource lookup in code returns the
    /// application theme, which differs when the user picks Light or Dark against the system setting.
    /// </summary>
    public static Brush Brush(string key)
    {
        _brand ??= Application.Current.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.ThemeDictionaries.TryGetValue("Light", out var light) && light is ResourceDictionary ld && ld.ContainsKey("SurfaceBrush"));
        var themeKey = EffectiveTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (_brand?.ThemeDictionaries[themeKey] is ResourceDictionary d && d.TryGetValue(key, out var value) && value is Brush b) return b;
        return Application.Current.Resources.TryGetValue(key, out var v) && v is Brush any ? any : new SolidColorBrush(Colors.Gray);
    }

    public static SolidColorBrush Hex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        var argb = Convert.ToUInt32(h, 16);
        return new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    public static (Brush fill, Brush text) EventBrushes(EventKind kind) => kind switch
    {
        EventKind.Meeting => (Brush("MeetingFillBrush"), Brush("MeetingTextBrush")),
        EventKind.Focus => (Brush("FocusFillBrush"), Brush("FocusTextBrush")),
        _ => (Brush("PersonalFillBrush"), Brush("PersonalTextBrush"))
    };

    public static Brush PriorityBrush(int priority) => priority switch
    {
        1 => Brush("DangerBrush"),
        2 => Brush("AmberBrush"),
        _ => Brush("Text2Brush")
    };

    public static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
    public static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>"Today", "Tomorrow", "Fri 13 Nov".</summary>
    public static string FriendlyDate(DateTime localDate)
    {
        var d = DateOnly.FromDateTime(localDate);
        if (d == Today) return "Today";
        if (d == Today.AddDays(1)) return "Tomorrow";
        if (d == Today.AddDays(-1)) return "Yesterday";
        return localDate.ToString(d.Year == Today.Year ? "ddd d MMM" : "ddd d MMM yyyy");
    }

    public static string DueText(TodoTask t)
    {
        if (t.DueUtc is not DateTime due) return "No date";
        var local = Local(due);
        return t.DueHasTime ? $"{FriendlyDate(local)}, {local:HH:mm}" : FriendlyDate(local);
    }
}
