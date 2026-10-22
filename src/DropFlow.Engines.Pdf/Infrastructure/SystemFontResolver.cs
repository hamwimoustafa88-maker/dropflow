using System.Text;
using PdfSharp.Fonts;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>
/// PDFsharp font source for macOS and Linux, where PDFsharp cannot read the installed fonts by itself (on Windows it uses the Windows
/// fonts). Scans the usual font folders for TrueType files, reads each file's family and style from its <c>name</c> table, and answers
/// requests for Windows families with a close installed font: Arial, Segoe UI, Tahoma and Verdana become Arial (macOS ships it), then
/// DejaVu Sans, Noto Sans or Liberation Sans. DejaVu Sans and Arial both carry Arabic, which the stamp and test tools draw.
/// Bold and italic use the real face when one is installed and are simulated otherwise.
/// </summary>
internal sealed class SystemFontResolver : IFontResolver
{
    private static readonly string[] SansFallbacks = ["Arial", "DejaVu Sans", "Noto Sans", "Liberation Sans", "Helvetica", "FreeSans"];
    private static readonly string[] MonoFallbacks = ["Courier New", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Menlo", "FreeMono"];
    private static readonly string[] SerifFallbacks = ["Times New Roman", "DejaVu Serif", "Liberation Serif", "Noto Serif", "Times", "FreeSerif"];

    private readonly Dictionary<string, List<Face>> _families;

    internal sealed record Face(string Path, string Family, bool Bold, bool Italic);

    public SystemFontResolver(IEnumerable<string>? folders = null)
    {
        _families = new Dictionary<string, List<Face>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in (folders ?? DefaultFolders()).Where(Directory.Exists).SelectMany(SafeFiles).Distinct())
        {
            if (Read(file) is not { } face) continue;
            if (!_families.TryGetValue(face.Family, out var list)) _families[face.Family] = list = [];
            list.Add(face);
        }
    }

    /// <summary>Number of usable TrueType families found (0 means PDFsharp cannot draw text on this machine).</summary>
    public int FamilyCount => _families.Count;

    public static IEnumerable<string> DefaultFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return "/System/Library/Fonts";
        yield return "/System/Library/Fonts/Supplemental";
        yield return "/Library/Fonts";
        if (home.Length > 0) yield return Path.Combine(home, "Library", "Fonts");
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        if (home.Length > 0)
        {
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        foreach (var family in Candidates(familyName))
        {
            if (!_families.TryGetValue(family, out var faces)) continue;
            var exact = faces.FirstOrDefault(f => f.Bold == bold && f.Italic == italic);
            if (exact is not null) return new FontResolverInfo(exact.Path);
            var regular = faces.FirstOrDefault(f => !f.Bold && !f.Italic) ?? faces[0];
            return new FontResolverInfo(regular.Path, bold && !regular.Bold, italic && !regular.Italic);
        }
        return null;
    }

    public byte[]? GetFont(string faceName) => File.Exists(faceName) ? File.ReadAllBytes(faceName) : null;

    /// <summary>The requested family first, then look-alikes, then any installed family (so text is never silently dropped).</summary>
    private IEnumerable<string> Candidates(string familyName)
    {
        yield return familyName;
        var lower = familyName.ToLowerInvariant();
        var fallbacks = lower.Contains("mono") || lower.Contains("courier") || lower.Contains("consolas")
            ? MonoFallbacks
            : lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("georgia")
                ? SerifFallbacks
                : SansFallbacks;
        foreach (var f in fallbacks) yield return f;
        foreach (var f in SansFallbacks) yield return f;
        foreach (var f in _families.Keys.Order(StringComparer.OrdinalIgnoreCase)) yield return f;
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.ttf", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Family and style of a TrueType (glyf) font from its <c>name</c> table; null for anything PDFsharp cannot embed.</summary>
    internal static Face? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            var version = BigEndian.U32(reader);
            if (version != 0x00010000 && version != 0x74727565) return null;   // 1.0 or 'true'; 'OTTO' (CFF) and collections are skipped
            int tables = BigEndian.U16(reader);
            reader.ReadBytes(6);
            long nameOffset = -1;
            for (var i = 0; i < tables; i++)
            {
                var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
                BigEndian.U32(reader);
                var offset = BigEndian.U32(reader);
                BigEndian.U32(reader);
                if (tag == "name") nameOffset = offset;
            }
            if (nameOffset < 0) return null;

            stream.Position = nameOffset;
            BigEndian.U16(reader);
            int count = BigEndian.U16(reader);
            int stringOffset = BigEndian.U16(reader);
            var names = new Dictionary<int, (int Rank, string Text)>();
            var records = new List<(int Platform, int Encoding, int Language, int Id, int Length, int Offset)>();
            for (var i = 0; i < count; i++)
                records.Add((BigEndian.U16(reader), BigEndian.U16(reader), BigEndian.U16(reader), BigEndian.U16(reader), BigEndian.U16(reader), BigEndian.U16(reader)));

            foreach (var r in records.Where(r => r.Id is 1 or 2 or 16 or 17))
            {
                // Prefer Windows English (3/1/0x409), then any Windows Unicode, then Macintosh Roman English.
                var rank = r.Platform == 3 && r.Language == 0x409 ? 0 : r.Platform == 3 ? 1 : r.Platform == 1 && r.Language == 0 ? 2 : 9;
                if (rank == 9 || (names.TryGetValue(r.Id, out var have) && have.Rank <= rank)) continue;
                stream.Position = nameOffset + stringOffset + r.Offset;
                var bytes = reader.ReadBytes(r.Length);
                var text = r.Platform == 3 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
                names[r.Id] = (rank, text.Trim());
            }

            var family = names.TryGetValue(16, out var typo) ? typo.Text : names.TryGetValue(1, out var fam) ? fam.Text : null;
            var style = names.TryGetValue(17, out var typoStyle) ? typoStyle.Text : names.TryGetValue(2, out var sub) ? sub.Text : "Regular";
            if (string.IsNullOrWhiteSpace(family)) return null;
            var bold = style.Contains("Bold", StringComparison.OrdinalIgnoreCase) || style.Contains("Black", StringComparison.OrdinalIgnoreCase);
            var italic = style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            // Light/condensed/semibold faces are not a plain "regular": keep them out so a request for Arial never gets Arial Narrow.
            if (!bold && !italic && !style.Equals("Regular", StringComparison.OrdinalIgnoreCase) && !style.Equals("Book", StringComparison.OrdinalIgnoreCase)
                && !style.Equals("Normal", StringComparison.OrdinalIgnoreCase) && !style.Equals("Roman", StringComparison.OrdinalIgnoreCase))
                return null;
            if (bold && !(style.StartsWith("Bold", StringComparison.OrdinalIgnoreCase))) return null;   // e.g. "SemiBold", "ExtraBold"
            return new Face(path, family, bold, italic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    private static class BigEndian
    {
        public static ushort U16(BinaryReader r) { var b = r.ReadBytes(2); if (b.Length < 2) throw new EndOfStreamException(); return (ushort)(b[0] << 8 | b[1]); }
        public static uint U32(BinaryReader r) { var b = r.ReadBytes(4); if (b.Length < 4) throw new EndOfStreamException(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    }
}
