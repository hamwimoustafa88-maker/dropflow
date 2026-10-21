using DropFlow.AI.Models;

namespace DropFlow.Cli;

/// <summary>
/// <c>dropflow models list | download &lt;id&gt; [--yes] | delete &lt;id&gt;</c>. Downloading is never implicit: without <c>--yes</c> the command only
/// shows what would be fetched (name, size, source, licence) and downloads nothing.
/// </summary>
internal static class ModelsCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, ModelManager models, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var verb = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        var yes = rest.RemoveAll(a => a is "--yes" or "-y") > 0;

        switch (verb)
        {
            case "list":
                await stdout.WriteLineAsync($"{"id",-26}{"for",-18}{"size",9}  {"state",-15}licence");
                foreach (var m in models.Catalog)
                    await stdout.WriteLineAsync($"{m.Id,-26}{m.Purpose,-18}{m.SizeBytes / 1_048_576.0,6:0} MB  {(models.IsInstalled(m.Id) ? "installed" : "not downloaded"),-15}{m.License}");
                await stdout.WriteLineAsync($"\nFolder: {models.Directory}");
                return 0;

            case "download":
                return await DownloadAsync(rest, yes, models, stdout, stderr, ct);

            case "delete":
                if (rest.Count != 1 || models.Find(rest[0]) is null) { await stderr.WriteLineAsync("Usage: dropflow models delete <id>. See 'dropflow models list'."); return 2; }
                await stdout.WriteLineAsync(models.Delete(rest[0]) ? $"Deleted {rest[0]}." : $"{rest[0]} was not installed.");
                return 0;

            default:
                await stderr.WriteLineAsync("Usage: dropflow models list | download <id> [--yes] | delete <id>");
                return 2;
        }
    }

    private static async Task<int> DownloadAsync(List<string> rest, bool yes, ModelManager models, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (rest.Count != 1 || models.Find(rest[0]) is not { } model)
        {
            await stderr.WriteLineAsync("Usage: dropflow models download <id> [--yes]. See 'dropflow models list'.");
            return 2;
        }

        var plan = models.GetDownloadPlan(model.Id);
        if (plan.IsInstalled)
        {
            await stdout.WriteLineAsync($"{model.Id} is already installed.");
            return 0;
        }
        if (plan.RequiresUnverifiedConsent)
        {
            await stderr.WriteLineAsync($"{model.Id} has no pinned SHA-256 and is not downloaded.");
            return 1;
        }

        await stdout.WriteLineAsync($"{model.DisplayName} ({model.Id}): {plan.RemainingBytes / 1_048_576.0:0.#} MB from {model.Url.Host}, licence {model.License}.");
        await stdout.WriteLineAsync($"Source: {model.SourcePage}");
        if (!yes)
        {
            await stdout.WriteLineAsync("Nothing was downloaded. Add --yes to download it (the file is checked against a pinned SHA-256).");
            return 2;
        }

        var last = -1;
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            var percent = (int)(p.Fraction * 100);
            if (percent == last && p.Phase == ModelDownloadPhase.Downloading) return;
            last = percent;
            stderr.Write($"\r{p.Phase} {percent}%   ");
        });
        try
        {
            await models.DownloadAsync(model.Id, progress, ct);
            await stderr.WriteLineAsync();
            await stdout.WriteLineAsync($"Installed {model.Id} in {models.Directory}");
            return 0;
        }
        catch (ModelDownloadException ex)
        {
            await stderr.WriteLineAsync();
            await stderr.WriteLineAsync($"Download failed ({ex.Kind}): {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("\nCancelled. The partial file is kept; run the command again to resume.");
            return 1;
        }
    }
}
