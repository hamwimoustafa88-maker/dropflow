// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop;

/// <summary>
/// DropFlow for macOS and Linux. One process per user: a second launch (a file manager's "Open with DropFlow", a second double-click)
/// hands its arguments to the running one over a Unix domain socket and exits.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var dirs = AppDirectories.Current;
        Log.Init(dirs.Logs);
        var command = DesktopCommandLine.Parse(args, Environment.CurrentDirectory);

        if (command.Kind == DesktopCommandKind.Snapshot)
            return BuildApp(new DesktopApp(command, null, dirs)).StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

        var instance = InstanceChannel.TryBecomePrimary(dirs.InstanceSocket);
        if (instance is null)
        {
            // Another DropFlow is running: forward (a plain start opens its settings) and leave.
            var forward = command.Kind == DesktopCommandKind.Start ? command with { Kind = DesktopCommandKind.Settings } : command;
            if (InstanceChannel.Forward(dirs.InstanceSocket, DesktopCommandLine.Build(forward), TimeSpan.FromSeconds(3))) return 0;
            Log.Error("another instance holds the lock but does not answer");
            return 1;
        }

        using (instance)
        {
            try
            {
                return BuildApp(new DesktopApp(command, instance, dirs)).StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception ex)
            {
                Log.Error("fatal", ex);
                throw;
            }
        }
    }

    private static AppBuilder BuildApp(DesktopApp app) =>
        AppBuilder.Configure(() => app)
            .UsePlatformDetect()
            // A menu-bar app: no Dock icon (Info.plist also sets LSUIElement).
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .With(new X11PlatformOptions { WmClass = "dropflow" })
            .LogToTrace();

    /// <summary>Used by Avalonia's designer tooling only.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new DesktopApp(new DesktopCommand(DesktopCommandKind.Start, []), null, AppDirectories.Current)).UsePlatformDetect();
}
