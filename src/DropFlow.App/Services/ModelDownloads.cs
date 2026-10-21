using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DropFlow.AI.Models;
using DropFlow.App.Views;
using DropFlow.Core.Localization;

namespace DropFlow.App.Services;

/// <summary>
/// Gets an AI model onto this PC, but only after the user says yes: a dialog names the model, its size, where it comes from and its
/// licence; the download is resumable, checked against a pinned SHA-256, and never touches the user's files.
/// </summary>
internal sealed class ModelDownloadService(Dispatcher ui, UiKit kit, ModelManager models)
{
    private ILocalizer Loc => kit.Loc;

    /// <summary>True when the model is installed (already, or after a confirmed download). False when declined, cancelled or failed.</summary>
    public async Task<bool> EnsureAsync(string modelId, string toolName)
    {
        var plan = models.Find(modelId) is null ? null : models.GetDownloadPlan(modelId);
        if (plan is null) return false;
        if (plan.IsInstalled) return true;

        // An entry without a pinned hash is never downloaded implicitly.
        if (plan.RequiresUnverifiedConsent)
        {
            FileLog.Info($"model {modelId} has no pinned hash; not downloading");
            return false;
        }

        var model = plan.Model;
        var sizeMb = Math.Max(1, (int)Math.Round(plan.RemainingBytes / 1_048_576.0));
        var accepted = await ui.InvokeAsync(() => MessageBox.Show(
            Loc.Format("model.confirm.text", toolName, model.DisplayName, sizeMb, model.Url.Host, model.License),
            Loc.Format("model.confirm.title", model.DisplayName),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes,
            Loc.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None) == MessageBoxResult.Yes);
        if (!accepted) return false;

        ModelDownloadWindow? window = null;
        await ui.InvokeAsync(() => { window = new ModelDownloadWindow(kit, model); window.Show(); });
        try
        {
            var progress = new Progress<ModelDownloadProgress>(p => window!.Report(p));
            await models.DownloadAsync(modelId, progress, window!.Token);
            FileLog.Info($"model {modelId} installed");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is ModelDownloadException or IOException or HttpRequestException)
        {
            FileLog.Error($"model download {modelId}", ex);
            await ui.InvokeAsync(() => MessageBox.Show(Loc.Format("model.failed", ex.Message), model.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning));
            return false;
        }
        finally
        {
            await ui.InvokeAsync(() => window?.Close());
        }
    }
}
