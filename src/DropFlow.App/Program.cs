using System.Windows;
using DropFlow.App.Services;
using DropFlow.Core.App;

namespace DropFlow.App;

public static class Program
{
    private const string InstanceName = "DropFlow.Instance.v1";

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack installer/updater hooks (--veloapp-install etc.) run here and exit; a normal launch continues.
        // Uninstall: remove what DropFlow added to Windows (startup entry, Explorer menu, Send To) so nothing is left behind.
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => SystemIntegration.RemoveAll(Environment.ProcessPath ?? "DropFlow.exe"))
            .Run();

        // DropFlow never runs elevated (CLAUDE.md). An installer or console started "as administrator" would start us elevated, and an
        // elevated instance cannot be reached by normal launches (the next double-click then silently did nothing). Restart unelevated.
        if (args.Length == 0 && Elevation.IsElevated() && Elevation.RelaunchUnelevated(Environment.ProcessPath)) return 0;

        var command = AppCommandLine.Parse(args);
        using var instance = SingleInstance.Acquire(InstanceName);

        if (!instance.IsPrimary)
        {
            // Already running: hand the request over. A bare second launch opens the settings.
            var forwarded = args.Length == 0 ? ["--settings"] : args;
            if (instance.Forward(forwarded, TimeSpan.FromSeconds(3))) return 0;
            ExplainUnreachableInstance();
            return 1;
        }

        var app = new DropFlowApp(command, instance);
        return app.Run();
    }

    /// <summary>The running DropFlow could not be reached (elevated or hung): say so instead of exiting silently.</summary>
    private static void ExplainUnreachableInstance()
    {
        var settings = new DropFlow.Core.Settings.SettingsStore(new AppPaths(AppPaths.DefaultDir).SettingsFile).Load();
        var loc = DropFlow.Core.Localization.JsonLocalizer.Load(
            LanguageResolver.Resolve(settings.Language, System.Globalization.CultureInfo.CurrentUICulture.Name));
        MessageBox.Show(loc["instance.unreachable"], "DropFlow", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

/// <summary>The WPF application: lives in the tray, has no main window.</summary>
internal sealed class DropFlowApp : Application
{
    private readonly AppCommand _startup;
    private readonly SingleInstance _instance;
    private AppHost? _host;

    public DropFlowApp(AppCommand startup, SingleInstance instance)
    {
        _startup = startup;
        _instance = instance;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Error("unhandled UI exception", args.Exception);
            args.Handled = true;           // a bug in one window must not take the tray app down
        };
        TaskScheduler.UnobservedTaskException += (_, args) => { FileLog.Error("unobserved task exception", args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => FileLog.Error("fatal", args.ExceptionObject as Exception);

        _host = new AppHost(Dispatcher);
        _host.Start();

        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() => _host.HandleForwarded(args));
        _instance.StartListening();

        if (_startup.Kind is not (AppCommandKind.Start or AppCommandKind.Quit)) _host.Handle(_startup);
        else if (_startup.Kind == AppCommandKind.Quit) Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
