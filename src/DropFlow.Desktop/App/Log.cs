// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Desktop.App;

/// <summary>A small local log (never sent anywhere): <c>~/Library/Logs/DropFlow</c> or <c>~/.local/state/dropflow/logs</c>.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Init(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _file = Path.Combine(folder, "dropflow-desktop.log");
            if (File.Exists(_file) && new FileInfo(_file).Length > 2_000_000) File.Move(_file, _file + ".old", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_file is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_file, line + "\n"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* never fail because of the log */ }
        }
    }
}
