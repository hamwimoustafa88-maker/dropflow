using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DropFlow.Core.Operations;
using DropFlow.Core.Session;
using DropFlow.Overlay;

namespace DropFlow.App.Services;

/// <summary>Where DropFlow keeps its files. Settings always live in the default folder; engines, models and history follow <c>DataDirectory</c>.</summary>
internal sealed class AppPaths(string dataDir)
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow");

    public string DataDir { get; } = dataDir;
    public string SettingsFile => Path.Combine(DefaultDir, "settings.json");
    public string HistoryFile => Path.Combine(DataDir, "history.json");
    public string ModelsDir => Path.Combine(DataDir, "models");
    public string LogDir => Path.Combine(DefaultDir, "logs");
}

/// <summary>A deliberately tiny file logger: one rolling file, never throws.</summary>
internal static class FileLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "dropflow.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000)
                File.Move(_path, Path.Combine(directory, "dropflow.old.log"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _path = null; }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        try
        {
            lock (Gate) File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging must never break the app */ }
    }
}

/// <summary>Runs delayed actions on the UI thread (used for the drag-release grace period and request batching).</summary>
internal sealed class DispatcherScheduler(Dispatcher dispatcher) : IScheduler
{
    private sealed class Handle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    public IDisposable After(TimeSpan delay, Action action)
    {
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(() => After(delay, action));
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
        return new Handle(timer);
    }
}

/// <summary>"Undo" sends outputs to the Recycle Bin instead of deleting them for good.</summary>
internal sealed class RecycleBinDisposer : IFileDisposer
{
    public void Dispose(string path)
    {
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else if (File.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
}

internal static class NativeInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public static PixelPoint CursorPosition() => GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : new PixelPoint(0, 0);

    public static nint Foreground() => GetForegroundWindow();
}

/// <summary>
/// Reads the files currently selected in the foreground Explorer window (or on the desktop) through the shell's COM automation.
/// That lets the wheel show the right wedges the moment a drag starts, before the files have entered it.
/// Best effort: returns an empty list for non-Explorer windows or on any COM failure.
/// </summary>
internal static class ExplorerSelection
{
    public static Task<IReadOnlyList<string>> ReadAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Read()); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or IOException)
            {
                tcs.TrySetResult([]);
            }
        }) { IsBackground = true, Name = "DropFlow.ExplorerSelection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return Task.WhenAny(tcs.Task, Task.Delay(timeout)).ContinueWith(t => t.Result == tcs.Task ? tcs.Task.Result : (IReadOnlyList<string>)[], TaskScheduler.Default);
    }

    private static IReadOnlyList<string> Read()
    {
        var foreground = NativeInput.Foreground();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return [];

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    if ((long)window.HWND != foreground.ToInt64()) continue;
                    var paths = new List<string>();
                    foreach (dynamic item in window.Document.SelectedItems())
                    {
                        string? path = item.Path;
                        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) paths.Add(path);
                    }
                    if (paths.Count > 0) return paths;
                }
                finally { Marshal.ReleaseComObject(window); }
            }

            // The desktop is not in Windows(); ask for it explicitly.
            const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
            object empty = Type.Missing;
            dynamic? desktop = shell.Windows().FindWindowSW(CSIDL_DESKTOP, empty, SWC_DESKTOP, out int hwnd, SWFO_NEEDDISPATCH);
            if (desktop is not null && hwnd != 0)
            {
                var list = new List<string>();
                foreach (dynamic item in desktop.Document.SelectedItems())
                {
                    string? path = item.Path;
                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) list.Add(path);
                }
                return list;
            }
            return [];
        }
        finally { Marshal.ReleaseComObject(shell); }
    }
}
