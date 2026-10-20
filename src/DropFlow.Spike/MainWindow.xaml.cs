using System.Windows;
using System.Windows.Threading;
using DropFlow.Hooks;

namespace DropFlow.Spike;

public partial class MainWindow : Window
{
    private readonly DragDetector _detector = LowLevelInputHook.CreateSystemDetector();
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private LowLevelInputHook? _hook;
    private WheelWindow? _wheel;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += (_, _) => _hook?.Dispose();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); _wheel?.HideWheel(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _wheel = new WheelWindow();
        _wheel.Prepare();
        _wheel.FilesDropped += (files, wheel) =>
        {
            Log($"DROP on '{wheel}' wheel: {files.Length} item(s)");
            foreach (var f in files) Log($"   {f}");
        };

        // Hook callbacks run on the hook thread: these handlers only post to the UI thread.
        _detector.DragStarted += p => Post(() => Log($"drag started at {p.X},{p.Y} (dpi {WindowNative.DpiAt(p)})"));
        _detector.DragEnded += p => Post(() => { Log($"drag ended at {p.X},{p.Y}"); _hideTimer.Stop(); _hideTimer.Start(); });
        _detector.DragCancelled += () => Post(() => { Log("drag cancelled (Esc)"); _wheel.HideWheel(); });
        _detector.KeyTapped += (key, p) => Post(() =>
        {
            _hideTimer.Stop();
            Log($"{key} tapped during drag -> {_wheel.ShowAt(p, key)}");
        });

        try
        {
            _hook = new LowLevelInputHook(_detector);
            _hook.Start();
            Status.Text = $"Hooks installed. Drag threshold {NativeDragX()}x{NativeDragY()} px. Process is {(Environment.Is64BitProcess ? "64" : "32")}-bit, OS {Environment.OSVersion.Version}.";
            Log("hooks installed");
        }
        catch (Exception ex)
        {
            Status.Text = "Could not install hooks: " + ex.Message;
            Log("hook install FAILED: " + ex);
        }
    }

    private static int NativeDragX() => (int)SystemParameters.MinimumHorizontalDragDistance;
    private static int NativeDragY() => (int)SystemParameters.MinimumVerticalDragDistance;

    private void Post(Action action) => Dispatcher.BeginInvoke(action);

    private void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void OnClear(object sender, RoutedEventArgs e) => LogBox.Clear();
    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetText(LogBox.Text);
}
