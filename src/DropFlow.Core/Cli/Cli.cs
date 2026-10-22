using System.Globalization;
using System.Text.Json;
using DropFlow.Core.Automation;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Cli;

public enum CliKind { Help, Version, List, Convert, Run, RecipeList, RecipeRun, Engines }

public sealed record CliCommand(
    CliKind Kind,
    string? Operation = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyDictionary<string, object?>? Options = null,
    string? OutputDirectory = null,
    string? Category = null,
    bool Json = false,
    string? Recipe = null);

public sealed record CliParseResult(CliCommand? Command, string? Error);

/// <summary>Parses <c>dropflow</c> command lines. Pure: no file system, no output.</summary>
public static class CliParser
{
    public const string Usage = """
        dropflow — convert and process files locally

        USAGE
          dropflow list [--category image|audio|video|pdf|document|slides|spreadsheet|epub|subtitle|archive] [--json]
          dropflow convert <files...> --to <format> [--opt key=value]... [--out <folder>] [--json]
          dropflow run <tool-id> <files...> [--opt key=value]... [--out <folder>] [--json]
          dropflow recipe list [--json]
          dropflow recipe run <name> <files...> [--out <folder>] [--json]
          dropflow engines [--json]
          dropflow doctor [--json]                                          (version, Windows, engines and their versions, encoders, models, WebView2)
          dropflow models list | download <id> [--yes] | delete <id>     (AI models; nothing downloads without --yes)
          dropflow version

        Results are written next to each source (or into --out). Originals are never changed.
        Exit codes: 0 success, 1 a job failed, 2 usage error.
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
        return Ok(new CliCommand(kind, operation, free, options, flags.GetValueOrDefault("out"), Json: flags.ContainsKey("json"), Recipe: recipe));
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
    string version)
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
                result = await new RecipeRunner(queue, disposer).RunAsync(recipe, files, c.OutputDirectory, ct);
            }
            else
            {
                var ext = ToolCatalog.ExtensionOf(files[0]);
                var converter = registry.Find(c.Operation!, ext);
                if (converter is null)
                {
                    await stderr.WriteLineAsync($"DropFlow cannot run '{c.Operation}' on .{ext} files. See 'dropflow list'.");
                    return 2;
                }
                var engine = converter.EngineFor(c.Operation!, c.Options ?? ConversionJob.NoOptions);
                if (engine != EngineId.None && engines.Locate(engine) is null)
                {
                    await stderr.WriteLineAsync($"{engine} is required but not installed. Install it from the DropFlow app (Settings > Engines) or with winget.");
                    return 1;
                }
                var batch = queue.Enqueue(new ConversionJob(files, c.Operation!, c.Options, c.OutputDirectory));
                queue.Changed += b => { if (!c.Json) stderr.Write($"\r{b.Progress * 100:0}%   "); };
                result = await batch.Completion.WaitAsync(ct);
                if (!c.Json) await stderr.WriteLineAsync();
            }
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("Cancelled.");
            return 1;
        }

        var note = Localization.NoteText.Localize(result.Note, English.Value);
        if (c.Json) await stdout.WriteLineAsync(JsonSerializer.Serialize(new { success = result.Success, outputs = result.Outputs, error = result.Error, note }, Json));
        else
        {
            foreach (var o in result.Outputs) await stdout.WriteLineAsync(o);
            if (note is not null) await stderr.WriteLineAsync(note);
            if (!result.Success) await stderr.WriteLineAsync($"Failed: {result.Error}");
        }
        return result.Success ? 0 : 1;
    }
}
