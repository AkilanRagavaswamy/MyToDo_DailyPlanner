using Windows.Storage;

namespace MyTodo.App.Helpers;

/// <summary>Local log file only (no remote logging, section 5.3).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    private static string PathOrNull()
    {
        if (_path != null) return _path;
        var dir = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "logs");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "app.log");
        return _path;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message + (ex != null ? Environment.NewLine + ex : ""));

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = PathOrNull();
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
