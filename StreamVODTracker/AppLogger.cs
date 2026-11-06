using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StreamVODTracker;

internal static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamVODTracker", "logs");

    public static string LogDirectory => DirectoryPath;

    public static void Info(string category, string message) => Write("INFO", category, message, null);
    public static void Warn(string category, string message) => Write("WARN", category, message, null);
    public static void Error(string category, string message, Exception? exception = null) => Write("ERROR", category, message, exception);

    private static void Write(string level, string category, string message, Exception? exception)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] [").Append(category).Append("] ")
            .AppendLine(message);
        if (exception is not null) entry.AppendLine(exception.ToString());
        var text = entry.ToString();

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var filePath = Path.Combine(DirectoryPath, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(filePath, text, new UTF8Encoding(false));
            }
        }
        catch (Exception logException)
        {
            Trace.WriteLine($"Could not write application log: {logException.Message}\n{text}");
        }
    }
}
