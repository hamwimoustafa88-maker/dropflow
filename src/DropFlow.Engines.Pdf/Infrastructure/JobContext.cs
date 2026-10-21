using DropFlow.Core.Operations;
using DropFlow.Core.Output;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>
/// Everything one running job needs: its inputs and options, progress, cancellation, and a safe way to claim output paths.
/// Output files are reserved with <see cref="FileMode.CreateNew"/> so two jobs can never write the same name, and anything the
/// job created is deleted again unless the job succeeded (partial outputs never survive a failure or cancellation).
/// </summary>
internal sealed class JobContext(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) : IDisposable
{
    private readonly List<string> _files = [];
    private readonly List<string> _folders = [];
    private readonly List<string> _temps = [];
    private bool _committed;

    public ConversionJob Job { get; } = job;
    public CancellationToken Ct { get; } = cancellationToken;
    public OutputPathResolver Paths { get; } = new();

    /// <summary>The first (or only) input file.</summary>
    public string Input => Job.Inputs[0];

    /// <summary>Everything reserved so far, in creation order (files and folders).</summary>
    public IReadOnlyList<string> Outputs => [.. _folders, .. _files];

    public void Report(double fraction, string? message = null) => progress.Report(new JobProgress(Math.Clamp(fraction, 0, 1), message));

    public ConversionResult Fail(string message) => ConversionResult.Fail(message);

    public ConversionResult Ok(params string[] outputs) => ConversionResult.Ok(outputs.Length > 0 ? outputs : Outputs);

    /// <summary>Success that carries a message the user should read (what was rasterized, what is not a real signature...).</summary>
    public ConversionResult OkWithNote(string note, params string[] outputs) =>
        ConversionResult.Ok(outputs.Length > 0 ? outputs : Outputs, default, note);

    /// <summary>Reserves "name (suffix).ext" beside the input (or in the output directory). The file exists (empty) on return.</summary>
    public string ReserveFile(string input, string extension, string suffix) =>
        Reserve(() => Paths.ForFile(input, extension, suffix, Job.OutputDirectory));

    /// <summary>
    /// Reserves several files that belong together ("a vs b (compare).pdf" and "a vs b (compare).txt"). They always share the same
    /// "(suffix N)" label: a name counts as taken when any of the files already exists. Never overwrites.
    /// </summary>
    public IReadOnlyList<string> ReserveSet(string nameSource, string suffix, params string[] extensions)
    {
        var resolver = new OutputPathResolver(p =>
            File.Exists(p) || Directory.Exists(p) || extensions.Any(e => File.Exists(Path.ChangeExtension(p, e))));
        while (true)
        {
            var paths = extensions.Select(e => resolver.ForFile(nameSource, e, suffix, Job.OutputDirectory)).ToArray();
            var created = new List<string>();
            var complete = true;
            foreach (var path in paths)
            {
                if (TryCreate(path)) created.Add(path);
                else { complete = false; break; }
            }

            if (complete) return paths;
            foreach (var path in created)
            {
                TryDelete(path);
                _files.Remove(path);
            }
        }
    }

    /// <summary>Reserves a file named after the first input of a multi-input job.</summary>
    public string ReserveGroupFile(string extension, string suffix) =>
        Reserve(() => Paths.ForGroup(Job.Inputs, extension, suffix, Job.OutputDirectory));

    /// <summary>Creates the folder "name (suffix)" for an operation that produces many files.</summary>
    public string ReserveFolder(string input, string suffix)
    {
        while (true)
        {
            var folder = Paths.ForFolder(input, suffix, Job.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            if (Directory.Exists(folder) || File.Exists(folder)) continue; // another job took this name since it was chosen
            Directory.CreateDirectory(folder);
            _folders.Add(folder);
            return folder;
        }
    }

    /// <summary>Reserves a file with an exact name inside a folder this job owns. Never overwrites.</summary>
    public string ReserveInFolder(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? fileName : $"{stem} ({n}){ext}";
            var path = Path.Combine(folder, name);
            if (TryCreate(path)) return path;
        }
    }

    /// <summary>A scratch folder that is deleted when the job ends (success or not).</summary>
    public string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "dropflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _temps.Add(path);
        return path;
    }

    /// <summary>A scratch file path (not created) inside a fresh temp folder; keeps the extension of <paramref name="name"/>.</summary>
    public string TempFile(string name) => Path.Combine(TempFolder(), name);

    /// <summary>Marks the outputs as final. Called by the converter wrapper when the job returned success.</summary>
    public void Commit() => _committed = true;

    public void Dispose()
    {
        if (!_committed)
        {
            foreach (var f in _files) TryDelete(f);
            foreach (var d in _folders.AsEnumerable().Reverse()) TryDeleteFolder(d);
        }
        foreach (var t in _temps) TryDeleteFolder(t);
    }

    private string Reserve(Func<string> pick)
    {
        while (true)
        {
            var path = pick();
            if (TryCreate(path)) return path;
        }
    }

    private bool TryCreate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            _files.Add(path);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false; // somebody else took this name; the caller picks the next one
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
