using System.IO;
using System.Windows.Threading;
using DropFlow.AI.Models;
using DropFlow.App.Services;
using DropFlow.Core.Catalog;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Overlay;
using DropFlow.TestSupport;

namespace DropFlow.App.Tests;

/// <summary>
/// Cross-module checks over the real converter list the app loads: what the engine projects implement must exist in the catalog,
/// and every option the generated form can show needs a label in both languages.
/// </summary>
public sealed class RegistryCoverageTests : IDisposable
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();
    private readonly TempDir _dir = new();
    private readonly ModelManager _models;

    public RegistryCoverageTests() => _models = new ModelManager(_dir.Combine("models"));

    public void Dispose()
    {
        _models.Dispose();
        _dir.Dispose();
    }

    private ToolRegistry Registry() =>
        new ToolRegistry().Register(Modules.Create(TestSupport.Engines.Locator, Dispatcher.CurrentDispatcher, _dir.Combine("web"), _ => Task.CompletedTask, _models));

    [Fact]
    public void Every_implemented_operation_exists_in_the_catalog()
    {
        var problems = new List<string>();
        foreach (var converter in Registry().Converters)
        {
            foreach (var op in converter.Operations)
            {
                if (Operation.IsConvert(op))
                {
                    var target = Operation.TargetOf(op);
                    if (!Catalog.Categories.Any(c => c.Outputs.Contains(target))) problems.Add($"{converter.GetType().Name}: {op} is not an output of any category");
                }
                else if (Catalog.Tool(op) is null)
                {
                    problems.Add($"{converter.GetType().Name}: tool {op} is not in the catalog");
                }
            }
        }
        AssertNone(problems);
    }

    /// <summary>
    /// A ratchet: the catalog items that have no converter yet are listed here, so a gap can only appear or disappear on purpose
    /// (and docs/status.md has to be updated with it). Conversions are checked after the category priority is applied (a gif is an image).
    /// </summary>
    [Fact]
    public void The_known_gaps_in_the_catalog_are_exactly_these()
    {
        var registry = Registry();
        var windowsTools = Catalog.Tools.Where(t => t.Platforms.Contains("windows")).ToList();
        var missingTools = windowsTools.Where(t => !registry.ImplementedOperations.Contains(t.Id)).Select(t => t.Id).OrderBy(x => x).ToList();

        var missingConversions = new List<string>();
        foreach (var category in Catalog.Categories)
            foreach (var input in category.Inputs.Where(i => Catalog.CategoryForExtension(i) == category.Id))
                foreach (var output in category.Outputs.Where(o => o != input))
                    if (!registry.Supports(Operation.Convert(output), input)) missingConversions.Add($"{input}->{output}");

        // No gaps are left: pdf -> pdfa is built in-process (PDF/A-2b, validated with veraPDF), heic -> pdf is tested with a real HEIC file.
        Assert.Empty(missingTools);
        Assert.Empty(missingConversions);
    }

    [Fact]
    public void No_operation_and_file_type_is_claimed_by_two_converters()
    {
        var converters = Registry().Converters;
        var extensions = Catalog.Categories.SelectMany(c => c.Inputs).Concat(["htm", "xhtml", "tgz", ""]).Distinct().ToList();
        var problems = new List<string>();

        foreach (var op in converters.SelectMany(c => c.Operations).Distinct())
            foreach (var ext in extensions)
            {
                var owners = converters.Where(c => c.Supports(op, ext)).ToList();
                if (owners.Count < 2) continue;

                problems.Add($"{op} on .{ext}: {string.Join(" and ", owners.Select(o => o.GetType().Name))}");
            }

        AssertNone(problems);
    }

    /// <summary>
    /// The promise "nothing to download" for everyday documents: HTML, text, Markdown, Word, spreadsheets, CSV/JSON and EPUB become
    /// PDF, PNG and JPG with no helper program (the owner does not want install prompts for these).
    /// </summary>
    [Fact]
    public void Everyday_documents_convert_to_pdf_and_pictures_without_any_helper_program()
    {
        var registry = Registry();
        var problems = new List<string>();
        foreach (var input in new[] { "html", "txt", "md", "docx", "csv", "tsv", "xlsx", "json", "epub" })
            foreach (var target in new[] { "pdf", "png", "jpg" })
            {
                var converter = registry.Find($"convert:{target}", input);
                if (converter is null) problems.Add($"{input} -> {target}: no converter");
                else if (converter.EngineFor($"convert:{target}") != EngineId.None) problems.Add($"{input} -> {target}: needs {converter.EngineFor($"convert:{target}")}");
            }
        AssertNone(problems);
    }

    [Fact]
    public void Every_option_has_a_label_in_both_languages()
    {
        var problems = new List<string>();
        foreach (var lang in new[] { "en", "ar" })
        {
            var loc = JsonLocalizer.Load(lang);
            foreach (var converter in Registry().Converters)
                foreach (var op in converter.Operations)
                    foreach (var option in converter.OptionsFor(op))
                        if (!OptionForm.LabelKeys(op, option).Any(k => loc[k] != k))
                            problems.Add($"{lang}: opt.{op}.{option.Key}");
        }
        AssertNone(problems);
    }

    private static void AssertNone(IEnumerable<string> problems)
    {
        var list = problems.Distinct().OrderBy(x => x).ToList();
        Assert.True(list.Count == 0, $"{list.Count} problem(s):\n" + string.Join("\n", list));
    }

    [Fact]
    public void Every_choice_that_is_a_plain_word_is_translated_in_both_languages()
    {
        // Technical values (h264, mp3, 1080 ...) may stay as they are; real words must not leak through in English.
        var problems = new List<string>();
        foreach (var lang in new[] { "en", "ar" })
        {
            var loc = JsonLocalizer.Load(lang);
            foreach (var converter in Registry().Converters)
                foreach (var op in converter.Operations)
                    foreach (var option in converter.OptionsFor(op).Where(o => o.Kind == ToolOptionKind.Choice && o.Choices is not null))
                        foreach (var choice in option.Choices!)
                        {
                            var keys = new[] { $"opt.{op}.{option.Key}.{choice}", $"opt.{option.Key}.{choice}" };
                            if (keys.Any(k => loc[k] != k)) continue;
                            if (IsTechnical(choice)) continue;
                            problems.Add($"{lang}: opt.{op}.{option.Key}.{choice}");
                        }
        }
        AssertNone(problems);
    }

    /// <summary>Codec names, formats, sizes, language codes: anything with a digit, or at most three letters, or a known format word.</summary>
    private static bool IsTechnical(string choice) =>
        choice.Any(char.IsDigit) || choice.Length <= 3 || choice.Contains('+') || choice.Contains('-')
        || choice is "h264" or "h265" or "flac" or "opus" or "vorbis" or "tesseract" or "windows" or "sha256" or "sha512" or "all";
}
