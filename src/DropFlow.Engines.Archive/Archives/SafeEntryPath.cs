using System.Text;

namespace DropFlow.Engines.Archive.Archives;

public enum EntryPathStatus
{
    /// <summary>The name is usable as it is.</summary>
    Ok,

    /// <summary>The name was usable after neutralising Windows-hostile parts (device names, ':' streams, bidi overrides...).</summary>
    Sanitized,

    /// <summary>The name tries to escape the destination (zip-slip) and must not be written.</summary>
    Unsafe,

    /// <summary>The name is empty ("./"); nothing to write.</summary>
    Skip,
}

/// <param name="RelativePath">Safe relative path with '/' separators (empty when not usable).</param>
public readonly record struct EntryPathResult(EntryPathStatus Status, string RelativePath, string? Reason);

/// <summary>
/// Turns an untrusted archive entry name into a safe relative path, or rejects it. This is the zip-slip defence.
/// Rejected (<see cref="EntryPathStatus.Unsafe"/>): NUL bytes, absolute and UNC paths, drive letters, any <c>..</c>
/// segment, and segments made only of dots/spaces (Windows folds those into <c>..</c>).
/// Neutralised (<see cref="EntryPathStatus.Sanitized"/>): reserved device names (<c>CON</c>, <c>NUL.txt</c>, <c>COM1</c>...),
/// NTFS stream syntax (<c>a.txt:stream</c>), characters Windows forbids, trailing dots/spaces, over-long segments and
/// right-to-left override characters that fake file extensions.
/// </summary>
public static class SafeEntryPath
{
    private const int MaxSegmentLength = 200;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    public static EntryPathResult Evaluate(string? rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return new(EntryPathStatus.Skip, "", null);
        if (rawName.Contains('\0')) return Unsafe("contains a NUL character");

        // Windows treats both separators alike, so an entry written with '\' is a path too.
        var name = rawName.Replace('\\', '/');
        if (name[0] == '/') return Unsafe("absolute or network path");
        if (name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0])) return Unsafe("drive-letter path");

        var segments = new List<string>();
        var modified = false;
        foreach (var raw in name.Split('/'))
        {
            if (raw.Length == 0 || raw == ".") continue;
            if (raw == "..") return Unsafe("contains a '..' parent-directory reference");
            if (raw.TrimEnd(' ', '.').Length == 0) return Unsafe("contains a segment made only of dots or spaces");

            var segment = CleanSegment(raw, ref modified);
            segments.Add(segment);
        }

        if (segments.Count == 0) return new(EntryPathStatus.Skip, "", null);
        return new(modified ? EntryPathStatus.Sanitized : EntryPathStatus.Ok, string.Join('/', segments), null);
    }

    /// <summary>
    /// Resolves <paramref name="relative"/> under <paramref name="rootWithSeparator"/> and proves the result stays inside it.
    /// Defence in depth: <see cref="Evaluate"/> already removed every escape, this catches anything that slipped through.
    /// </summary>
    public static string ResolveUnder(string rootWithSeparator, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(rootWithSeparator, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new ArchiveException(ArchiveProblem.UnsafePath, $"Entry '{relative}' would be written outside the destination folder.");
        return full;
    }

    /// <summary>Full path of a folder, guaranteed to end with a directory separator.</summary>
    public static string WithTrailingSeparator(string folder)
    {
        var full = Path.GetFullPath(folder);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>Makes ONE file name valid on Windows (forbidden characters, device names, trailing dots). Used by batch rename.</summary>
    internal static string CleanFileName(string name)
    {
        var modified = false;
        return CleanSegment(name, ref modified);
    }

    private static EntryPathResult Unsafe(string reason) => new(EntryPathStatus.Unsafe, "", reason);

    private static string CleanSegment(string segment, ref bool modified)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (IsForbidden(c)) { sb.Append('_'); modified = true; }
            else sb.Append(c);
        }

        // Windows silently drops trailing dots and spaces, which would make two different entries collide.
        var trimmed = sb.ToString().TrimEnd(' ', '.');
        if (trimmed.Length != sb.Length) modified = true;

        // "CON", "con.txt", "NUL.tar.gz" all name a device: only the part before the first dot counts.
        var dot = trimmed.IndexOf('.');
        var stem = (dot < 0 ? trimmed : trimmed[..dot]).TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            trimmed = "_" + trimmed;
            modified = true;
        }

        if (trimmed.Length > MaxSegmentLength)
        {
            var ext = Path.GetExtension(trimmed);
            if (ext.Length > 20) ext = "";
            trimmed = trimmed[..(MaxSegmentLength - ext.Length)] + ext;
            if (char.IsHighSurrogate(trimmed[MaxSegmentLength - ext.Length - 1])) trimmed = trimmed.Remove(MaxSegmentLength - ext.Length - 1, 1);
            modified = true;
        }

        return trimmed;
    }

    private static bool IsForbidden(char c) =>
        c < ' ' || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/'
        || c is >= '‪' and <= '‮'      // bidi embedding/override: "invoice‮fdp.exe" looks like "invoiceexe.pdf"
        || c is >= '⁦' and <= '⁩';     // bidi isolates
}
