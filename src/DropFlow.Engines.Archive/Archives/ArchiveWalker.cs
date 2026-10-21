using System.Diagnostics;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Archive.Archives;

/// <summary>Where the entries of a source archive end up (a folder, a zip, a tar...).</summary>
internal interface IEntrySink : IDisposable
{
    /// <param name="relativePath">Safe relative path with '/' separators, already de-duplicated.</param>
    void AddDirectory(string relativePath, DateTime? modified);

    /// <param name="content">Guarded stream: counts bytes, enforces limits, honours cancellation. Read it to the end.</param>
    /// <param name="declaredSize">Size the archive claims, or -1. The sink must not trust it.</param>
    void AddFile(string relativePath, Stream content, long declaredSize, DateTime? modified);

    /// <summary>Writes trailers (zip central directory, tar end blocks...). Only call when every entry succeeded.</summary>
    void Commit();
}

internal sealed class WalkReport
{
    public int Files { get; set; }
    public int Directories { get; set; }
    public int SkippedLinks { get; set; }
    public int SkippedUnsafe { get; set; }
    public int Sanitized { get; set; }
    public int Renamed { get; set; }
    public long Bytes { get; set; }

    /// <summary>Human-readable summary of what was left out, or null when nothing was.</summary>
    public string? Note
    {
        get
        {
            var parts = new List<string>();
            if (SkippedLinks > 0) parts.Add($"{SkippedLinks} link(s) skipped");
            if (SkippedUnsafe > 0) parts.Add($"{SkippedUnsafe} entr{(SkippedUnsafe == 1 ? "y" : "ies")} with unsafe paths skipped");
            if (Sanitized > 0) parts.Add($"{Sanitized} name(s) adjusted for Windows");
            if (Renamed > 0) parts.Add($"{Renamed} duplicate name(s) renamed");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }
}

/// <summary>
/// The safe pipeline every extraction and conversion goes through: checks entry count and size limits, rejects or skips
/// unsafe paths, never creates links, makes names unique, and counts the bytes that really come out of the decompressor.
/// </summary>
internal static class ArchiveWalker
{
    public static WalkReport Run(
        ArchiveSource source,
        IEntrySink sink,
        ArchiveLimits limits,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        var report = new WalkReport();
        long declaredTotal = 0;

        // 1) With a central directory we can refuse a hostile archive before writing a single byte.
        if (source.Listing is { } listing)
        {
            if (listing.Count > limits.MaxEntries)
                throw new ArchiveException(ArchiveProblem.TooManyEntries,
                    $"The archive has {listing.Count:N0} entries, more than the limit of {limits.MaxEntries:N0}. Raise the 'maxEntries' option if you trust it.");

            foreach (var entry in listing)
            {
                if (entry.IsEncrypted && limits.Password is null) throw EncryptedError(entry.Name);
                if (!entry.IsLink && limits.UnsafePaths == UnsafePathPolicy.Fail) RequireSafe(entry.Name);
                if (entry.Size > 0 && !entry.IsDirectory) declaredTotal += entry.Size;
            }

            if (declaredTotal > limits.MaxTotalBytes) throw TooLarge(limits, declaredTotal);
        }

        var tracker = new ProgressTracker(progress, declaredTotal, source);
        var names = new NameRegistry();
        var count = 0;

        // 2) Stream every entry through the guards.
        foreach (var item in source.Items(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > limits.MaxEntries)
                throw new ArchiveException(ArchiveProblem.TooManyEntries,
                    $"The archive has more than {limits.MaxEntries:N0} entries. Raise the 'maxEntries' option if you trust it.");

            var entry = item.Entry;
            if (entry.IsEncrypted && limits.Password is null) throw EncryptedError(entry.Name);

            if (entry.IsLink)
            {
                report.SkippedLinks++; // links are never created: they are the classic way to write outside the destination
                continue;
            }

            var path = SafeEntryPath.Evaluate(entry.Name);
            switch (path.Status)
            {
                case EntryPathStatus.Skip:
                    continue;
                case EntryPathStatus.Unsafe when limits.UnsafePaths == UnsafePathPolicy.Skip:
                    report.SkippedUnsafe++;
                    continue;
                case EntryPathStatus.Unsafe:
                    throw UnsafeError(entry.Name, path.Reason);
                case EntryPathStatus.Sanitized:
                    report.Sanitized++;
                    break;
            }

            if (entry.IsDirectory)
            {
                names.AddDirectory(path.RelativePath);
                sink.AddDirectory(path.RelativePath, entry.Modified);
                report.Directories++;
                continue;
            }

            var unique = names.AddFile(path.RelativePath);
            if (!string.Equals(unique, path.RelativePath, StringComparison.Ordinal)) report.Renamed++;

            using var raw = item.Open();
            using var guarded = new GuardedStream(raw, limits, tracker, cancellationToken);
            sink.AddFile(unique, guarded, entry.Size, entry.Modified);
            report.Files++;
            report.Bytes = tracker.Bytes;
            tracker.Report(unique, force: true);
        }

        return report;
    }

    private static void RequireSafe(string rawName)
    {
        var path = SafeEntryPath.Evaluate(rawName);
        if (path.Status == EntryPathStatus.Unsafe) throw UnsafeError(rawName, path.Reason);
    }

    private static ArchiveException UnsafeError(string name, string? reason) =>
        new(ArchiveProblem.UnsafePath,
            $"Unsafe path in archive: '{Printable(name)}' {reason}. Nothing was extracted. (Set option unsafePaths=skip to skip such entries.)");

    private static ArchiveException EncryptedError(string name) =>
        new(ArchiveProblem.Encrypted, $"'{Printable(name)}' is password-protected. Provide the password (option 'password') to open this archive.");

    internal static ArchiveException TooLarge(ArchiveLimits limits, long size) =>
        new(ArchiveProblem.TooLarge,
            $"The archive expands to more than the limit of {FormatBytes(limits.MaxTotalBytes)} (it claims {FormatBytes(size)}). " +
            "It may be a decompression bomb. Raise the 'maxTotalMB' option if you trust it.");

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => $"{bytes} bytes",
    };

    private static string Printable(string name)
    {
        var cleaned = new string(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return cleaned.Length > 120 ? cleaned[..120] + "..." : cleaned;
    }

    /// <summary>Throttled progress: fraction of declared bytes, or of the physical file when sizes are unknown.</summary>
    internal sealed class ProgressTracker(IProgress<JobProgress>? progress, long declaredTotal, ArchiveSource source)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReportMs = -1000;

        public long Bytes { get; private set; }

        public void Add(long count, string? current = null)
        {
            Bytes += count;
            Report(current, force: false);
        }

        public void Report(string? message, bool force)
        {
            if (progress is null) return;
            var now = _clock.ElapsedMilliseconds;
            if (!force && now - _lastReportMs < 120) return;
            _lastReportMs = now;

            var fraction = declaredTotal > 0 ? (double)Bytes / declaredTotal : source.PhysicalProgress;
            progress.Report(new JobProgress(Math.Clamp(fraction, 0, 0.99), message));
        }
    }

    /// <summary>Remembers produced names so duplicates and case-insensitive clashes get "name (2).ext" instead of overwriting.</summary>
    private sealed class NameRegistry
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

        public void AddDirectory(string relative) => RegisterParents(relative, includeSelf: true);

        public string AddFile(string relative)
        {
            RegisterParents(relative, includeSelf: false);
            var candidate = relative;
            for (var n = 2; _files.Contains(candidate) || _directories.Contains(candidate); n++)
                candidate = WithSuffix(relative, n);
            _files.Add(candidate);
            return candidate;
        }

        private void RegisterParents(string relative, bool includeSelf)
        {
            var parts = relative.Split('/');
            var limit = includeSelf ? parts.Length : parts.Length - 1;
            var prefix = "";
            for (var i = 0; i < limit; i++)
            {
                prefix = i == 0 ? parts[0] : prefix + "/" + parts[i];
                if (_files.Contains(prefix))
                    throw new ArchiveException(ArchiveProblem.Conflict, $"The archive contains both a file and a folder named '{prefix}'.");
                _directories.Add(prefix);
            }
        }

        private static string WithSuffix(string relative, int n)
        {
            var slash = relative.LastIndexOf('/');
            var dir = slash < 0 ? "" : relative[..(slash + 1)];
            var file = relative[(slash + 1)..];
            var ext = Path.GetExtension(file);
            var stem = file[..^ext.Length];
            return $"{dir}{stem} ({n}){ext}";
        }
    }
}

/// <summary>
/// Wraps an entry's decompressed stream. Counts the bytes that actually come out (archive headers can lie about sizes),
/// aborts when the total exceeds the limit, and checks for cancellation on every read.
/// </summary>
internal sealed class GuardedStream(Stream inner, ArchiveLimits limits, ArchiveWalker.ProgressTracker tracker, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = inner.Read(buffer);
        if (n > 0)
        {
            tracker.Add(n);
            if (tracker.Bytes > limits.MaxTotalBytes)
                throw ArchiveWalker.TooLarge(limits, tracker.Bytes);
        }
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
