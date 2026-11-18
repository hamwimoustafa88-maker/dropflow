using System.Globalization;
using System.Text.Json;
using DropFlow.Core.Automation;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Cli;

public enum CliKind { Help, Version, List, Convert, Run, RecipeList, RecipeRun, Engines, Formats, Watch }

public sealed record CliCommand(
    CliKind Kind,
    string? Operation = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyDictionary<string, object?>? Options = null,
    string? OutputDirectory = null,
    string? Category = null,
    bool Json = false,
    string? Recipe = null,
    bool Events = false);

public sealed record CliParseResult(CliCommand? Command, string? Error);

/// <summary>Parses <c>dropflow</c> command lines. Pure: no file system, no output.</summary>
public static class CliParser
{
    public const string Usage = """
        dropflow — convert and process files locally

        USAGE
          dropflow list [--category image|audio|video|pdf|document|slides|data|spreadsheet|epub|subtitle|archive] [--json]
          dropflow formats [--json]                                        (every input format and what it converts to)
          dropflow convert <files...> --to <format> [--opt key=value]... [--out <folder>] [--json | --events]
          dropflow run <tool-id> <files...> [--opt key=value]... [--out <folder>] [--json | --events]
          dropflow watch <folder> --to <format> [--opt key=value]... [--out <folder>]   (converts every file that lands there; Ctrl+C stops)
          dropflow recipe list [--json]
          dropflow recipe run <name> <files...> [--out <folder>] [--json | --events]
          dropflow engines [--json]
          dropflow doctor [--json]                                          (version, Windows, engines and their versions, encoders, models, WebView2)
          dropflow models list | download <id> [--yes] | delete <id>     (AI models; nothing downloads without --yes)
          dropflow version

        Results are written next to each source (or into --out). Originals are never changed.
        --json prints one object at the end; --events prints one JSON object per line while it works
        (start, progress, output, summary), for scripts and AI agents.
        Exit codes: 0 success, 1 a job failed (or a needed engine is missing), 2 usage error (bad arguments, missing file),
        3 nothing to convert (DropFlow has no tool for these files).
        """;

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h" or "/?") return Ok(new CliCommand(CliKind.Help));
        var verb = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();

        switch (verb)
        {
            case "version" or "--version" or "-v":
                return Ok(new CliCommand(CliKind.Version));
            case "engines":
                return WithFlags(rest, CliKind.Engines, positional: 0);
            case "list":
                return WithFlags(rest, CliKind.List, positional: 0);
            case "formats":
                return WithFlags(rest, CliKind.Formats, positional: 0);
            case "watch":
                return Files(rest, CliKind.Watch, needsTarget: true);
            case "convert":
                return Files(rest, CliKind.Convert, needsTarget: true);
            case "run":
                if (rest.Count == 0) return Fail("run needs a tool id (see 'dropflow list').");
                return Files(rest.Skip(1).ToList(), CliKind.Run, needsTarget: false, tool: rest[0]);
            case "recipe":
                if (rest.Count == 0) return Fail("recipe needs 'list' or 'run <name> <files...>'.");
                if (rest[0] == "list") return WithFlags(rest.Skip(1).ToList(), CliKind.RecipeList, positional: 0);
                if (rest[0] == "run" && rest.Count >= 2) return Files(rest.Skip(2).ToList(), CliKind.RecipeRun, needsTarget: false, recipe: rest[1]);
                return Fail("usage: dropflow recipe run <name> <files...>");
            default:
                return Fail($"Unknown command '{args[0]}'. Try 'dropflow help'.");
        }
    }

    private static CliParseResult WithFlags(List<string> args, CliKind kind, int positional)
    {
        var (flags, free, error) = Split(args);
        if (error is not null) return Fail(error);
        if (free.Count != positional) return Fail($"Unexpected argument '{free[0]}'.");
        return Ok(new CliCommand(kind, Category: flags.GetValueOrDefault("category"), Json: flags.ContainsKey("json")));
    }

    private static CliParseResult Files(List<string> args, CliKind kind, bool needsTarget, string? tool = null, string? recipe = null)
    {
        var (flags, free, error, options) = SplitWithOptions(args);
        if (error is not null) return Fail(error);
        if (free.Count == 0) return Fail("Give at least one file.");

        string? operation = tool;
        if (needsTarget)
        {
            if (!flags.TryGetValue("to", out var to) || string.IsNullOrWhiteSpace(to)) return Fail("convert needs --to <format>.");
            operation = Operation.Convert(ToolCatalog.Normalize(to));
        }
        if (flags.ContainsKey("json") && flags.ContainsKey("events")) return Fail("Use either --json or --events, not both.");
        return Ok(new CliCommand(kind, operation, free, options, flags.GetValueOrDefault("out"), Json: flags.ContainsKey("json"), Recipe: recipe, Events: flags.ContainsKey("events")));
    }

    private static (Dictionary<string, string> Flags, List<string> Free, string? Error) Split(List<string> args)
    {
        var (flags, free, error, _) = SplitWithOptions(args);
        return (flags, free, error);
    }

    private static (Dictionary<string, string> Flags, List<string> Free, string? Error, Dictionary<string, object?> Options) SplitWithOptions(List<string> args)
    {
        var flags = new Dictionary<string, string>();
        var free = new List<string>();
        var options = new Dictionary<string, object?>();

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { flags["json"] = "true"; continue; }
            if (a == "--events") { flags["events"] = "true"; continue; }
            if (a is "--to" or "--out" or "--category" or "--opt")
            {
                if (i + 1 >= args.Count) return (flags, free, $"{a} needs a value.", options);
                var value = args[++i];
                if (a == "--opt")
                {
                    var eq = value.IndexOf('=');
                    if (eq <= 0) return (flags, free, $"--opt expects key=value, got '{value}'.", options);
                    options[value[..eq]] = ParseValue(value[(eq + 1)..]);
                }
                else flags[a[2..]] = value;
                continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal)) return (flags, free, $"Unknown option '{a}'.", options);
            free.Add(a);
        }
        return (flags, free, null, options);
    }

    /// <summary>"80" becomes 80, "true" becomes true, anything else stays text.</summary>
    public static object ParseValue(string text)
    {
        if (bool.TryParse(text, out var b)) return b;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return text;
    }

    private static CliParseResult Ok(CliCommand c) => new(c, null);
    private static CliParseResult Fail(string message) => new(null, message);
}

/// <summary>Executes a parsed command and writes to the given streams. Returns the process exit code.</summary>
public sealed class CliRunner(
    ToolRegistry registry,
    ToolCatalog catalog,
    IEngineProvider engines,
    IReadOnlyList<Recipe> recipes,
    IFileDisposer disposer,
    TextWriter stdout,
    TextWriter stderr,
    string version,
    TimeSpan? watchPoll = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The CLI speaks English; notes that are resource keys are printed in their English wording.</summary>
    private static readonly Lazy<Localization.JsonLocalizer> English = new(() => Localization.JsonLocalizer.Load("en"));

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var parsed = CliParser.Parse(args);
        if (parsed.Command is null)
        {
            await stderr.WriteLineAsync(parsed.Error);
            return 2;
        }
        var c = parsed.Command;

        switch (c.Kind)
        {
            case CliKind.Help: await stdout.WriteLineAsync(CliParser.Usage); return 0;
            case CliKind.Version: await stdout.WriteLineAsync($"dropflow {version}"); return 0;
            case CliKind.List: return await ListAsync(c);
            case CliKind.Formats: return await FormatsAsync(c);
            case CliKind.Watch: return await WatchAsync(c, ct);
            case CliKind.Engines: return await EnginesAsync(c);
            case CliKind.RecipeList: return await RecipesAsync(c);
            default: return await ExecuteAsync(c, ct);
        }
    }

    private async Task<int> ListAsync(CliCommand c)
    {
        var rows = new List<object>();
        foreach (var cat in catalog.Categories.Where(x => c.Category is null || x.Id == c.Category))
        {
            foreach (var target in cat.Outputs)
            {
                var ext = cat.Inputs.FirstOrDefault(i => registry.Supports(Operation.Convert(target), i));
                if (ext is not null) rows.Add(new { kind = "convert", category = cat.Id, id = Operation.Convert(target), inputs = cat.Inputs.Where(i => registry.Supports(Operation.Convert(target), i)).ToArray() });
            }
            foreach (var tool in catalog.Tools.Where(t => t.AppliesTo(cat.Id)))
            {
                var inputs = cat.Inputs.Where(i => registry.Supports(tool.Id, i)).ToArray();
                if (inputs.Length > 0) rows.Add(new { kind = "tool", category = cat.Id, id = tool.Id, name = tool.En, multi = tool.Multi, inputs });
            }
        }
        foreach (var tool in catalog.Tools.Where(t => t.Cat == "general" && registry.ImplementedOperations.Contains(t.Id)))
            if (c.Category is null) rows.Add(new { kind = "tool", category = "general", id = tool.Id, name = tool.En, multi = tool.Multi, inputs = Array.Empty<string>() });

        if (c.Json) await stdout.WriteLineAsync(JsonSerializer.Serialize(rows, Json));
        else foreach (var r in rows) await stdout.WriteLineAsync(JsonSerializer.Serialize(r));
        return 0;
    }

    /// <summary>Every input extension the registry really reads, with the formats it converts to directly.</summary>
    private async Task<int> FormatsAsync(CliCommand c)
    {
        var inputs = catalog.Categories.SelectMany(x => x.Inputs).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal);
        var targets = catalog.Categories.SelectMany(x => x.Outputs).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var rows = new List<FormatRow>();
        foreach (var input in inputs)
        {
            var to = targets.Where(t => !t.Equals(input, StringComparison.OrdinalIgnoreCase) && registry.Supports(Operation.Convert(t), input)).ToArray();
            if (to.Length > 0) rows.Add(new FormatRow(input, to));
        }
        if (c.Json) await stdout.WriteLineAsync(JsonSerializer.Serialize(rows.Select(r => new { input = r.Input, targets = r.Targets }), Json));
        else foreach (var r in rows) await stdout.WriteLineAsync($"{r.Input,-8} -> {string.Join(", ", r.Targets)}");
        return 0;
    }

    private sealed record FormatRow(string Input, string[] Targets);

    /// <summary>
    /// Converts every file that finishes arriving in the folder (results in "&lt;folder&gt;\DropFlow" or --out) until Ctrl+C. Files DropFlow
    /// cannot convert are skipped with a line on stderr. Stopping is a normal end (exit 0).
    /// </summary>
    private async Task<int> WatchAsync(CliCommand c, CancellationToken ct)
    {
        var folder = Path.GetFullPath(c.Files![0]);
        if (!Directory.Exists(folder))
        {
            await stderr.WriteLineAsync($"Folder not found: {folder}");
            return 2;
        }
        var output = c.OutputDirectory is { } o ? Path.GetFullPath(o) : Path.Combine(folder, "DropFlow");
        Directory.CreateDirectory(output);
        var queue = new JobQueue(registry, catalog, Math.Max(1, Environment.ProcessorCount / 2));
        WatchService? watch = null;
        watch = new WatchService(async (_, files) =>
        {
            foreach (var file in files)
            {
                if (!registry.Supports(c.Operation!, ToolCatalog.ExtensionOf(file)))
                {
                    lock (stderr) stderr.WriteLine($"skipped (cannot {c.Operation}): {file}");
                    continue;
                }
                var result = await queue.Enqueue(new ConversionJob([file], c.Operation!, c.Options, output)).Completion.ConfigureAwait(false);
                watch!.MarkProduced(result.Outputs);
                if (result.Success) lock (stdout) foreach (var path in result.Outputs) stdout.WriteLine(path);
                else lock (stderr) stderr.WriteLine($"failed: {file}: {Localization.NoteText.Localize(result.Error, English.Value)}");
            }
        }, poll: watchPoll);
        using (watch)
        {
            watch.Apply([new WatchRule("cli", folder, [], null, OutputSubfolder: Path.GetRelativePath(folder, output) is var rel && !rel.StartsWith("..", StringComparison.Ordinal) ? rel : "")]);
            await stderr.WriteLineAsync($"watching {folder} → {c.Operation} (Ctrl+C to stop)");
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Ctrl+C: a normal end */ }
        }
        return 0;
    }

    private async Task<int> EnginesAsync(CliCommand c)
    {
        var rows = Enum.GetValues<EngineId>().Where(e => e != EngineId.None).Select(e => new { engine = e.ToString(), path = engines.Locate(e), installed = engines.Locate(e) is not null }).ToList();
        if (c.Json) await stdout.WriteLineAsync(JsonSerializer.Serialize(rows, Json));
        else foreach (var r in rows) await stdout.WriteLineAsync($"{r.engine,-12} {(r.installed ? r.path : "not installed")}");
        return 0;
    }

    private async Task<int> RecipesAsync(CliCommand c)
    {
        var rows = recipes.Select(r => new { name = r.Name, steps = r.Steps.Select(s => s.Operation).ToArray(), description = r.Description }).ToList();
        if (c.Json) await stdout.WriteLineAsync(JsonSerializer.Serialize(rows, Json));
        else foreach (var r in rows) await stdout.WriteLineAsync($"{r.name,-20} {string.Join(" → ", r.steps)}");
        return 0;
    }

    private async Task<int> ExecuteAsync(CliCommand c, CancellationToken ct)
    {
        var files = c.Files!.Select(Path.GetFullPath).ToList();
        var missing = files.Where(f => !File.Exists(f) && !Directory.Exists(f)).ToList();
        if (missing.Count > 0)
        {
            await stderr.WriteLineAsync($"File not found: {missing[0]}");
            return 2;
        }

        var queue = new JobQueue(registry, catalog, Math.Max(1, Environment.ProcessorCount / 2));
        ConversionResult result;
        try
        {
            if (c.Kind == CliKind.RecipeRun)
            {
                var recipe = recipes.FirstOrDefault(r => r.Name.Equals(c.Recipe, StringComparison.OrdinalIgnoreCase));
                if (recipe is null)
                {
                    await stderr.WriteLineAsync($"Unknown recipe '{c.Recipe}'. See 'dropflow recipe list'.");
                    return 2;
                }
                Event(c, new { @event = "start", recipe = recipe.Name, files });
                result = await new RecipeRunner(queue, disposer).RunAsync(recipe, files, c.OutputDirectory, ct);
            }
            else
            {
                var ext = ToolCatalog.ExtensionOf(files[0]);
                var converter = registry.Find(c.Operation!, ext);
                if (converter is null)
                {
                    await stderr.WriteLineAsync($"DropFlow cannot run '{c.Operation}' on .{ext} files. See 'dropflow formats' or 'dropflow list'.");
                    return 3;
                }
                var engine = converter.EngineFor(c.Operation!, c.Options ?? ConversionJob.NoOptions);
                if (engine != EngineId.None && engines.Locate(engine) is null)
                {
                    await stderr.WriteLineAsync($"{engine} is required but not installed. Install it from the DropFlow app (Settings > Engines) or with winget.");
                    return 1;
                }
                Event(c, new { @event = "start", operation = c.Operation, files });
                var batch = queue.Enqueue(new ConversionJob(files, c.Operation!, c.Options, c.OutputDirectory));
                var lastProgress = DateTime.MinValue;
                queue.Changed += b =>
                {
                    if (c.Events)
                    {
                        var now = DateTime.UtcNow;
                        if (now - lastProgress < TimeSpan.FromMilliseconds(250) && b.Progress < 1) return;
                        lastProgress = now;
                        Event(c, new { @event = "progress", value = Math.Round(b.Progress, 3) });
                    }
                    else if (!c.Json) stderr.Write($"\r{b.Progress * 100:0}%   ");
                };
                result = await batch.Completion.WaitAsync(ct);
                if (!c.Json && !c.Events) await stderr.WriteLineAsync();
            }
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("Cancelled.");
            return 1;
        }

        var note = Localization.NoteText.Localize(result.Note, English.Value);
        var error = Localization.NoteText.Localize(result.Error, English.Value);
        var exit = result.Success ? 0 : 1;
        if (c.Events)
        {
            foreach (var o in result.Outputs) Event(c, new { @event = "output", path = o });
            Event(c, new { @event = "summary", success = result.Success, outputs = result.Outputs, error, note, exitCode = exit });
            return exit;
        }
        if (c.Json) await stdout.WriteLineAsync(JsonSerializer.Serialize(new { success = result.Success, outputs = result.Outputs, error, note }, Json));
        else
        {
            foreach (var o in result.Outputs) await stdout.WriteLineAsync(o);
            if (note is not null) await stderr.WriteLineAsync(note);
            if (!result.Success) await stderr.WriteLineAsync($"Failed: {error}");
        }
        return exit;
    }

    private static readonly JsonSerializerOptions Line = new() { WriteIndented = false };

    /// <summary>One event line on stdout (only with --events). Lines are written whole, so a reader can parse each as it arrives.</summary>
    private void Event(CliCommand c, object payload)
    {
        if (!c.Events) return;
        lock (stdout) stdout.WriteLine(JsonSerializer.Serialize(payload, Line));
    }
}
