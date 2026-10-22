using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ImageMagick;

namespace DropFlow.Engines.Images;

public enum MetadataGroup { File, Exif, Gps, Iptc, Xmp }

/// <summary>
/// One metadata field as shown in the editor.
/// Key is stable and machine-readable (EXIF/GPS: the ExifTag name e.g. "Artist", "GPSLatitude"; IPTC: IptcTag name;
/// XMP: "prefix:name" e.g. "dc:creator" or "xmp:CreatorTool"; File: "name","size","dimensions","format","modified").
/// Name is a human label (English; the UI may translate known keys). Value is display text.
/// </summary>
public sealed record MetadataField(MetadataGroup Group, string Key, string Name, string Value, bool Editable, bool Removable);

/// <summary>
/// Value null (or blank) = remove the field. Key "*" with Value null = remove the whole group
/// (Gps "*" removes every GPS tag; Exif "*" removes the EXIF profile incl. GPS and thumbnail).
/// </summary>
public sealed record MetadataChange(MetadataGroup Group, string Key, string? Value);

/// <summary>
/// Reads and edits EXIF, GPS, IPTC and XMP metadata.
/// <para>JPEG is edited <b>losslessly</b>: only the metadata segments (APP1 EXIF, APP1 XMP, APP13 Photoshop/IPTC) are rebuilt;
/// every other segment (JFIF, ICC profile, Adobe, quantisation/Huffman tables…) and the compressed image data are copied byte for
/// byte, so pixels cannot change. The EXIF Orientation tag is never dropped (the pixels are not rotated on this path): removing
/// the whole EXIF block keeps a minimal block holding only Orientation when it is not "normal".
/// Other formats (png, webp, tiff, heic, avif) go through Magick.NET profiles and are re-saved.</para>
/// <para>Text and Arabic: IPTC is written as UTF-8 (with the 1:90 marker) and XMP is UTF-8, so both hold any language.
/// EXIF "ASCII" text tags (Artist, Copyright, ImageDescription…) are written by Magick.NET as UTF-8 bytes: DropFlow and
/// Magick read them back exactly, but strict EXIF readers may show non-Latin text garbled. For Arabic, prefer the XMP dc:* and
/// IPTC fields, or the Windows EXIF fields XPTitle/XPComment/XPAuthor/XPKeywords/XPSubject and UserComment, which are UTF-16.</para>
/// </summary>
public static class ImageMetadata
{
    /// <summary>Fields that can be added when missing, per group (any listed or existing editable key can be set).</summary>
    public static readonly IReadOnlyDictionary<MetadataGroup, IReadOnlyList<string>> AddableKeys = new Dictionary<MetadataGroup, IReadOnlyList<string>>
    {
        [MetadataGroup.Exif] =
        [
            "Artist", "Copyright", "ImageDescription", "Make", "Model", "Software", "DateTime", "DateTimeOriginal", "DateTimeDigitized",
            "XPTitle", "XPSubject", "XPComment", "XPAuthor", "XPKeywords", "UserComment",
            "CameraOwnerName", "BodySerialNumber", "LensMake", "LensModel", "ImageUniqueID",
        ],
        [MetadataGroup.Iptc] =
        [
            "Title", "Headline", "Caption", "Keyword", "Byline", "BylineTitle", "Credit", "Source", "CopyrightNotice",
            "City", "SubLocation", "ProvinceState", "Country", "CaptionWriter", "SpecialInstructions",
        ],
        [MetadataGroup.Xmp] =
        [
            "dc:creator", "dc:title", "dc:description", "dc:rights", "dc:subject", "xmp:CreatorTool", "xmp:Rating", "xmp:Label",
            "photoshop:Headline", "photoshop:Credit", "photoshop:Source", "photoshop:City", "photoshop:State", "photoshop:Country",
        ],
    };

    // ---------------------------------------------------------------- JSON (the converter option "changes")

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record ChangeDto(string? Group, string? Key, string? Value);

    public static string ToJson(IReadOnlyList<MetadataChange> changes) =>
        JsonSerializer.Serialize(changes.Select(c => new ChangeDto(c.Group.ToString(), c.Key, c.Value)), Json);

    /// <summary>Parses the JSON written by <see cref="ToJson"/>. Empty text gives no changes.</summary>
    /// <exception cref="ArgumentException">The text is not a JSON array of {group,key,value} objects.</exception>
    public static IReadOnlyList<MetadataChange> ParseChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        List<ChangeDto?>? items;
        try { items = JsonSerializer.Deserialize<List<ChangeDto?>>(json, Json); }
        catch (JsonException ex) { throw new ArgumentException($"The metadata changes are not valid JSON: {ex.Message}", nameof(json), ex); }

        var result = new List<MetadataChange>();
        foreach (var item in items ?? [])
        {
            if (item is null) continue;
            if (!Enum.TryParse<MetadataGroup>(item.Group, ignoreCase: true, out var group) || !Enum.IsDefined(group) || int.TryParse(item.Group, out _))
                throw new ArgumentException($"Unknown metadata group '{item.Group}'.", nameof(json));
            if (string.IsNullOrWhiteSpace(item.Key)) throw new ArgumentException("A metadata change has no key.", nameof(json));
            result.Add(new MetadataChange(group, item.Key.Trim(), item.Value));
        }
        return result;
    }

    /// <summary>Removes EXIF (incl. GPS and thumbnail), IPTC and XMP. The ICC colour profile is kept so colours do not shift.</summary>
    public static IReadOnlyList<MetadataChange> RemoveAll() =>
    [
        new(MetadataGroup.Exif, "*", null),
        new(MetadataGroup.Gps, "*", null),
        new(MetadataGroup.Iptc, "*", null),
        new(MetadataGroup.Xmp, "*", null),
    ];

    // ---------------------------------------------------------------- Read

    /// <exception cref="InvalidDataException">The file is damaged or not an image.</exception>
    public static IReadOnlyList<MetadataField> Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The image does not exist.", path);
        var bytes = File.ReadAllBytes(path);

        Model model;
        string format;
        (int Width, int Height)? dimensions;
        if (IsJpeg(path, bytes))
        {
            var jpeg = JpegFile.Parse(bytes);
            model = Model.From(jpeg);
            dimensions = jpeg.Dimensions;
            format = "JPEG";
        }
        else
        {
            using var image = ReadImage(bytes);
            model = Model.From(image);
            dimensions = ((int)image.Width, (int)image.Height);
            format = image.Format.ToString().ToUpperInvariant();
        }

        var fields = new List<MetadataField>
        {
            FileField("name", "Name", info.Name),
            FileField("size", "Size", Size(info.Length)),
        };
        if (dimensions is { } d) fields.Add(FileField("dimensions", "Dimensions", $"{d.Width} × {d.Height}"));
        fields.Add(FileField("format", "Format", format));
        fields.Add(FileField("modified", "Modified", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

        if (model.Exif is { } exif)
        {
            var values = exif.Values.ToList();
            foreach (var v in values.Where(v => !IsGps(v.Tag.ToString())))
            {
                var key = v.Tag.ToString();
                fields.Add(new MetadataField(MetadataGroup.Exif, key, Humanize(key), ExifText.Display(v), ExifText.IsEditable(key), key != "Orientation"));
            }
            var gps = values.Where(v => IsGps(v.Tag.ToString())).ToList();
            if (ExifText.Position(exif) is { } position)
                fields.Add(new MetadataField(MetadataGroup.Gps, "position", "Position", position, false, false));
            foreach (var v in gps)
            {
                var key = v.Tag.ToString();
                fields.Add(new MetadataField(MetadataGroup.Gps, key, Humanize(key), ExifText.Display(v), false, true));
            }
        }

        if (model.Iptc is { } iptc)
            foreach (var (key, _, value, editable) in iptc.Fields())
                fields.Add(new MetadataField(MetadataGroup.Iptc, key, Humanize(key), value, editable, true));

        if (model.Xmp is { } xmp)
            foreach (var (key, value) in xmp.Fields())
                fields.Add(new MetadataField(MetadataGroup.Xmp, key, Humanize(key[(key.IndexOf(':') + 1)..]), value, true, true));

        return fields;

        static MetadataField FileField(string key, string name, string value) => new(MetadataGroup.File, key, name, value, false, false);
    }

    // ---------------------------------------------------------------- Apply

    /// <summary>
    /// Writes a copy of <paramref name="input"/> with <paramref name="changes"/> applied (in order) to <paramref name="output"/>.
    /// The input is never touched; the output is written to "output.part" first and never overwrites an existing file.
    /// </summary>
    /// <exception cref="ArgumentException">A change is not allowed (read-only field, unknown key, bad date…).</exception>
    /// <exception cref="InvalidDataException">The image is damaged, or a metadata block would exceed the 64 KB JPEG segment limit.</exception>
    /// <exception cref="IOException">The output already exists.</exception>
    public static void Apply(string input, IReadOnlyList<MetadataChange> changes, string output)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (System.IO.File.Exists(output)) throw new IOException($"'{Path.GetFileName(output)}' already exists; DropFlow never overwrites files.");
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The output must be a different file from the input.");

        var bytes = System.IO.File.ReadAllBytes(input);
        if (IsJpeg(input, bytes))
        {
            var jpeg = JpegFile.Parse(bytes);
            var model = Model.From(jpeg);
            model.ApplyAll(changes);
            model.WriteTo(jpeg);
            var result = jpeg.ToBytes();
            JpegFile.Parse(result); // sanity: what we write must read back
            ImageIo.SaveBytes(result, output);
            return;
        }

        var ext = DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(input);
        if (!MetadataFormats.Supported.Contains(ext)) throw new ArgumentException($"Editing metadata is not supported for .{ext} files.");
        using var image = ReadImage(bytes);
        var m = Model.From(image);
        m.ApplyAll(changes);
        if (ext == "tiff" && m.ExifDirty && m.Exif is not null)
            throw new ArgumentException("DropFlow cannot write EXIF fields into TIFF files; use the XMP or IPTC fields instead.");
        m.WriteTo(image);
        ImageIo.Save(image, output, ext, 92);
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsJpeg(string path, byte[] bytes) =>
        JpegFile.LooksLikeJpeg(bytes) || DropFlow.Core.Catalog.ToolCatalog.ExtensionOf(path) == "jpg";

    private static MagickImage ReadImage(byte[] bytes)
    {
        try { return new MagickImage(bytes); } // no auto-orient: pixels and the Orientation tag stay as they are
        catch (MagickException ex) { throw new InvalidDataException($"The image could not be read ({ex.Message}).", ex); }
    }

    internal static bool IsGps(string tag) => tag.StartsWith("GPS", StringComparison.Ordinal);

    private static string Size(long length) => length switch
    {
        < 1024 => $"{length:N0} bytes",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{length / 1024.0:0.#} KB ({length:N0} bytes)"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{length / 1024.0 / 1024.0:0.##} MB ({length:N0} bytes)"),
    };

    private static readonly Regex WordBreak = new("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    internal static string Humanize(string key) => WordBreak.Replace(key, " ");

    /// <summary>The metadata of one file while it is being edited.</summary>
    private sealed class Model
    {
        public ExifProfile? Exif;
        public bool ExifDamaged, ExifDirty, DropExtras;
        public IptcData? Iptc;
        public bool IptcDirty, IptcDropAll;
        public byte[]? Photoshop;
        public XmpPacket? Xmp;
        public bool XmpDamaged, XmpDirty;

        public static Model From(JpegFile jpeg)
        {
            var m = new Model();
            if (jpeg.Segments.FirstOrDefault(s => s.IsExif) is { } exif) m.LoadExif(exif.Payload!);
            if (jpeg.Segments.FirstOrDefault(s => s.IsXmp) is { } xmp) m.LoadXmp(xmp.Body(JpegSegment.XmpHeader));
            var photoshop = jpeg.Segments.Where(s => s.IsPhotoshop).SelectMany(s => s.Body(JpegSegment.PhotoshopHeader)).ToArray();
            if (photoshop.Length > 0)
            {
                m.Photoshop = photoshop;
                if (PhotoshopResources.Parse(photoshop).Get(PhotoshopResources.IptcId) is { } iptc) m.Iptc = IptcData.Parse(iptc);
            }
            return m;
        }

        public static Model From(MagickImage image)
        {
            var m = new Model();
            if (image.GetProfile("exif")?.ToByteArray() is { Length: > 0 } exif) m.LoadExif(exif);
            if (image.GetProfile("xmp")?.ToByteArray() is { Length: > 0 } xmp) m.LoadXmp(xmp);
            m.Photoshop = image.GetProfile("8bim")?.ToByteArray();
            var iptc = image.GetProfile("iptc")?.ToByteArray();
            if (iptc is not { Length: > 0 } && m.Photoshop is { } ps) iptc = PhotoshopResources.Parse(ps).Get(PhotoshopResources.IptcId);
            if (iptc is { Length: > 0 }) m.Iptc = IptcData.Parse(iptc);
            return m;
        }

        private void LoadExif(byte[] data)
        {
            try
            {
                var profile = new ExifProfile(data);
                _ = profile.Values.Count(); // parse now so damage is found here
                Exif = profile;
            }
            catch (Exception ex) when (ex is MagickException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                ExifDamaged = true;
            }
        }

        private void LoadXmp(byte[] data)
        {
            try { Xmp = XmpPacket.Parse(data); }
            catch (InvalidDataException) { XmpDamaged = true; }
        }

        public void ApplyAll(IReadOnlyList<MetadataChange> changes)
        {
            foreach (var change in changes) Apply(change);
            if (Exif is not null && !Exif.Values.Any()) Exif = null;
            if (Iptc is not null && Iptc.IsEmpty) Iptc = null;
        }

        private void Apply(MetadataChange change)
        {
            var key = change.Key?.Trim() ?? "";
            if (key.Length == 0) throw new ArgumentException("A metadata change has no key.");
            var value = string.IsNullOrWhiteSpace(change.Value) ? null : change.Value.Trim();
            var whole = key == "*";
            if (whole && value is not null) throw new ArgumentException("A whole group can only be removed (\"*\" needs an empty value).");

            switch (change.Group)
            {
                case MetadataGroup.File:
                    throw new ArgumentException($"The file field '{key}' is read-only.");

                case MetadataGroup.Exif when whole:
                    var orientation = Exif?.GetValue(ExifTag.Orientation)?.Value;
                    Exif = null;
                    if (orientation is > 1)
                    {
                        Exif = new ExifProfile();
                        Exif.SetValue(ExifTag.Orientation, orientation.Value);
                    }
                    ExifDamaged = false;
                    ExifDirty = DropExtras = true;
                    break;

                case MetadataGroup.Exif:
                    if (IsGps(key)) throw new ArgumentException($"{key} is a GPS field; change it in the GPS group.");
                    if (key == "Orientation") throw new ArgumentException("Orientation is kept so the picture does not turn; it cannot be changed here.");
                    RequireUndamagedExif();
                    if (value is null)
                    {
                        if (Exif?.Values.FirstOrDefault(v => v.Tag.ToString() == key) is { } existing)
                        {
                            Exif.RemoveValue(existing.Tag);
                            ExifDirty = true;
                        }
                        break;
                    }
                    Exif ??= new ExifProfile();
                    ExifText.Set(Exif, key, value);
                    ExifDirty = true;
                    break;

                case MetadataGroup.Gps:
                    if (value is not null) throw new ArgumentException("GPS fields are read-only; they can only be removed.");
                    if (key == "position") throw new ArgumentException("The position is computed from the GPS fields; remove the GPS group (\"*\") instead.");
                    if (!whole && !IsGps(key)) throw new ArgumentException($"'{key}' is not a GPS field.");
                    RequireUndamagedExif();
                    if (Exif is not null)
                        foreach (var v in Exif.Values.Where(v => whole ? IsGps(v.Tag.ToString()) : v.Tag.ToString() == key).ToList())
                        {
                            Exif.RemoveValue(v.Tag);
                            ExifDirty = true;
                        }
                    // XMP can carry the location too (exif:GPSLatitude…): removing the GPS group removes it there as well.
                    if (whole && Xmp is not null && Xmp.RemoveWhere(n => n.NamespaceName == XmpPacket.KnownPrefixes["exif"] && IsGps(n.LocalName)))
                        XmpDirty = true;
                    break;

                case MetadataGroup.Iptc when whole:
                    Iptc = null;
                    IptcDirty = IptcDropAll = true;
                    break;

                case MetadataGroup.Iptc:
                    var number = IptcData.NumberOf(key) ?? throw new ArgumentException($"'{key}' is not an IPTC field.");
                    if (value is null)
                    {
                        if (Iptc?.Remove(number) == true) IptcDirty = true;
                        break;
                    }
                    (Iptc ??= new IptcData()).Set(number, value);
                    IptcDirty = true;
                    break;

                case MetadataGroup.Xmp when whole:
                    Xmp = null;
                    XmpDamaged = false;
                    XmpDirty = true;
                    break;

                case MetadataGroup.Xmp:
                    if (XmpDamaged) throw new InvalidDataException("The XMP block in this file is damaged; remove the whole XMP group first.");
                    if (value is null)
                    {
                        if (Xmp?.Remove(key) == true) XmpDirty = true;
                        break;
                    }
                    (Xmp ??= XmpPacket.CreateEmpty()).Set(key, value);
                    XmpDirty = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown metadata group '{change.Group}'.");
            }
        }

        private void RequireUndamagedExif()
        {
            if (ExifDamaged) throw new InvalidDataException("The EXIF block in this file is damaged; remove the whole EXIF group first.");
        }

        private byte[]? ExifBytes()
        {
            if (Exif?.ToByteArray() is not { Length: > 0 } data) return null;
            return data.AsSpan().StartsWith(JpegSegment.ExifHeader) ? data : [.. JpegSegment.ExifHeader, .. data];
        }

        private byte[]? IptcResources()
        {
            var resources = IptcDropAll || Photoshop is null ? new PhotoshopResources() : PhotoshopResources.Parse(Photoshop);
            resources.Set(PhotoshopResources.IptcId, Iptc?.ToBytes());
            return resources.IsEmpty ? null : resources.ToBytes();
        }

        public void WriteTo(JpegFile jpeg)
        {
            if (ExifDirty)
            {
                jpeg.Replace(s => s.IsExif, JpegSegment.App1, ExifBytes(), []);
                if (DropExtras)
                {
                    // MPF secondary pictures (phone previews, gain maps) sit after the main picture and carry their own EXIF.
                    jpeg.Segments.RemoveAll(s => s.IsMpf);
                    jpeg.Trailer = [];
                }
            }
            if (XmpDirty)
            {
                jpeg.Replace(s => s.IsXmp, JpegSegment.App1, Xmp is null ? null : [.. JpegSegment.XmpHeader, .. Xmp.ToBytes()], [s => s.IsExif]);
                if (Xmp is null) jpeg.Segments.RemoveAll(s => s.IsXmpExtension);
            }
            if (IptcDirty)
            {
                var resources = IptcResources();
                jpeg.Replace(s => s.IsPhotoshop, JpegSegment.App13, resources is null ? null : [.. JpegSegment.PhotoshopHeader, .. resources],
                    [s => s.IsExif, s => s.IsXmp]);
            }
        }

        public void WriteTo(MagickImage image)
        {
            if (ExifDirty)
            {
                if (Exif is null) image.RemoveProfile("exif");
                else image.SetProfile(Exif);
                // Magick writes image.Orientation into the EXIF block, so keep the two in step.
                image.Orientation = Exif?.GetValue(ExifTag.Orientation)?.Value is { } o ? (ImageMagick.Orientation)o : ImageMagick.Orientation.Undefined;
                // Some readers (TIFF) turn EXIF into "exif:*" attributes that a writer may put back: drop the removed ones too.
                var kept = Exif?.Values.Select(v => "exif:" + v.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                foreach (var name in image.AttributeNames.Where(n => n.StartsWith("exif:", StringComparison.OrdinalIgnoreCase) && !kept.Contains(n)).ToList())
                    image.RemoveAttribute(name);
            }
            if (XmpDirty)
            {
                if (Xmp is null) image.RemoveProfile("xmp");
                else image.SetProfile(new XmpProfile(Xmp.ToBytes()));
            }
            if (IptcDirty)
            {
                if (Iptc is null) image.RemoveProfile("iptc");
                else image.SetProfile(new IptcProfile(Iptc.ToBytes()));
                if (Photoshop is not null)
                {
                    var resources = IptcResources();
                    if (resources is null) image.RemoveProfile("8bim");
                    else image.SetProfile(new ImageProfile("8bim", resources));
                }
            }
        }
    }
}

/// <summary>EXIF value formatting and the text tags DropFlow can write.</summary>
internal static class ExifText
{
    private static readonly Dictionary<string, ExifTag<string>> StringTags = typeof(ExifTag)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(ExifTag<string>) && !ImageMetadata.IsGps(p.Name))
        .ToDictionary(p => p.Name, p => (ExifTag<string>)p.GetValue(null)!, StringComparer.Ordinal);

    /// <summary>Windows "XP" tags: UTF-16LE text with a terminating null.</summary>
    private static readonly Dictionary<string, ExifTag<byte[]>> XpTags = new(StringComparer.Ordinal)
    {
        ["XPTitle"] = ExifTag.XPTitle,
        ["XPComment"] = ExifTag.XPComment,
        ["XPAuthor"] = ExifTag.XPAuthor,
        ["XPKeywords"] = ExifTag.XPKeywords,
        ["XPSubject"] = ExifTag.XPSubject,
    };

    private static readonly HashSet<string> DateTags = ["DateTime", "DateTimeOriginal", "DateTimeDigitized"];

    private static readonly byte[] AsciiCode = "ASCII\0\0\0"u8.ToArray();
    private static readonly byte[] UnicodeCode = "UNICODE\0"u8.ToArray();

    public static bool IsEditable(string key) =>
        key != "Orientation" && !ImageMetadata.IsGps(key) && (StringTags.ContainsKey(key) || XpTags.ContainsKey(key) || key == "UserComment");

    /// <exception cref="ArgumentException">The tag is not a text tag, or a date is not a date.</exception>
    public static void Set(ExifProfile exif, string key, string value)
    {
        if (XpTags.TryGetValue(key, out var xp))
        {
            exif.SetValue(xp, Encoding.Unicode.GetBytes(value + "\0"));
            return;
        }
        if (key == "UserComment")
        {
            byte[] encoded = value.All(c => c < 128)
                ? [.. AsciiCode, .. Encoding.ASCII.GetBytes(value)]
                : [.. UnicodeCode, .. Encoding.Unicode.GetBytes(value)];
            exif.SetValue(ExifTag.UserComment, encoded);
            return;
        }
        if (StringTags.TryGetValue(key, out var tag))
        {
            exif.SetValue(tag, DateTags.Contains(key) ? NormalizeDate(key, value) : value);
            return;
        }
        if (exif.Values.Any(v => v.Tag.ToString() == key))
            throw new ArgumentException($"The EXIF field {key} is not text; it can only be removed.");
        throw new ArgumentException($"'{key}' is not an EXIF text field DropFlow can write.");
    }

    private static readonly string[] DateFormats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm",
        "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd", "yyyy:MM:dd",
    ];

    private static string NormalizeDate(string key, string value)
    {
        if (DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
        throw new ArgumentException($"'{value}' is not a date for {key} (use YYYY-MM-DD HH:MM:SS).");
    }

    public static string Display(IExifValue value)
    {
        var key = value.Tag.ToString();
        return value.GetValue() switch
        {
            null => "",
            string s => s.TrimEnd('\0'),
            byte[] b when XpTags.ContainsKey(key) => Encoding.Unicode.GetString(b).TrimEnd('\0'),
            byte[] b when key == "UserComment" => UserComment(b),
            byte[] b => b.Length > 0 && b.All(x => x is >= 0x20 and < 0x7F or 0) ? Encoding.ASCII.GetString(b).TrimEnd('\0') : $"{b.Length:N0} bytes",
            Rational[] r when key is "GPSLatitude" or "GPSLongitude" or "GPSDestLatitude" or "GPSDestLongitude" && r.Length == 3 =>
                string.Create(CultureInfo.InvariantCulture, $"{r[0].ToDouble():0.####}° {r[1].ToDouble():0.####}' {r[2].ToDouble():0.##}\""),
            Rational[] r when key == "GPSTimestamp" && r.Length == 3 =>
                string.Create(CultureInfo.InvariantCulture, $"{r[0].ToDouble():00}:{r[1].ToDouble():00}:{r[2].ToDouble():00.##}"),
            Rational r => Format(r),
            SignedRational r => Format(r),
            Array a => string.Join(", ", a.Cast<object?>().Select(Item)),
            var other => Item(other),
        };
    }

    private static string Item(object? item) => item switch
    {
        null => "",
        Rational r => Format(r),
        SignedRational r => Format(r),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => item.ToString() ?? "",
    };

    private static string Format(Rational r)
    {
        if (r.Denominator == 0) return "0";
        if (r.Numerator % r.Denominator == 0) return (r.Numerator / r.Denominator).ToString(CultureInfo.InvariantCulture);
        if (r.Numerator == 1) return $"1/{r.Denominator}";
        return r.ToDouble().ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Format(SignedRational r)
    {
        if (r.Denominator == 0) return "0";
        if (r.Numerator % r.Denominator == 0) return (r.Numerator / r.Denominator).ToString(CultureInfo.InvariantCulture);
        return r.ToDouble().ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string UserComment(byte[] b)
    {
        if (b.Length < 8) return Encoding.UTF8.GetString(b).TrimEnd('\0', ' ');
        var body = b[8..];
        if (b.AsSpan(0, 8).SequenceEqual(UnicodeCode)) return Encoding.Unicode.GetString(body).TrimEnd('\0', ' ');
        if (b.AsSpan(0, 8).SequenceEqual(AsciiCode)) return Encoding.ASCII.GetString(body).TrimEnd('\0', ' ');
        if (b.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0) return Encoding.UTF8.GetString(body).TrimEnd('\0', ' ');
        return $"{b.Length:N0} bytes";
    }

    /// <summary>"lat, long" in decimal degrees (south and west negative), or null without both coordinates.</summary>
    public static string? Position(ExifProfile exif)
    {
        var lat = Degrees(exif.GetValue(ExifTag.GPSLatitude)?.Value, exif.GetValue(ExifTag.GPSLatitudeRef)?.Value, "S");
        var lon = Degrees(exif.GetValue(ExifTag.GPSLongitude)?.Value, exif.GetValue(ExifTag.GPSLongitudeRef)?.Value, "W");
        if (lat is null || lon is null) return null;
        return string.Create(CultureInfo.InvariantCulture, $"{lat:0.0000}, {lon:0.0000}");
    }

    private static double? Degrees(Rational[]? parts, string? reference, string negative)
    {
        if (parts is not { Length: > 0 } || parts.Any(p => p.Denominator == 0)) return null;
        var value = parts[0].ToDouble() + (parts.Length > 1 ? parts[1].ToDouble() / 60 : 0) + (parts.Length > 2 ? parts[2].ToDouble() / 3600 : 0);
        return string.Equals(reference?.Trim(), negative, StringComparison.OrdinalIgnoreCase) ? -value : value;
    }
}
