using System.Reflection;
using DropFlow.Core.Automation;
using DropFlow.Core.Catalog;
using DropFlow.Core.Cli;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Engines.Common;

namespace DropFlow.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow");
        var engines = new EngineLocator(dataDir);
        var registry = new ToolRegistry().Register(Modules.Create(engines));
        var recipes = BuiltInRecipes.Available(registry).Concat(new RecipeStore(Path.Combine(dataDir, "recipes.json")).Load()).ToList();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var runner = new CliRunner(registry, ToolCatalog.LoadEmbedded(), engines, recipes, new PermanentDelete(), Console.Out, Console.Error, version);
        return await runner.RunAsync(args, cts.Token);
    }
}

/// <summary>The CLI has no Recycle Bin dialog: intermediate files of a recipe are simply deleted.</summary>
internal sealed class PermanentDelete : IFileDisposer
{
    public void Dispose(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// Which converter modules the CLI loads: the app's list minus what needs Windows UI or a model download (HTML → PDF, speech to text,
/// OCR, background removal), which are app-only for now.
/// </summary>
internal static class Modules
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) =>
    [
        .. DropFlow.Engines.Images.ImagesModule.Create(engines),
        .. DropFlow.Engines.Media.MediaModule.Create(engines),
        .. DropFlow.Engines.Pdf.PdfModule.Create(engines),
        .. DropFlow.Engines.Archive.ArchiveModule.Create(engines),
    ];
}
