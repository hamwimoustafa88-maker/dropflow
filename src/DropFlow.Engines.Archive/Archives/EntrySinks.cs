using System.IO.Compression;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.Writers.Tar;

namespace DropFlow.Engines.Archive.Archives;

/// <summary>Writes entries into a brand-new folder. Never overwrites: files are opened with CreateNew.</summary>
internal sealed class FolderSink : IEntrySink
{
    private readonly string _root;

    public FolderSink(string folder)
    {
        _root = SafeEntryPath.WithTrailingSeparator(folder);
        Directory.CreateDirectory(_root);
    }

    public void AddDirectory(string relativePath, DateTime? modified)
    {
        var path = SafeEntryPath.ResolveUnder(_root, relativePath);
        EnsureDirectory(path);
    }

    public void AddFile(string relativePath, Stream content, long declaredSize, DateTime? modified)
    {
        var path = SafeEntryPath.ResolveUnder(_root, relativePath);
        EnsureDirectory(Path.GetDirectoryName(path)!);

        // CreateNew: if anything exists at this path (a race, a case-insensitive clash) this throws instead of overwriting.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            content.CopyTo(file, 81920);

        if (modified is { } time) TrySetTime(path, time);
    }

    public void Commit() { }
    public void Dispose() { }

    private void EnsureDirectory(string path)
    {
        // A file in the way (a/b where "a" was already written as a file) must fail loudly rather than be replaced.
        for (var current = path; current.Length > _root.Length; current = Path.GetDirectoryName(current)!)
        {
            if (File.Exists(current))
                throw new ArchiveException(ArchiveProblem.Conflict, $"Cannot create folder '{Path.GetFileName(current)}': a file with that name already exists.");
        }
        Directory.CreateDirectory(path);
    }

    private static void TrySetTime(string path, DateTime time)
    {
        try
        {
            if (time.Year < 1980 || time > DateTime.Now.AddDays(1)) return;
            File.SetLastWriteTime(path, time);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        {
            // timestamps are cosmetic
        }
    }
}

/// <summary>Writes a zip with the in-box ZipArchive: sets the UTF-8 flag for non-ASCII names, supports Zip64.</summary>
internal sealed class ZipSink : IEntrySink
{
    private readonly ZipArchive _zip;
    private readonly CompressionLevel _level;

    public ZipSink(Stream output, int level)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        _level = MapLevel(level);
    }

    public static CompressionLevel MapLevel(int level) => level switch
    {
        <= 0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 7 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    public void AddDirectory(string relativePath, DateTime? modified)
    {
        var entry = _zip.CreateEntry(relativePath + "/");
        entry.LastWriteTime = ClampTime(modified);
    }

    public void AddFile(string relativePath, Stream content, long declaredSize, DateTime? modified)
    {
        var entry = _zip.CreateEntry(relativePath, _level);
        entry.LastWriteTime = ClampTime(modified);
        using var target = entry.Open();
        content.CopyTo(target, 81920);
    }

    public void Commit() => _zip.Dispose(); // writes the central directory
    public void Dispose() { /* disposing without Commit() leaves a truncated file, which the caller deletes */ }

    internal static DateTimeOffset ClampTime(DateTime? time)
    {
        var t = time ?? DateTime.Now;
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        else if (t.Year > 2107) t = new DateTime(2107, 12, 31);
        return new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Local) : t);
    }
}

/// <summary>
/// Writes a GNU-format tar (long and non-ASCII names are stored as UTF-8). A tar header needs the exact size up front, so an
/// entry of unknown size is spooled first and an entry of declared size is checked byte for byte while it streams.
/// </summary>
internal sealed class TarSink : IEntrySink
{
    private readonly SharpCompress.Writers.Tar.TarWriter _writer;

    public TarSink(Stream output)
    {
        var options = new TarWriterOptions(CompressionType.None, true, TarHeaderWriteFormat.GNU_TAR_LONG_LINK) { LeaveStreamOpen = true };
        _writer = new SharpCompress.Writers.Tar.TarWriter(output, options);
    }

    public void AddDirectory(string relativePath, DateTime? modified) => _writer.WriteDirectory(relativePath, modified);

    public void AddFile(string relativePath, Stream content, long declaredSize, DateTime? modified)
    {
        if (declaredSize >= 0)
        {
            var exact = new ExactLengthStream(content, declaredSize, relativePath);
            _writer.Write(relativePath, exact, modified, declaredSize);
            exact.EnsureFullyConsumed(); // the writer stops after 'declaredSize' bytes; extra data means the header lied
            return;
        }

        using var spool = new SpoolStream();
        content.CopyTo(spool, 81920);
        spool.Rewind();
        _writer.Write(relativePath, spool, modified, spool.Length);
    }

    public void Commit() => _writer.Dispose(); // writes the end-of-archive blocks
    public void Dispose() { }
}

/// <summary>A single file compressed with gzip ("report.txt.gz"). Only valid for archives holding exactly one file.</summary>
internal sealed class GzipSingleSink : IEntrySink
{
    private readonly GZipStream _gzip;
    private bool _written;

    public GzipSingleSink(Stream output, int level) => _gzip = NewGzip(output, level);

    public static GZipStream NewGzip(Stream output, int level) =>
        new(output, new ZLibCompressionOptions { CompressionLevel = Math.Clamp(level, 0, 9) }, leaveOpen: true);

    public void AddDirectory(string relativePath, DateTime? modified) { /* a bare gzip has no folders */ }

    public void AddFile(string relativePath, Stream content, long declaredSize, DateTime? modified)
    {
        if (_written) throw new ArchiveException(ArchiveProblem.Unsupported, "A .gz holds one file only; this archive has more.");
        _written = true;
        content.CopyTo(_gzip, 81920);
    }

    public void Commit() => _gzip.Dispose();
    public void Dispose() { }
}

/// <summary>A tar compressed with gzip (".tar.gz").</summary>
internal sealed class TarGzipSink : IEntrySink
{
    private readonly GZipStream _gzip;
    private readonly TarSink _tar;

    public TarGzipSink(Stream output, int level)
    {
        _gzip = GzipSingleSink.NewGzip(output, level);
        _tar = new TarSink(_gzip);
    }

    public void AddDirectory(string relativePath, DateTime? modified) => _tar.AddDirectory(relativePath, modified);
    public void AddFile(string relativePath, Stream content, long declaredSize, DateTime? modified) => _tar.AddFile(relativePath, content, declaredSize, modified);

    public void Commit()
    {
        _tar.Commit();
        _gzip.Dispose();
    }

    public void Dispose() { }
}

/// <summary>Passes a stream through but fails when it delivers more or fewer bytes than the header promised (a corrupt or hostile archive).</summary>
internal sealed class ExactLengthStream(Stream inner, long expected, string name) : Stream
{
    private long _seen;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expected;
    public override long Position { get => _seen; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>Reads one more byte; throws if the stream still has data beyond the promised size.</summary>
    public void EnsureFullyConsumed()
    {
        Span<byte> probe = stackalloc byte[1];
        if (Read(probe) == 0) return;
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;

        // read at most one byte beyond the promise so overflow is detected without ever being passed on
        var allowed = (int)Math.Min(buffer.Length, Math.Max(0, expected - _seen) + 1);
        var n = inner.Read(buffer[..allowed]);
        _seen += n;
        if (_seen > expected)
            throw new ArchiveException(ArchiveProblem.Corrupt, $"'{name}' is larger than its header says; the archive is damaged or hostile.");
        if (n == 0 && _seen < expected)
            throw new ArchiveException(ArchiveProblem.Corrupt, $"'{name}' is shorter than its header says; the archive is damaged.");
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Buffers a stream of unknown length: in memory while small, in a self-deleting temp file when large.</summary>
internal sealed class SpoolStream : Stream
{
    private const int MemoryLimit = 8 * 1024 * 1024;
    private Stream _inner = new MemoryStream();

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public void Rewind() => _inner.Position = 0;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_inner is MemoryStream ms && ms.Length + buffer.Length > MemoryLimit)
        {
            var path = Path.Combine(Path.GetTempPath(), "dropflow-spool-" + Guid.NewGuid().ToString("N"));
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            ms.Position = 0;
            ms.CopyTo(file);
            _inner = file;
            ms.Dispose();
        }
        _inner.Write(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
