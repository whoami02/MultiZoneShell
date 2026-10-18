using System.IO;

namespace MultiZoneShell.Infrastructure;

internal static class AppPaths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiZoneShell");

    public static readonly string Logs = Path.Combine(Data, "logs");
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = Path.Combine(AppPaths.Logs, $"shell-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
                if (ex != null) line += Environment.NewLine + ex;
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the shell.
        }
    }
}
