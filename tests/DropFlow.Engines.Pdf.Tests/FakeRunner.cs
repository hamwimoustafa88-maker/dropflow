using DropFlow.Engines.Common;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// Stands in for an external engine: records every call and answers with a scripted result. qpdf is not installed on the dev machine,
/// so this is what lets the orchestration (temp files, exit codes, "not smaller" handling, cancellation) be tested for real.
/// </summary>
internal sealed class FakeRunner : IEngineRunner
{
    public delegate ProcessResult Behavior(string executable, IReadOnlyList<string> arguments);

    private readonly Behavior _behavior;

    public FakeRunner(Behavior behavior) => _behavior = behavior;

    public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, ProcessOptions? options, CancellationToken cancellationToken)
    {
        Calls.Add((executable, arguments.ToList()));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new ProcessResult(-1, "", "", TimeSpan.Zero, false, true));
        return Task.FromResult(_behavior(executable, arguments));
    }

    public static ProcessResult Result(int exitCode = 0, string stdout = "", string stderr = "") =>
        new(exitCode, stdout, stderr, TimeSpan.FromMilliseconds(5), false, false);

    /// <summary>qpdf and LibreOffice style: the last argument is the output; copy the first existing input file there.</summary>
    public static Behavior CopyInputToLastArgument(int exitCode = 0, string stderr = "") => (_, args) =>
    {
        var output = args[^1];
        var input = args.Take(args.Count - 1).Last(a => File.Exists(a));
        File.Copy(input, output, overwrite: true);
        return Result(exitCode, stderr: stderr);
    };

    /// <summary>Writes <paramref name="size"/> bytes of a valid-looking PDF to the last argument.</summary>
    public static Behavior WriteFileOfSize(long size) => (_, args) =>
    {
        var bytes = new byte[size];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        File.WriteAllBytes(args[^1], bytes);
        return Result();
    };

    public static Behavior Fail(int exitCode, string stderr) => (_, _) => Result(exitCode, stderr: stderr);

    public static IReadOnlyList<DropFlow.Core.Operations.IConverter> Module(FakeRunner runner, params DropFlow.Core.Operations.EngineId[] installed) =>
        PdfModule.Create(new FakeEngines(installed), runner);
}
