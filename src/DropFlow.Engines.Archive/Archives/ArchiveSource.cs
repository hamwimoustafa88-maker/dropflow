using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace DropFlow.Engines.Archive.Archives;

internal enum SourceKind
{
    /// <summary>zip, tar, 7z or rar opened through SharpCompress with random access.</summary>
    Archive,

    /// <summary>A gzip file holding one ordinary file ("report.txt.gz").</summary>
    GzipFile,

    /// <summary>A gzip file holding a tar ("photos.tar.gz" / ".tgz").</summary>
    GzipTar,
}

/// <param name="Size">Declared uncompressed size, or -1 when unknown. Never trusted for safety, only for progress and tar headers.</param>
internal sealed record SourceEntry(string Name, bool IsDirectory, long Size, bool IsLink, bool IsEncrypted, DateTime? Modified);

/// <summary>One entry plus a way to read its bytes. The stream is only valid until the enumeration advances.</summary>
internal readonly record struct SourceItem(SourceEntry Entry, Func<Stream> Open);

/// <summary>
/// A readable archive. Hides whether it is zip/tar/7z/rar (SharpCompress) or a gzip stream, so extraction and conversion
/// share one safe pipeline.
/// </summary>
internal abstract class ArchiveSource : IDisposable
{
    public abstract SourceKind Kind { get; }

    /// <summary>The SharpCompress archive type, when <see cref="Kind"/> is <see cref="SourceKind.Archive"/>.</summary>
    public virtual ArchiveType? ArchiveType => null;

    /// <summary>All entries known up front (central directory) so limits can be checked before anything is written; null for streams.</summary>
    public abstract IReadOnlyList<SourceEntry>? Listing { get; }

    /// <summary>Best-effort 0..1 position in the physical file, used for progress when sizes are unknown.</summary>
    public abstract double PhysicalProgress { get; }

    public abstract IEnumerable<SourceItem> Items(CancellationToken cancellationToken);

    public virtual void Dispose() { }

    public static ArchiveSource Open(string path, string? password)
    {
        if (!File.Exists(path)) throw new ArchiveException(ArchiveProblem.Corrupt, $"File not found: {path}");

        try
        {
            return GzipSource.LooksLikeGzip(path) ? new GzipSource(path) : new RandomAccessSource(path, password);
        }
        catch (ArchiveException) { throw; }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            throw Explain(path, password, ex);
        }
    }

    /// <summary>Turns a reader exception into a message a person can act on, recognising the password cases.</summary>
    internal static ArchiveException Explain(string path, string? password, Exception ex)
    {
        var name = Path.GetFileName(path);
        if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return password is null
                ? new ArchiveException(ArchiveProblem.Encrypted, $"'{name}' is password-protected. Provide the password (option 'password') to open this archive.", ex)
                : new ArchiveException(ArchiveProblem.Encrypted, $"The password for '{name}' is wrong, or the archive is damaged ({ex.Message}).", ex);
        }

        return password is null
            ? new ArchiveException(ArchiveProblem.Corrupt, $"'{name}' is not a valid archive or is damaged ({ex.Message}).", ex)
            : new ArchiveException(ArchiveProblem.Corrupt, $"'{name}' could not be opened: it is damaged, not an archive, or the password is wrong ({ex.Message}).", ex);
    }

    /// <summary>Exceptions a damaged or unsupported archive makes the readers throw (as opposed to bugs in this code).</summary>
    internal static bool IsReadFailure(Exception ex) =>
        ex is InvalidDataException or EndOfStreamException or NotSupportedException or NotImplementedException
            or InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException
            or FormatException or InvalidFormatException or SharpCompressException || ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true;

    internal static SourceEntry Map(IEntry e)
    {
        var encrypted = false;
        try { encrypted = e.IsEncrypted; } catch (NotImplementedException) { }
        return new SourceEntry(e.Key ?? "", e.IsDirectory, e.Size < 0 ? -1 : e.Size, IsLink(e), encrypted, SafeTime(e));
    }

    private static DateTime? SafeTime(IEntry e)
    {
        try { return e.LastModifiedTime; } catch (NotImplementedException) { return null; }
    }

    /// <summary>Symbolic and hard links: tar reports a link target, zip/7z carry a unix mode or a Windows reparse flag in their attributes.</summary>
    private static bool IsLink(IEntry e)
    {
        if (!string.IsNullOrEmpty(e.LinkTarget)) return true;
        try
        {
            if (e.Attrib is int attrib)
            {
                if (((attrib >> 16) & 0xF000) == 0xA000) return true;     // unix S_IFLNK
                if ((attrib & 0x400) != 0) return true;                   // FILE_ATTRIBUTE_REPARSE_POINT
            }
        }
        catch (NotImplementedException) { /* tar entries do not expose attributes */ }
        return false;
    }
}

/// <summary>zip, tar, 7z and rar through SharpCompress's random-access API.</summary>
internal sealed class RandomAccessSource : ArchiveSource
{
    private readonly IArchive _archive;
    private readonly List<SourceEntry> _listing;

    public RandomAccessSource(string path, string? password)
    {
        var options = new ReaderOptions { Password = password, LeaveStreamOpen = false };
        _archive = ArchiveFactory.OpenArchive(path, options);
        try
        {
            _listing = _archive.Entries.Select(Map).ToList();
        }
        catch
        {
            _archive.Dispose();
            throw;
        }
    }

    public override SourceKind Kind => SourceKind.Archive;
    public override ArchiveType? ArchiveType => _archive.Type;
    public override IReadOnlyList<SourceEntry>? Listing => _listing;
    public override double PhysicalProgress => 0;

    public override IEnumerable<SourceItem> Items(CancellationToken cancellationToken)
    {
        // Solid 7z/rar must be read in order or every entry would re-decode the whole block.
        if (_archive.IsSolid || _archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = _archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SourceItem(Map(reader.Entry), () => reader.OpenEntryStream());
            }
        }
        else
        {
            foreach (var entry in _archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = entry;
                yield return new SourceItem(Map(entry), () => current.OpenEntryStream());
            }
        }
    }

    public override void Dispose() => _archive.Dispose();
}

/// <summary>A .gz file: either a tar inside ("x.tar.gz", forward-only) or one ordinary file ("x.txt.gz").</summary>
internal sealed class GzipSource : ArchiveSource
{
    private readonly string _path;
    private readonly bool _isTar;
    private FileStream? _file;

    public GzipSource(string path)
    {
        _path = path;
        var name = Path.GetFileName(path);
        _isTar = name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                 || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                 || InnerLooksLikeTar(path);
    }

    public override SourceKind Kind => _isTar ? SourceKind.GzipTar : SourceKind.GzipFile;
    public override IReadOnlyList<SourceEntry>? Listing => null;

    public override double PhysicalProgress
    {
        get
        {
            var f = _file;
            try { return f is { CanSeek: true } && f.Length > 0 ? Math.Clamp((double)f.Position / f.Length, 0, 1) : 0; }
            catch (ObjectDisposedException) { return 1; }
        }
    }

    public static bool LooksLikeGzip(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
    }

    private static bool InnerLooksLikeTar(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var header = new byte[512];
            var read = 0;
            while (read < header.Length)
            {
                var n = gz.Read(header, read, header.Length - read);
                if (n == 0) break;
                read += n;
            }
            // POSIX "ustar\0" or GNU "ustar  \0" at offset 257.
            return read >= 262 && header[257] == (byte)'u' && header[258] == (byte)'s' && header[259] == (byte)'t'
                   && header[260] == (byte)'a' && header[261] == (byte)'r';
        }
        catch (InvalidDataException)
        {
            return false; // a damaged stream is reported later, with a proper message, when it is actually read
        }
    }

    public override IEnumerable<SourceItem> Items(CancellationToken cancellationToken)
    {
        _file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        try
        {
            using var gz = new GZipStream(_file, CompressionMode.Decompress, leaveOpen: true);
            if (_isTar)
            {
                using var reader = ReaderFactory.OpenReader(gz, new ReaderOptions { LeaveStreamOpen = true });
                while (reader.MoveToNextEntry())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new SourceItem(Map(reader.Entry), () => reader.OpenEntryStream());
                }
            }
            else
            {
                var modified = File.GetLastWriteTime(_path);
                yield return new SourceItem(new SourceEntry(StemOf(_path), false, -1, false, false, modified), () => gz);
            }
        }
        finally
        {
            _file.Dispose();
        }
    }

    /// <summary>"report.txt.gz" gives "report.txt"; "photos.gz" gives "photos".</summary>
    internal static string StemOf(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        else if (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) name = name[..^4] + ".tar";
        return name.Length == 0 ? "file" : name;
    }
}
