using DropFlow.Engines.Common;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// A stand-in for soffice.exe: it reads the real command line the module built, writes a plausible output file where
/// <c>--outdir</c> says, and snapshots the input folder so tests can check what LibreOffice would have been given.
/// </summary>
internal sealed class FakeSoffice
{
    private readonly Func<string, byte[]> _output;
    private readonly int _exitCode;
    private readonly bool _writeOutput;

    public FakeSoffice(Func<string, byte[]>? output = null, int exitCode = 0, bool writeOutput = true)
    {
        _output = output ?? DefaultOutput;
        _exitCode = exitCode;
        _writeOutput = writeOutput;
        Runner = new FakeRunner(Run);
    }

    public FakeRunner Runner { get; }

    /// <summary>Input folder contents at the moment LibreOffice "ran": relative path to bytes.</summary>
    public List<Dictionary<string, byte[]>> InputSnapshots { get; } = [];

    public List<string> ProfileDirectories { get; } = [];

    public string ConvertTo(int call) => Runner.Calls[call].Arguments[Runner.Calls[call].Arguments.ToList().IndexOf("--convert-to") + 1];

    public string InputFile(int call) => Runner.Calls[call].Arguments[^1];

    private ProcessResult Run(string executable, IReadOnlyList<string> args)
    {
        var list = args.ToList();
        var outDir = list[list.IndexOf("--outdir") + 1];
        var input = list[^1];
        var ext = list[list.IndexOf("--convert-to") + 1].Split(':')[0];

        var inputDir = Path.GetDirectoryName(input)!;
        InputSnapshots.Add(Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(inputDir, f).Replace('\\', '/'), File.ReadAllBytes));
        ProfileDirectories.Add(list.Single(a => a.StartsWith("-env:UserInstallation=", StringComparison.Ordinal)));

        if (_writeOutput)
            File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "." + ext), _output(ext));
        return FakeRunner.Result(_exitCode, stderr: _exitCode == 0 ? "" : "Error: source file could not be loaded");
    }

    public static byte[] DefaultOutput(string ext)
    {
        using var scratch = new TempDir();
        switch (ext)
        {
            case "pdf":
                return File.ReadAllBytes(Samples.Pdf(scratch.Combine("o.pdf"), 1, _ => "From LibreOffice"));
            case "docx":
                return File.ReadAllBytes(DocxFixture.Write(scratch.Combine("o.docx"), DocxFixture.P("Converted heading", "Heading1") + DocxFixture.P("Some body text.")));
            case "xlsx":
                return File.ReadAllBytes(XlsxFixture.Write(scratch.Combine("o.xlsx"), ("Sheet1", [["a", "b"], [1L, "x"]])));
            default:
                return System.Text.Encoding.UTF8.GetBytes($"fake {ext} output");
        }
    }

    public static byte[] ThreePagePdf(string ext)
    {
        using var scratch = new TempDir();
        return ext == "pdf" ? File.ReadAllBytes(Samples.Pdf(scratch.Combine("o.pdf"), 3, n => $"Slide {n}")) : DefaultOutput(ext);
    }

    public static byte[] TwoSheetWorkbook(string ext)
    {
        using var scratch = new TempDir();
        return ext == "xlsx"
            ? File.ReadAllBytes(XlsxFixture.Write(scratch.Combine("o.xlsx"), ("First", [["h1"], ["v1"]]), ("Second", [["h2"], ["v2"]])))
            : DefaultOutput(ext);
    }
}
