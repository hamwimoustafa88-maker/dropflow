using DropFlow.Core.Operations;

namespace DropFlow.App.Services;

/// <summary>
/// The "Open in DropFlow Encoder" wedge on the Tools wheel for video files. It runs nothing itself: the operation runner makes sure
/// FFmpeg is installed and then hands all the files to <paramref name="open"/>, which shows the Encoder window.
/// </summary>
internal sealed class EncoderTool(Func<IReadOnlyList<string>, Task> open) : IInteractiveTool
{
    public const string ToolId = "vid.encoder";

    // The video inputs of the catalog without gif (a gif is offered as an image).
    private static readonly HashSet<string> VideoExtensions = ["mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v"];

    public IReadOnlyCollection<string> Operations => [ToolId];

    public bool Supports(string operation, string inputExtension) => operation == ToolId && VideoExtensions.Contains(inputExtension);

    public EngineId EngineFor(string operation) => EngineId.Ffmpeg;

    public Task OpenAsync(string operation, IReadOnlyList<string> files) => open(files);

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        Task.FromResult(ConversionResult.Fail("The Encoder opens its own window and cannot run without it."));
}
