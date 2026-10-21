using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DropFlow.App.Views;
using DropFlow.Core.App;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Runner;
using DropFlow.Engines.Common;
using DropFlow.Overlay;
using WinRT;

namespace DropFlow.App.Services;

/// <summary>The file actions (Control wheel on Tangerine, Alt here): copy, move, copy path, terminal, share, float.</summary>
internal sealed class ActionService(
    ILocalizer loc, Func<string, Task> toast, Overlay.ShelfModel shelf, Action showShelf, Func<IReadOnlyList<string>, Task> share,
    Action<IReadOnlyList<string>> sendToPhone)
{
    public static readonly string[] SupportedIds = ["act.share", "act.float", "act.copy", "act.move", "act.copy-path", "act.terminal", "act.send-phone"];

    public async Task ExecuteAsync(string actionId, IReadOnlyList<string> files)
    {
        try
        {
            switch (actionId)
            {
                case "act.copy":
                    Clipboard.SetFileDropList([.. files]);
                    await toast(loc.Format("action.copied", files.Count));
                    break;

                case "act.copy-path":
                    Clipboard.SetText(FileActionPlans.PathLines(files));
                    await toast(loc["action.pathCopied"]);
                    break;

                case "act.move":
                    await MoveAsync(files);
                    break;

                case "act.terminal":
                    OpenTerminal(files[0]);
                    break;

                case "act.float":
                    shelf.Add(files);
                    showShelf();
                    await toast(loc["action.shelfAdded"]);
                    break;

                case "act.share":
                    await share(files);
                    break;

                case "act.send-phone":
                    sendToPhone(files);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            FileLog.Error("action " + actionId, ex);
            await toast(ex.Message);
        }
    }

    private async Task MoveAsync(IReadOnlyList<string> files)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = loc["action.moveTo"] };
        if (dialog.ShowDialog() != true) return;

        var steps = FileActionPlans.PlanMove(files, dialog.FolderName);
        var moved = 0;
        foreach (var step in steps)
        {
            if (Directory.Exists(step.From)) Directory.Move(step.From, step.To);
            else File.Move(step.From, step.To);
            moved++;
        }
        await toast(loc.Format("action.moved", moved));
    }

    private void OpenTerminal(string path)
    {
        var folder = FileActionPlans.TerminalFolder(path);
        var wtAvailable = new EngineLocator().ExistsOnPath("wt.exe");
        var plan = FileActionPlans.Terminal(folder, wtAvailable);
        var psi = new ProcessStartInfo(plan.FileName) { UseShellExecute = true, WorkingDirectory = folder };
        foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}

internal static class LocatorExtensions
{
    /// <summary>True when an executable name is found in any PATH folder or the Windows Apps alias folder (where wt.exe lives).</summary>
    public static bool ExistsOnPath(this EngineLocator _, string exe)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        return dirs.Append(alias).Any(d => File.Exists(Path.Combine(d, exe)));
    }
}

/// <summary>Shows the Windows share sheet for files. Unverified on real hardware; any failure is reported, never thrown.</summary>
internal static class ShareService
{
    public static async Task<bool> ShareAsync(nint hwnd, IReadOnlyList<string> files)
    {
        try
        {
            var items = new List<Windows.Storage.IStorageItem>();
            foreach (var f in files)
            {
                if (Directory.Exists(f)) items.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(f));
                else items.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(f));
            }

            var interop = Windows.ApplicationModel.DataTransfer.DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
            var ptr = interop.GetForWindow(hwnd, ref iid);
            var manager = WinRT.MarshalInterface<Windows.ApplicationModel.DataTransfer.DataTransferManager>.FromAbi(ptr);
            manager.DataRequested += (_, args) =>
            {
                args.Request.Data.Properties.Title = "DropFlow";
                args.Request.Data.SetStorageItems(items);
            };
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or IOException or NotSupportedException or PlatformNotSupportedException or System.Runtime.InteropServices.MarshalDirectiveException or ArgumentException)
        {
            FileLog.Error("share", ex);
            return false;
        }
    }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow(nint appWindow, ref Guid riid);
        void ShowShareUIForWindow(nint appWindow);
    }
}

/// <summary>Dialogs the runner needs, marshalled to the UI thread.</summary>
internal sealed class UserPrompts(System.Windows.Threading.Dispatcher ui, ILocalizer loc, Func<string, Task> toast, Func<bool> offerInstalls) : IUserPrompts
{
    public Task<IReadOnlyDictionary<string, object?>?> AskOptionsAsync(string operation, string title, IReadOnlyList<ToolOption> options) =>
        ui.InvokeAsync(() => ToolOptionsDialog.Ask(title, operation, options, loc)).Task;

    /// <summary>
    /// By default nothing is offered for download: the user is told what the tool needs and where to install it if they want to
    /// (Settings > Engines). Only when "offer installs" is on does a dialog ask.
    /// </summary>
    public Task<bool> ConfirmEngineInstallAsync(EngineId engine, int approxSizeMb)
    {
        if (!offerInstalls())
        {
            _ = toast(loc.Format("engine.notOffered", loc["engine.name." + engine]));
            return Task.FromResult(false);
        }
        return ConfirmInstallDialogAsync(engine, approxSizeMb);
    }

    private Task<bool> ConfirmInstallDialogAsync(EngineId engine, int approxSizeMb) =>
        ui.InvokeAsync(() =>
        {
            var name = loc["engine.name." + engine];
            return MessageBox.Show(loc.Format("engine.confirm.text", name, approxSizeMb), loc.Format("engine.confirm.title", name),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes,
                loc.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None) == MessageBoxResult.Yes;
        }).Task;

    public Task ShowErrorAsync(string message) => toast(message);
}

/// <summary>Installs engines through winget with a small progress window.</summary>
internal sealed class EngineInstallService(System.Windows.Threading.Dispatcher ui, UiKit kit, EngineInstaller installer, EngineLocator locator) : IEngineInstallService
{
    public async Task<bool> InstallAsync(EngineId engine, IProgress<string> log, CancellationToken cancellationToken)
    {
        InstallWindow? window = null;
        await ui.InvokeAsync(() => { window = new InstallWindow(kit, engine); window.Show(); });
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window!.Token);
            var progress = new Progress<string>(line => { window.Log(line); log.Report(line); });
            var result = await installer.InstallAsync(engine, progress, linked.Token);
            locator.Refresh();
            FileLog.Info($"install {engine}: exit {result.ExitCode}");
            return locator.Locate(engine) is not null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            FileLog.Error("winget not available", ex);
            return false;
        }
        finally
        {
            await ui.InvokeAsync(() => window?.Close());
        }
    }
}
