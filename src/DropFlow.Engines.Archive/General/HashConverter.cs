using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;

namespace DropFlow.Engines.Archive.General;

/// <summary>The checksum algorithms <c>gen.hash</c> offers, in the order they are written.</summary>
public static class HashAlgorithms
{
    public sealed record Algorithm(string Key, string Label, HashAlgorithmName Name);

    public static readonly IReadOnlyList<Algorithm> All =
    [
        new("md5", "MD5", HashAlgorithmName.MD5),
        new("sha1", "SHA-1", HashAlgorithmName.SHA1),
        new("sha256", "SHA-256", HashAlgorithmName.SHA256),
        new("sha512", "SHA-512", HashAlgorithmName.SHA512),
    ];

    /// <summary>
    /// Parses "sha256", "md5,sha256", "SHA-1 SHA-512" or "all". Returns algorithms in canonical order without duplicates.
    /// Throws <see cref="ArgumentException"/> for an unknown name.
    /// </summary>
    public static IReadOnlyList<Algorithm> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [All[2]];

        var wanted = new HashSet<string>();
        foreach (var raw in text.Split([',', ';', ' ', '+', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = raw.Replace("-", "").Replace("_", "").ToLowerInvariant();
            if (key == "all") return All;
            if (key == "sha2") key = "sha256";
            if (All.All(a => a.Key != key)) throw new ArgumentException($"Unknown hash algorithm '{raw}'. Use md5, sha1, sha256, sha512 or all.");
            wanted.Add(key);
        }

        return wanted.Count == 0 ? [All[2]] : All.Where(a => wanted.Contains(a.Key)).ToList();
    }
}

/// <summary>
/// <c>gen.hash</c>: writes <c>name (hash).txt</c> beside each input in <c>sha256sum</c> format (<c>hash  filename</c>, two
/// spaces, LF line ends), so it can be checked later with <c>sha256sum -c</c>. With several algorithms the file has one block
/// per algorithm introduced by a <c># SHA-256</c> comment line. Option <c>algorithms</c>: md5, sha1, sha256 (default), sha512,
/// a comma-separated list, or all.
/// </summary>
public sealed class HashConverter : IConverter
{
    public const string OperationId = "gen.hash";

    private static readonly ToolOption AlgorithmsOption =
        new("algorithms", ToolOptionKind.Choice, "sha256", ["sha256", "md5", "sha1", "sha512", "all"]);

    public IReadOnlyCollection<string> Operations { get; } = [OperationId];

    public bool Supports(string operation, string inputExtension) => operation == OperationId;

    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) => [AlgorithmsOption];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = job.Inputs[0];
        if (Directory.Exists(input)) return ConversionResult.Fail("Folders cannot be hashed. Select files.");
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

        IReadOnlyList<HashAlgorithms.Algorithm> algorithms;
        try { algorithms = HashAlgorithms.Parse(job.GetString("algorithms")); }
        catch (ArgumentException ex) { return ConversionResult.Fail(ex.Message); }

        var sw = Stopwatch.StartNew();
        string output = "";
        var created = false;
        try
        {
            var hashes = await ComputeAsync(input, algorithms, progress, cancellationToken).ConfigureAwait(false);
            var text = Format(Path.GetFileName(input), algorithms, hashes);

            var resolver = new OutputPathResolver();
            for (var attempt = 0; ; attempt++)
            {
                output = resolver.ForFile(input, "txt", "hash", job.OutputDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                try
                {
                    await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    created = true;
                    await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (attempt < 8 && !created && File.Exists(output))
                {
                    // name taken in the meantime
                }
            }

            progress.Report(new JobProgress(1, Path.GetFileName(output)));
            return ConversionResult.Ok([output], sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            if (created) TryDelete(output);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created) TryDelete(output);
            return ConversionResult.Fail($"Could not hash '{Path.GetFileName(input)}': {ex.Message}");
        }
    }

    /// <summary>Hashes a file with every algorithm in a single pass.</summary>
    public static async Task<IReadOnlyList<string>> ComputeAsync(
        string path,
        IReadOnlyList<HashAlgorithms.Algorithm> algorithms,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        var hashers = algorithms.Select(a => IncrementalHash.CreateHash(a.Name)).ToList();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 20];
            var length = stream.Length;
            long done = 0;
            int read;
            var lastReport = Environment.TickCount64;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                foreach (var hasher in hashers) hasher.AppendData(buffer, 0, read);
                done += read;

                var now = Environment.TickCount64;
                if (progress is not null && length > 0 && now - lastReport >= 100)
                {
                    lastReport = now;
                    progress.Report(new JobProgress(Math.Min(0.99, (double)done / length)));
                }
            }
            return hashers.Select(h => Convert.ToHexStringLower(h.GetHashAndReset())).ToList();
        }
        finally
        {
            foreach (var hasher in hashers) hasher.Dispose();
        }
    }

    /// <summary>The file content: sha256sum lines, with "# LABEL" headers when more than one algorithm was requested.</summary>
    public static string Format(string fileName, IReadOnlyList<HashAlgorithms.Algorithm> algorithms, IReadOnlyList<string> hashes)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < algorithms.Count; i++)
        {
            if (algorithms.Count > 1) sb.Append("# ").Append(algorithms[i].Label).Append('\n');
            sb.Append(hashes[i]).Append("  ").Append(fileName).Append('\n');
        }
        return sb.ToString();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
