using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Archives;
using DropFlow.Engines.Common;
using DropFlow.TestSupport;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DropFlow.Engines.Archive.Tests;

/// <summary>Collects progress reports synchronously (Progress&lt;T&gt; would post to the thread pool and reorder them).</summary>
internal sealed class Recorder : IProgress<JobProgress>
{
    private readonly List<JobProgress> _items = [];
    public IReadOnlyList<JobProgress> Items { get { lock (_items) return [.. _items]; } }
    public Action<JobProgress>? OnReport { get; set; }

    public void Report(JobProgress value)
    {
        lock (_items) _items.Add(value);
        OnReport?.Invoke(value);
    }
}

/// <summary>An engine provider that reports nothing installed, for "engine missing" tests.</summary>
internal sealed class NoEngines : IEngineProvider
{
    public string? Locate(EngineId engine) => null;
}

internal static class Kit
{
    /// <summary>"ملف عربي" - an Arabic file name with a space.</summary>
    public const string ArabicFile = "ملف عربي";

    /// <summary>"مجلد عربي" - an Arabic folder name.</summary>
    public const string ArabicFolder = "مجلد عربي";

    public const string ArabicText = "مرحبا بالعالم";

    public static IEngineProvider RealEngines => DropFlow.TestSupport.Engines.Locator;

    public static ConversionJob Job(string operation, IEnumerable<string> inputs, Dictionary<string, object?>? options = null, string? outputDirectory = null) =>
        new([.. inputs], operation, options, outputDirectory);

    public static Task<ConversionResult> Run(IConverter converter, ConversionJob job, CancellationToken ct = default, Recorder? recorder = null) =>
        converter.RunAsync(job, recorder ?? new Recorder(), ct);

    public static Task<ConversionResult> Run(IConverter converter, string operation, string input, Dictionary<string, object?>? options = null, string? outputDirectory = null, CancellationToken ct = default, Recorder? recorder = null) =>
        Run(converter, Job(operation, [input], options, outputDirectory), ct, recorder);

    public static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>A deterministic pseudo-random buffer (so tests are repeatable but the data does not compress).</summary>
    public static byte[] Noise(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>
    /// Creates the standard sample tree: ASCII and Arabic names, a nested folder, an empty folder, binary and highly compressible data.
    /// Returns relative path ('/' separators) to content for every FILE in it.
    /// </summary>
    public static Dictionary<string, byte[]> BuildSampleTree(string root)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["readme.txt"] = Bytes("hello DropFlow\n"),
            ["docs/guide.txt"] = Bytes(string.Concat(Enumerable.Repeat("The quick brown fox. ", 500))),
            [$"docs/{ArabicFile}.txt"] = Bytes(ArabicText + "\n"),
            [$"{ArabicFolder}/صورة.bin"] = Noise(5000),
            ["big.txt"] = Bytes(new string('A', 200_000)),
            ["zero.dat"] = [],
        };

        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
        return files;
    }

    /// <summary>Every file under a folder: relative path with '/' to content hash.</summary>
    public static SortedDictionary<string, string> TreeHashes(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, file).Replace('\\', '/')] = Hash(File.ReadAllBytes(file));
        return map;
    }

    public static SortedDictionary<string, string> ExpectedHashes(Dictionary<string, byte[]> files) =>
        new(files.ToDictionary(kv => kv.Key, kv => Hash(kv.Value)), StringComparer.Ordinal);

    public static SortedSet<string> FolderNames(string root) =>
        new(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')), StringComparer.Ordinal);

    // ---------------------------------------------------------------- independent readers (NOT the code under test)

    public static SortedDictionary<string, string> ReadZipWithBcl(string zipPath)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            map[entry.FullName] = Hash(ms.ToArray());
        }
        return map;
    }

    public static SortedSet<string> ZipFolderEntries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return new SortedSet<string>(zip.Entries.Where(e => e.FullName.EndsWith('/')).Select(e => e.FullName.TrimEnd('/')), StringComparer.Ordinal);
    }

    public static SortedDictionary<string, string> ReadTarWithBcl(Stream tarStream)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var reader = new System.Formats.Tar.TarReader(tarStream, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory || entry.DataStream is null && entry.Length == 0 && entry.Name.EndsWith('/')) continue;
            using var ms = new MemoryStream();
            entry.DataStream?.CopyTo(ms);
            map[entry.Name.TrimEnd('/')] = Hash(ms.ToArray());
        }
        return map;
    }

    public static SortedDictionary<string, string> ReadTarFileWithBcl(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadTarWithBcl(fs);
    }

    public static SortedDictionary<string, string> ReadTarGzWithBcl(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var buffered = new MemoryStream();
        gz.CopyTo(buffered);
        buffered.Position = 0;
        return ReadTarWithBcl(buffered);
    }

    /// <summary>Reads any archive with SharpCompress (file name to content hash). Used as the "other" reader for 7z, rar and zip.</summary>
    public static SortedDictionary<string, string> ReadWithSharpCompress(string path)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
        if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                using var ms = new MemoryStream();
                using (var s = reader.OpenEntryStream()) s.CopyTo(ms);
                map[reader.Entry.Key!.Replace('\\', '/')] = Hash(ms.ToArray());
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                using var ms = new MemoryStream();
                using (var s = entry.OpenEntryStream()) s.CopyTo(ms);
                map[entry.Key!.Replace('\\', '/')] = Hash(ms.ToArray());
            }
        }
        return map;
    }

    /// <summary>Runs 7z.exe and returns its output (the real 7-Zip as an independent reader).</summary>
    public static async Task<ProcessResult> SevenZip(params string[] args)
    {
        var exe = RealEngines.Locate(EngineId.SevenZip) ?? throw new InvalidOperationException("7z.exe not found");
        return await ProcessRunner.RunAsync(exe, args, new ProcessOptions { Timeout = TimeSpan.FromMinutes(2) });
    }

    /// <summary>File names (with '/') listed by `7z l -slt`, folders excluded.</summary>
    public static async Task<SortedSet<string>> SevenZipFileNames(string archive)
    {
        var result = await SevenZip(SevenZipCommand.ListArguments(archive).ToArray());
        Assert.True(result.Success, result.StdErr + result.StdOut);

        // After the "----------" line every entry is a block of "Key = value" lines separated by a blank line.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var text = result.StdOut.Replace("\r\n", "\n");
        var marker = text.IndexOf("\n----------\n", StringComparison.Ordinal);
        Assert.True(marker >= 0, text);
        foreach (var block in text[(marker + 12)..].Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = block.Split('\n').Select(l => l.Split(" = ", 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!fields.TryGetValue("Path", out var path)) continue;
            var isFolder = fields.GetValueOrDefault("Folder") == "+" || (fields.GetValueOrDefault("Attributes") ?? "").StartsWith('D');
            if (!isFolder) names.Add(path.Replace('\\', '/'));
        }
        return names;
    }

    // ---------------------------------------------------------------- archive builders (independent of the code under test)

    public static string MakeZip(string zipPath, string sourceFolder)
    {
        ZipFile.CreateFromDirectory(sourceFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }

    public static string MakeTar(string tarPath, string sourceFolder)
    {
        TarFile.CreateFromDirectory(sourceFolder, tarPath, includeBaseDirectory: false);
        return tarPath;
    }

    public static string MakeTarGz(string path, string sourceFolder)
    {
        var tar = path + ".tmp.tar";
        MakeTar(tar, sourceFolder);
        using (var input = File.OpenRead(tar))
        using (var output = File.Create(path))
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            input.CopyTo(gz);
        File.Delete(tar);
        return path;
    }

    public static string MakeGzipOfFile(string gzPath, string file)
    {
        using var input = File.OpenRead(file);
        using var output = File.Create(gzPath);
        using var gz = new GZipStream(output, CompressionLevel.Optimal);
        input.CopyTo(gz);
        return gzPath;
    }

    /// <summary>Creates a zip whose entries have arbitrary (possibly hostile) names. Each entry: name to content.</summary>
    public static string CraftZip(string path, params (string Name, byte[] Content)[] entries)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var s = entry.Open();
            s.Write(content);
        }
        return path;
    }

    /// <summary>Adds a symbolic-link entry the way Unix zip tools do (mode 120777 in the external attributes).</summary>
    public static void AddZipSymlink(ZipArchive zip, string name, string target)
    {
        var entry = zip.CreateEntry(name);
        entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
        using var s = entry.Open();
        s.Write(Bytes(target));
    }

    public static string NewDir(TempDir temp, string name)
    {
        var path = temp.Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static async Task<T> Eventually<T>(Func<T> probe, Func<T, bool> done, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        T value;
        do
        {
            value = probe();
            if (done(value)) return value;
            await Task.Delay(20);
        } while (Environment.TickCount64 < end);
        return value;
    }
}

/// <summary>Skipped (visibly) unless WinRAR's Rar.exe is installed; it is only used to make a REAL .rar to read in tests.</summary>
public sealed class RarFactAttribute : FactAttribute
{
    public static readonly string? RarExe = Locate();

    public RarFactAttribute()
    {
        if (RarExe is null) Skip = "Rar.exe (WinRAR) is not installed; the hand-built RAR5 test still covers .rar reading.";
    }

    private static string? Locate()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "Rar.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinRAR", "Rar.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>Builds a minimal valid RAR 5 archive holding stored (uncompressed) files, so .rar reading is tested without WinRAR.</summary>
internal static class Rar5Builder
{
    private static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    public static byte[] Build(params (string Name, byte[] Content)[] files)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        ms.Write(Header(type: 1, flags: 0, fields: VInt(0)));            // main header, archive flags 0
        foreach (var (name, content) in files)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var fields = new List<byte>();
            fields.AddRange(VInt(0x0004));                                // file flags: CRC32 present
            fields.AddRange(VInt((ulong)content.Length));                 // unpacked size
            fields.AddRange(VInt(0x20));                                  // attributes (archive)
            fields.AddRange(BitConverter.GetBytes(Crc32(content)));       // data CRC32
            fields.AddRange(VInt(0));                                     // compression: version 0, method 0 (store)
            fields.AddRange(VInt(0));                                     // host OS: Windows
            fields.AddRange(VInt((ulong)nameBytes.Length));
            fields.AddRange(nameBytes);
            ms.Write(Header(type: 2, flags: 0x0002, fields: [.. fields], dataSize: (ulong)content.Length));
            ms.Write(content);
        }
        ms.Write(Header(type: 5, flags: 0, fields: VInt(0)));            // end of archive
        return ms.ToArray();
    }

    private static byte[] Header(ulong type, ulong flags, byte[] fields, ulong? dataSize = null)
    {
        var body = new List<byte>();
        body.AddRange(VInt(type));
        body.AddRange(VInt(flags));
        if (dataSize is { } size) body.AddRange(VInt(size));
        body.AddRange(fields);

        var withSize = new List<byte>(VInt((ulong)body.Count));
        withSize.AddRange(body);

        var result = new List<byte>(BitConverter.GetBytes(Crc32([.. withSize])));
        result.AddRange(withSize);
        return [.. result];
    }

    private static byte[] VInt(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            bytes.Add(b);
        } while (value != 0);
        return [.. bytes];
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
