using System.IO.Compression;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Common;

namespace DropFlow.Engines.Archive.Archives;

/// <summary>
/// Archive tools. <c>arc.extract</c> unpacks into a new folder beside the archive. <c>arc.zip</c> packs files and folders
/// into one zip. <c>convert:zip|tar|gz|7z</c> re-packs an archive (zip, tar, gz, tgz, rar, 7z) into another format.
/// <para>
/// Reading is done in-process (SharpCompress for zip/tar/7z/rar, the BCL for gzip), so extraction needs no engine. Writing zip,
/// tar and gzip is in-process too; only <c>convert:7z</c> needs 7z.exe (<see cref="EngineId.SevenZip"/>). RAR is read-only.
/// </para>
/// <para>
/// <c>convert:gz</c>: gzip compresses one stream, so (1) a <c>.tar</c> is gzipped as it is, giving <c>name.tar.gz</c>;
/// (2) an archive holding exactly one top-level file becomes a plain gzip of that file (<c>name.gz</c>); (3) any other
/// archive (several files, or folders) becomes a tar of all its entries, gzipped: <c>name.tar.gz</c>.
/// </para>
/// </summary>
public sealed class ArchiveConverter(IEngineProvider engines) : IConverter
{
    public const string ExtractOperation = "arc.extract";
    public const string ZipOperation = "arc.zip";

    private static readonly string[] InputFormats = ["zip", "tar", "gz", "tgz", "rar", "7z"];
    private static readonly string[] OutputFormats = ["zip", "tar", "gz", "7z"];

    private static readonly ToolOption LevelOption = new("level", ToolOptionKind.Range, 6, null, 0, 9);

    public IReadOnlyCollection<string> Operations { get; } =
        [.. OutputFormats.Select(Operation.Convert), ExtractOperation, ZipOperation];

    public bool Supports(string operation, string inputExtension)
    {
        var ext = NormalizeExtension(inputExtension);
        if (operation == ExtractOperation) return InputFormats.Contains(ext);
        if (operation == ZipOperation) return true; // any file or folder can be zipped
        if (!Operation.IsConvert(operation)) return false;

        var target = Operation.TargetOf(operation);
        return OutputFormats.Contains(target) && InputFormats.Contains(ext) && ext != target;
    }

    public EngineId EngineFor(string operation) => operation == Operation.Convert("7z") ? EngineId.SevenZip : EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) => operation == ZipOperation ? [LevelOption] : [];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (job.Operation == ZipOperation) return await Task.Run(() => ZipFiles(job, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

            var input = job.Inputs[0];
            if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

            if (job.Operation == ExtractOperation) return await Task.Run(() => Extract(job, input, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (!Operation.IsConvert(job.Operation)) return ConversionResult.Fail($"Unsupported operation '{job.Operation}'.");

            var target = Operation.TargetOf(job.Operation);
            if (target == "7z") return await ConvertTo7z(job, input, progress, cancellationToken).ConfigureAwait(false);
            if (!OutputFormats.Contains(target)) return ConversionResult.Fail($"Cannot create .{target} archives.");
            return await Task.Run(() => Convert(job, input, target, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveException ex)
        {
            return ConversionResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ArchiveSource.IsReadFailure(ex))
        {
            // Mid-read failures: a wrong password and a damaged archive look alike to the reader, so say both.
            var name = Path.GetFileName(job.Inputs[0]);
            return ConversionResult.Fail(ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? $"The password for '{name}' is wrong, or the archive is damaged ({ex.Message})."
                : $"The archive could not be processed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- extract

    private static ConversionResult Extract(ConversionJob job, string input, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var limits = ArchiveLimits.FromJob(job);
        using var source = ArchiveSource.Open(input, limits.Password);
        using var created = new CreatedOutputs();

        var destination = new OutputPathResolver().ForFolder(OutputNaming.BaseOf(input), "extracted", job.OutputDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        created.AddFolder(destination);

        WalkReport report;
        using (var sink = new FolderSink(destination))
        {
            report = ArchiveWalker.Run(source, sink, limits, progress, ct);
            sink.Commit();
        }

        RequireContent(report, input);
        created.Commit();
        progress.Report(new JobProgress(1, Path.GetFileName(destination)));
        return ConversionResult.Ok([destination], default, report.Note);
    }

    // ---------------------------------------------------------------- convert (zip, tar, gz)

    private static ConversionResult Convert(ConversionJob job, string input, string target, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var limits = ArchiveLimits.FromJob(job);
        var level = OutputNaming.Level(job, 6);
        using var source = ArchiveSource.Open(input, limits.Password);
        if (source.Listing is { Count: 0 }) RequireContent(new WalkReport(), input);   // also covers the routes that copy bytes without walking entries
        using var created = new CreatedOutputs();
        var pseudo = OutputNaming.BaseOf(input);
        WalkReport? report = null;

        string output;
        var isPlainTar = source.Kind == SourceKind.Archive && source.ArchiveType == SharpCompress.Common.ArchiveType.Tar;

        // Gzipping a tar byte for byte is only done when every entry is plain and safe; otherwise the entries are re-packed through
        // the safe pipeline (which drops links and neutralises hostile names) so the result is never a copy of a booby-trapped tar.
        var tarIsClean = isPlainTar && source.Listing!.All(e => !e.IsLink && !e.IsEncrypted && SafeEntryPath.Evaluate(e.Name).Status is EntryPathStatus.Ok or EntryPathStatus.Skip);

        if (target == "gz" && tarIsClean)
        {
            // gzip of a tar is simply the tar, compressed: keep every byte of metadata.
            output = WriteNewFile(job, pseudo, "tar.gz", "converted", created, fs =>
            {
                using var gzip = GzipSingleSink.NewGzip(fs, level);
                using var tar = File.OpenRead(input);
                StreamCopy.Copy(tar, gzip, () => Fraction(tar), null, progress, ct);
            });
        }
        else if (target == "gz" && source.Kind == SourceKind.GzipTar)
        {
            // already a tar.gz: hand back an identical copy under the new name
            output = WriteNewFile(job, pseudo, "tar.gz", "converted", created, fs =>
            {
                using var original = File.OpenRead(input);
                StreamCopy.Copy(original, fs, () => Fraction(original), null, progress, ct);
            });
        }
        else if (target == "tar" && source.Kind == SourceKind.GzipTar)
        {
            // .tar.gz -> .tar is plain decompression, no re-packing
            output = WriteNewFile(job, pseudo, "tar", "converted", created, fs =>
            {
                using var compressed = File.OpenRead(input);
                using var gunzip = new GZipStream(compressed, CompressionMode.Decompress);
                StreamCopy.Copy(gunzip, fs, () => Fraction(compressed), limits, progress, ct);
            });
        }
        else
        {
            var singleFile = target == "gz" && IsSingleTopLevelFile(source);
            var extension = target == "gz" && !singleFile ? "tar.gz" : target;
            output = WriteNewFile(job, pseudo, extension, "converted", created, fs =>
            {
                using IEntrySink sink = target switch
                {
                    "zip" => new ZipSink(fs, level),
                    "tar" => new TarSink(fs),
                    _ when singleFile => new GzipSingleSink(fs, level),
                    _ => new TarGzipSink(fs, level),
                };
                report = ArchiveWalker.Run(source, sink, limits, progress, ct);
                sink.Commit();
            });
        }

        if (report is not null) RequireContent(report, input);
        created.Commit();
        progress.Report(new JobProgress(1, Path.GetFileName(output)));
        return ConversionResult.Ok([output], default, report?.Note);
    }

    /// <summary>An archive with nothing in it is almost always a wrong or damaged file; say so instead of producing an empty result.</summary>
    private static void RequireContent(WalkReport report, string input)
    {
        if (report.Files == 0 && report.Directories == 0)
            throw new ArchiveException(ArchiveProblem.Corrupt, $"'{Path.GetFileName(input)}' contains no files or folders. It may be empty, damaged, or not really an archive.");
    }

    private static double Fraction(FileStream file) => file.Length == 0 ? 1 : (double)file.Position / file.Length;

    private static bool IsSingleTopLevelFile(ArchiveSource source)
    {
        if (source.Listing is not { } listing) return source.Kind == SourceKind.GzipFile;
        var files = listing.Where(e => !e.IsDirectory && !e.IsLink)
            .Select(e => SafeEntryPath.Evaluate(e.Name))
            .Where(p => p.Status is EntryPathStatus.Ok or EntryPathStatus.Sanitized)
            .ToList();
        return files.Count == 1 && !files[0].RelativePath.Contains('/');
    }

    /// <summary>Creates a new (never existing) output file, hands its stream to <paramref name="write"/>, and registers it for cleanup.</summary>
    private static string WriteNewFile(ConversionJob job, string pseudoInput, string extension, string suffix, CreatedOutputs created, Action<FileStream> write)
    {
        var resolver = new OutputPathResolver();
        for (var attempt = 0; ; attempt++)
        {
            var path = resolver.ForFile(pseudoInput, extension, suffix, job.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(path) && attempt < 8)
            {
                continue; // someone created it between the check and now: pick the next free name
            }

            created.AddFile(path);
            using (stream) write(stream);
            return path;
        }
    }

    // ---------------------------------------------------------------- convert to 7z (needs 7z.exe)

    private async Task<ConversionResult> ConvertTo7z(ConversionJob job, string input, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var exe = engines.Locate(EngineId.SevenZip);
        if (exe is null) return ConversionResult.Fail("7-Zip (7z.exe) is not installed. Install the 7-Zip engine to create .7z archives.");

        var limits = ArchiveLimits.FromJob(job);
        var level = OutputNaming.Level(job, 5);
        var staging = Path.Combine(Path.GetTempPath(), "dropflow-7z-" + Guid.NewGuid().ToString("N"));
        using var created = new CreatedOutputs();

        try
        {
            // 1) Unpack safely into a private staging folder: 7z.exe never sees the hostile archive, only our vetted files.
            WalkReport report;
            using (var source = ArchiveSource.Open(input, limits.Password))
            using (var sink = new FolderSink(staging))
            {
                var stagingProgress = new ScaledProgress(progress, 0, 0.5);
                report = await Task.Run(() => ArchiveWalker.Run(source, sink, limits, stagingProgress, ct), ct).ConfigureAwait(false);
            }

            if (report.Files == 0 && report.Directories == 0)
                return ConversionResult.Fail("The archive has no files to convert.");

            // 2) Pack with 7z.exe to a temporary name, then move it into place without overwriting anything.
            var final = new OutputPathResolver().ForFile(OutputNaming.BaseOf(input), "7z", "converted", job.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            var temporary = final + ".part-" + Guid.NewGuid().ToString("N")[..8];
            created.AddFile(temporary);

            var result = await ProcessRunner.RunAsync(
                exe,
                SevenZipCommand.CreateArguments(temporary, level),
                new ProcessOptions
                {
                    WorkingDirectory = staging,
                    OnOutputLine = line =>
                    {
                        if (SevenZipCommand.ParsePercent(line) is { } percent)
                            progress.Report(new JobProgress(0.5 + percent / 200.0, "7z"));
                    },
                },
                ct).ConfigureAwait(false);

            if (result.Cancelled) throw new OperationCanceledException(ct);
            if (!result.Success || !File.Exists(temporary))
                return ConversionResult.Fail($"7-Zip failed (exit code {result.ExitCode}): {result.LastMessage}");

            File.Move(temporary, final, overwrite: false);
            created.Commit();
            progress.Report(new JobProgress(1, Path.GetFileName(final)));
            return ConversionResult.Ok([final], default, report.Note);
        }
        finally
        {
            CreatedOutputs.TryDelete(() => { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); });
        }
    }

    // ---------------------------------------------------------------- arc.zip

    private static ConversionResult ZipFiles(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var level = OutputNaming.Level(job, 6);
        foreach (var input in job.Inputs)
            if (!File.Exists(input) && !Directory.Exists(input))
                return ConversionResult.Fail($"Not found: {input}");

        var plan = ZipPlan.Build(job.Inputs, ct);
        using var created = new CreatedOutputs();
        var output = WriteNewFile(job, OutputNaming.BaseOfAny(job.Inputs[0]), "zip", "zipped", created,
            stream => WriteZip(stream, plan, level, progress, ct));

        created.Commit();
        progress.Report(new JobProgress(1, Path.GetFileName(output)));
        return ConversionResult.Ok([output], default, plan.SkippedLinks > 0 ? $"{plan.SkippedLinks} link(s) skipped" : null);
    }

    private static void WriteZip(Stream stream, ZipPlan plan, int level, IProgress<JobProgress> progress, CancellationToken ct)
    {
        using var sink = new ZipSink(stream, level);
        long done = 0;
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                sink.AddDirectory(item.EntryName, item.Modified);
                continue;
            }

            try
            {
                using var file = new FileStream(item.FullPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
                var counter = new CountingReadStream(file, ct, n =>
                {
                    done += n;
                    if (plan.TotalBytes > 0) progress.Report(new JobProgress(Math.Min(0.99, (double)done / plan.TotalBytes), item.EntryName));
                });
                sink.AddFile(item.EntryName, counter, item.Size, item.Modified);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ArchiveException(ArchiveProblem.Unsupported, $"Cannot read '{item.FullPath}': {ex.Message}", ex);
            }
        }
        sink.Commit();
    }

    private static string NormalizeExtension(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext == "tar.gz" ? "tgz" : ext;
    }
}

/// <summary>Plain copy loops with cancellation, progress and an optional output cap.</summary>
internal static class StreamCopy
{
    public static void Copy(Stream source, Stream destination, Func<double> fraction, ArchiveLimits? limits, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        var lastReport = Environment.TickCount64;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (limits is not null && total > limits.MaxTotalBytes) throw ArchiveWalker.TooLarge(limits, total);
            destination.Write(buffer, 0, read);

            var now = Environment.TickCount64;
            if (now - lastReport >= 120)
            {
                lastReport = now;
                progress.Report(new JobProgress(Math.Clamp(fraction(), 0, 0.99)));
            }
        }
    }
}

/// <summary>Counts bytes read and checks cancellation, so zip creation reports progress and stops promptly.</summary>
internal sealed class CountingReadStream(Stream inner, CancellationToken ct, Action<int> onRead) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        var n = inner.Read(buffer);
        if (n > 0) onRead(n);
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
