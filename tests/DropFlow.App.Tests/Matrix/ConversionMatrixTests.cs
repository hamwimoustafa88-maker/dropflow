// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using DropFlow.AI.Models;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Wheel;
using DropFlow.Engines.Common;
using DropFlow.App.Services;

namespace DropFlow.App.Tests.Matrix;

/// <summary>Runs only with <c>DROPFLOW_MATRIX=1</c>: it takes 20-60 minutes and needs the real helper programs and models.</summary>
public sealed class MatrixFactAttribute : FactAttribute
{
    public MatrixFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DROPFLOW_MATRIX") != "1")
            Skip = "Conversion matrix: set DROPFLOW_MATRIX=1 (optional DROPFLOW_MATRIX_DIR, DROPFLOW_MATRIX_ONLY=<sample or operation substring>).";
    }
}

/// <summary>
/// The whole product on real files: every item the wheel offers for every sample (single files, then pairs for the multi-file tools),
/// run through the real registry with default options, every output re-opened and checked. Writes <c>report.md</c> and
/// <c>report.json</c> into the matrix folder and fails when any cell failed.
/// </summary>
public sealed class ConversionMatrixTests
{
    private sealed record Cell(string Sample, string Operation, string Status, string Detail, double Seconds, IReadOnlyList<string> Outputs, string Options = "");

    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(6);

    [MatrixFact]
    public async Task Every_wheel_item_works_on_every_sample()
    {
        var root = Environment.GetEnvironmentVariable("DROPFLOW_MATRIX_DIR") ?? Path.Combine(Path.GetTempPath(), "dropflow-matrix");
        var only = Environment.GetEnvironmentVariable("DROPFLOW_MATRIX_ONLY");
        var corpusDir = Path.Combine(root, "corpus");
        var outRoot = Path.Combine(root, "out");
        if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);
        Directory.CreateDirectory(outRoot);

        var dataDir = DropFlow.Core.App.DataFolder.Default;
        var shipped = Environment.GetEnvironmentVariable("DROPFLOW_MATRIX_ENGINES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow", "current", "engines");
        var engines = new EngineLocator(dataDir, bundledDirectory: Directory.Exists(shipped) ? shipped : null);

        var corpus = new MatrixCorpus(engines, corpusDir);
        corpus.Build();

        using var ui = new UiThread();
        using var models = new ModelManager(Path.Combine(dataDir, "models"));
        var registry = new ToolRegistry().Register(Modules.Create(engines, ui.Dispatcher, Path.Combine(root, "webview"), _ => Task.CompletedTask, models));
        var catalog = ToolCatalog.LoadEmbedded();
        var wheel = new WheelBuilder(catalog, registry, engines);
        var checker = new OutputChecker(engines);

        // Work list: every Convert/Tools item for each single sample, then the multi-file tools on pairs of one category.
        var work = new List<(MatrixSample[] Files, Wedge Item)>();
        foreach (var sample in corpus.Samples)
        {
            var set = wheel.Build([sample.Path], "en", []);
            foreach (var item in set.Convert.Concat(set.Tools).Where(w => !w.IsAdd)) work.Add(([sample], item));
        }
        foreach (var group in corpus.Samples.GroupBy(s => catalog.CategoryForExtension(ToolCatalog.ExtensionOf(s.Path))).Where(g => g.Key is not null))
        {
            var pairs = group.GroupBy(s => ToolCatalog.ExtensionOf(s.Path)).Where(g => g.Count() >= 2).Select(g => g.Take(2).ToArray())
                .Append(group.Take(2).ToArray()).Where(p => p.Length == 2).DistinctBy(p => p[0].Path + "|" + p[1].Path);
            foreach (var pair in pairs)
            {
                var single = wheel.Build([pair[0].Path], "en", []).Tools.Select(w => w.Id).ToHashSet();
                foreach (var item in wheel.Build(pair.Select(s => s.Path).ToList(), "en", []).Tools.Where(w => !w.IsAdd && (w.Multi || !single.Contains(w.Id))))
                    work.Add((pair, item));
            }
        }
        if (!string.IsNullOrEmpty(only))
            work = work.Where(w => w.Files.Any(f => f.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) || w.Item.Operation.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

        // A crash of the whole process (native code) leaves no report: this log says which cells were running and what was thrown.
        var logPath = Path.Combine(root, "matrix.log");
        var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var logGate = new object();
        void Log(string line) { lock (logGate) log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}"); }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"UNHANDLED {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log($"UNOBSERVED {e.Exception}");

        var cells = new ConcurrentBag<Cell>();
        using var gate = new SemaphoreSlim(3);
        var index = 0;
        await Task.WhenAll(work.Select(async w =>
        {
            await gate.WaitAsync();
            try
            {
                var n = Interlocked.Increment(ref index);
                var dir = Path.Combine(outRoot, $"{n:0000}-{Safe(string.Join("+", w.Files.Select(f => f.Name)))}-{Safe(w.Item.Operation)}");
                var label = $"{n:0000} {string.Join("+", w.Files.Select(f => f.Name))} {w.Item.Operation}";
                Log($"start {label} (memory {Environment.WorkingSet / 1048576} MB)");
                var cell = await RunCell(registry, checker, w.Files, w.Item, dir);
                Log($"end   {label} {cell.Status} {cell.Seconds:0.0}s");
                cells.Add(cell);
            }
            finally { gate.Release(); }
        }));

        log.Dispose();
        var ordered = cells.OrderBy(c => c.Sample, StringComparer.Ordinal).ThenBy(c => c.Operation, StringComparer.Ordinal).ToList();
        WriteReport(root, corpus, ordered);

        var bad = ordered.Where(c => c.Status is "FAIL" or "THREW" or "TIMEOUT" or "BAD-OUTPUT").ToList();
        Assert.True(bad.Count == 0, $"{bad.Count} of {ordered.Count} cells failed; see {Path.Combine(root, "report.md")}\n" +
            string.Join("\n", bad.Take(60).Select(c => $"{c.Status} {c.Sample} :: {c.Operation} :: {c.Detail}")));
    }

    private static async Task<Cell> RunCell(ToolRegistry registry, OutputChecker checker, MatrixSample[] files, Wedge item, string outDir)
    {
        var sampleName = string.Join(" + ", files.Select(f => f.Name));
        var op = item.Operation;
        var converter = registry.Find(op, ToolCatalog.ExtensionOf(files[0].Path));
        if (converter is null) return new Cell(sampleName, op, "FAIL", "wheel offered it but no converter found", 0, []);
        if (converter is IInteractiveTool) return new Cell(sampleName, op, "WINDOW", "opens its own window (not run headless)", 0, []);

        var options = new Dictionary<string, object?>();
        // As the options dialog sends them: defaults as they are, a required option without one gets a plausible value, and an
        // optional one without a default is left out (it means "not set", e.g. the free x/y of a signature).
        foreach (var option in converter.OptionsFor(op))
        {
            if (option.Default is not null) options[option.Key] = option.Default;
            else if (option.Required || option.Kind == ToolOptionKind.Password) options[option.Key] = FillRequired(op, option, files);
        }
        // What the visual editors would send: without these the editor-driven tools only say "nothing to do".
        if (EditorValues.TryGetValue(op, out var editor))
            foreach (var (k, v) in editor)
                options[k] = v is string text ? text.Replace("{dir}", Path.GetDirectoryName(files[0].Path) + Path.DirectorySeparatorChar) : v;
        foreach (var f in files)
            if (f.Options is not null) foreach (var (k, v) in f.Options) options.TryAdd(k, v);
        if (files[0].Options?.TryGetValue("password", out var pw) == true) options["password"] = pw;

        var used = string.Join(", ", options.Select(kv => $"{kv.Key}={Convert.ToString(kv.Value, CultureInfo.InvariantCulture)}"));
        var cell = await RunWithOptions();
        return cell with { Options = used };

        async Task<Cell> RunWithOptions()
        {
        Directory.CreateDirectory(outDir);
        var before = files.ToDictionary(f => f.Path, f => Hash(f.Path));
        var watch = Stopwatch.StartNew();
        ConversionResult result;
        using var cts = new CancellationTokenSource(JobTimeout);
        try
        {
            result = await Task.Run(() => converter.RunAsync(new ConversionJob(files.Select(f => f.Path).ToList(), op, options, outDir), new Progress<JobProgress>(), cts.Token));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new Cell(sampleName, op, "TIMEOUT", $"no result after {JobTimeout.TotalMinutes} min", watch.Elapsed.TotalSeconds, []);
        }
        catch (Exception e)
        {
            return new Cell(sampleName, op, "THREW", $"{e.GetType().Name}: {e.Message}", watch.Elapsed.TotalSeconds, []);
        }
        var seconds = watch.Elapsed.TotalSeconds;

        foreach (var f in files)
            if (Hash(f.Path) != before[f.Path]) return new Cell(sampleName, op, "FAIL", "THE ORIGINAL WAS CHANGED", seconds, result.Outputs);

        var protectedInput = files.Any(f => f.Options?.ContainsKey("password") == true);
        if (!result.Success)
        {
            var status = Expected(op, files, result.Error) ? "REFUSED" : NeedsInput(result.Error) ? "NEEDS-INPUT" : "FAIL";
            return new Cell(sampleName, op, status, result.Error ?? "(no message)", seconds, []);
        }
        if (result.Outputs.Count == 0)
            return new Cell(sampleName, op, result.Note is null ? "FAIL" : "NOTE", result.Note ?? "success but no output and no note", seconds, []);

        var problems = new List<string>();
        var warnings = new List<string>();
        var markers = files.SelectMany(f => f.Markers).Distinct().ToList();
        // PDF to Excel keeps the tables only (by design): check the table's words, which every Arabic sample PDF has.
        if (op == "convert:xlsx" && files[0].Name == "arabic-edge.pdf") markers = ["قلم", "دفتر", "الصنف"];
        var password = (options.TryGetValue("userPassword", out var up) ? up as string : null) ?? (options.TryGetValue("password", out var pv) ? pv as string : null);
        var scanned = files.Any(f => f.Note.Contains("no text layer"));
        var all = result.Outputs.SelectMany(o => Directory.Exists(o) ? Directory.EnumerateFiles(o, "*", SearchOption.AllDirectories) : [o]).ToList();
        if (all.Count == 0) problems.Add("outputs are empty folders");
        // Text must survive somewhere in the result (a workbook becomes one CSV per sheet: the words are in one of them).
        var textMisses = new List<(string File, CheckResult Check)>();
        foreach (var path in all)
        {
            var check = checker.Check(path, op, markers, password);
            if (check.TextMiss) { textMisses.Add((path, check)); continue; }
            if (check.Error is not null) problems.Add($"{Path.GetFileName(path)}: {check.Error}");
            if (check.Warning is not null) warnings.Add($"{Path.GetFileName(path)}: {check.Warning}");
        }
        var textOutputs = markers.Count == 0 ? 0 : all.Count(p => checker.ChecksText(p, op));
        if (textMisses.Count > 0 && textMisses.Count == textOutputs)
        {
            var (file, first) = textMisses[0];
            var message = $"{Path.GetFileName(file)}: {first.Error ?? first.Warning}";
            if (first.Error is not null && !scanned) problems.Add(message); else warnings.Add(message);
        }
        if (Operation.IsConvert(op))
        {
            var target = Operation.TargetOf(op) == "pdfa" ? "pdf" : Operation.TargetOf(op);
            foreach (var output in result.Outputs.Where(File.Exists))
                if (!string.Equals(Path.GetExtension(output).TrimStart('.'), target, StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(output).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{Path.GetFileName(output)}: extension is not .{target}");
        }

        var detail = string.Join("; ", problems.Concat(warnings.Select(w => "warn: " + w)).Prepend(result.Note is null ? null : "note: " + result.Note).Where(s => s is not null));
        var cellStatus = problems.Count > 0 ? "BAD-OUTPUT" : warnings.Count > 0 ? "WARN" : "PASS";
        _ = protectedInput;
        return new Cell(sampleName, op, cellStatus, detail, seconds, result.Outputs.Select(o => Path.GetRelativePath(Path.GetDirectoryName(outDir)!, o)).ToList());
        }
    }

    /// <summary>The tool asked for something only the user can give (a name, times, a page list): the options dialog collects it.</summary>
    private static bool NeedsInput(string? error) => error is not null &&
        (error.StartsWith("Enter ", StringComparison.Ordinal) || error.StartsWith("Set a start", StringComparison.Ordinal) ||
         error.StartsWith("No subtitle file was chosen", StringComparison.Ordinal) || error.StartsWith("Nothing to change", StringComparison.Ordinal));

    private static readonly Dictionary<string, Dictionary<string, object?>> EditorValues = new()
    {
        ["img.annotate"] = new() { ["items"] = """[{"type":"rect","x":20,"y":20,"w":100,"h":80,"color":"#ff0000"},{"type":"arrow","x":10,"y":10,"x2":200,"y2":150,"color":"#00ff00"},{"type":"number","x":60,"y":60,"n":1}]""" },
        ["img.redact"] = new() { ["regions"] = "10,10,60,40" },
        ["img.metadata"] = new() { ["artist"] = "Matrix Tester", ["description"] = "مرحبا matrix" },
        ["img.edit"] = new() { ["filter"] = "grayscale" },
        ["pdf.redact"] = new() { ["terms"] = "4821" },
        ["pdf.sign"] = new() { ["text"] = "Matrix Tester" },
        ["pdf.organize"] = new() { ["pages"] = "1" },
        ["pdf.crop"] = new() { ["top"] = 36d, ["bottom"] = 36d },
        ["vid.metadata"] = new() { ["title"] = "مرحبا matrix", ["artist"] = "Matrix Tester" },
        ["aud.metadata"] = new() { ["title"] = "مرحبا matrix", ["artist"] = "Matrix Tester" },
        ["vid.redact"] = new() { ["x"] = 20d, ["y"] = 20d, ["w"] = 120d, ["h"] = 80d },
        ["vid.split"] = new() { ["points"] = "0:02" },
        ["aud.bleep"] = new() { ["ranges"] = "1.0-1.6" },
        ["aud.trim"] = new() { ["start"] = "0.5", ["end"] = "3" },
        ["vid.burn-subs"] = new() { ["subtitle"] = "{dir}arabic.srt" },
    };

    /// <summary>A refusal that is the correct answer (a locked PDF without the password, a picture without text...).</summary>
    private static bool Expected(string op, MatrixSample[] files, string? error) =>
        error is not null && (
            (files.Any(f => f.Options?.ContainsKey("password") == true) && op != "pdf.unlock" && error.Contains("password", StringComparison.OrdinalIgnoreCase)) ||
            (files.Any(f => f.Name.Contains("no-audio")) && error.Contains("audio", StringComparison.OrdinalIgnoreCase)) ||
            (op == "img.qr" && error.StartsWith("No QR code", StringComparison.Ordinal)) ||
            // Correct answers for a sample that has nothing to work on: no text in a photo, no speech in a tone, a scan without text.
            error.StartsWith("No text was found", StringComparison.Ordinal) || error.StartsWith("No speech was detected", StringComparison.Ordinal) ||
            error.StartsWith("Nothing to redact was detected", StringComparison.Ordinal) || error.Contains("already mono", StringComparison.Ordinal) ||
            (files.Any(f => f.Note.Contains("no text layer")) && (error.Contains("no text layer", StringComparison.Ordinal) || error.Contains("no searchable text", StringComparison.Ordinal))));

    private static object? FillRequired(string op, ToolOption option, MatrixSample[] files) => option.Kind switch
    {
        ToolOptionKind.Password => MatrixCorpus.Password,
        ToolOptionKind.Bool => false,
        ToolOptionKind.Number => option.Min ?? 1,
        ToolOptionKind.Choice => option.Choices?.FirstOrDefault(),
        ToolOptionKind.Range => "1",
        ToolOptionKind.Folder => null,
        _ => option.Key.ToLowerInvariant() switch
        {
            "text" or "watermark" or "caption" or "content" => "DropFlow matrix",
            "terms" or "words" => "4821",
            "pattern" or "template" => "{name}-{n}",
            "url" => "https://dropflow.scouthub.dev",
            "name" or "author" or "signer" => "Matrix Tester",
            "title" => "Matrix",
            _ => "DropFlow",
        },
    };

    private static void WriteReport(string root, MatrixCorpus corpus, List<Cell> cells)
    {
        var md = new StringBuilder();
        md.AppendLine($"# DropFlow conversion matrix — {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        foreach (var group in cells.GroupBy(c => c.Status).OrderBy(g => g.Key)) md.AppendLine($"- **{group.Key}**: {group.Count()}");
        md.AppendLine($"- cells: {cells.Count}, samples: {corpus.Samples.Count}");
        md.AppendLine();
        if (corpus.Problems.Count > 0)
        {
            md.AppendLine("## Corpus problems");
            foreach (var p in corpus.Problems) md.AppendLine($"- {p}");
            md.AppendLine();
        }
        md.AppendLine("## Samples");
        md.AppendLine("| File | What |\n|---|---|");
        foreach (var s in corpus.Samples) md.AppendLine($"| {s.Name} | {s.Note} |");
        md.AppendLine();
        md.AppendLine("## Not passing");
        md.AppendLine("| Status | Sample | Operation | Detail | s |\n|---|---|---|---|---|");
        foreach (var c in cells.Where(c => c.Status != "PASS")) md.AppendLine($"| {c.Status} | {c.Sample} | `{c.Operation}` | {Esc(c.Detail)} | {c.Seconds:0.0} |");
        md.AppendLine();
        md.AppendLine("## All cells");
        md.AppendLine("| Sample | Operation | Status | s | Outputs |\n|---|---|---|---|---|");
        foreach (var c in cells) md.AppendLine($"| {c.Sample} | `{c.Operation}` | {c.Status} | {c.Seconds:0.0} | {Esc(string.Join(", ", c.Outputs))} |");
        File.WriteAllText(Path.Combine(root, "report.md"), md.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(cells, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Safe(string s)
    {
        var name = new string(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ':' ? '_' : ch).ToArray());
        return name.Length > 60 ? name[..60] : name;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        public Dispatcher Dispatcher { get; }

        public UiThread()
        {
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
        }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
