using System.Windows.Threading;
using DropFlow.App.Views;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Overlay;

namespace DropFlow.App.Tests;

public class InstallWindowTests
{
    [Fact]
    public void Log_lines_from_a_background_thread_reach_the_window_without_crashing()
    {
        // The owner's first run crashed here: winget output is reported from a thread-pool thread.
        var ok = Sta.Run(() =>
        {
            var window = new InstallWindow(new UiKit(WheelTheme.FromSystem(), JsonLocalizer.Load("en")), EngineId.LibreOffice);
            window.Show();
            Exception? error = null;
            var worker = new Thread(() =>
            {
                try { window.Log("Downloading 12 %"); }
                catch (Exception ex) { error = ex; }
            });
            worker.Start();
            worker.Join();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.Close();
            return error is null;
        });

        Assert.True(ok);
    }
}
