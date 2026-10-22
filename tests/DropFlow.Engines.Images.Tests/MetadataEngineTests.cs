using System.Text;
using System.Xml.Linq;
using DropFlow.Core.Operations;
using DropFlow.Engines.Images;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Images.Tests;

/// <summary>
/// The metadata engine behind the visual metadata editor. Two independent readers check every output: Magick.NET, and a small
/// JPEG segment walker written here (not the engine's parser).
/// </summary>
public class MetadataEngineTests
{
    private static readonly IProgress<JobProgress> NoProgress = new Progress<JobProgress>();
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private const string Arabic = "مصطفى الحموي — رحلة إلى بيروت";

    // ---------------------------------------------------------------- samples

    private static byte[] Iim(params (byte Number, string Value)[] sets)
    {
        var bytes = new List<byte> { 0x1C, 1, 90, 0, 3, 0x1B, 0x25, 0x47, 0x1C, 2, 0, 0, 2, 0, 4 };
        foreach (var (number, value) in sets)
        {
            var raw = Encoding.UTF8.GetBytes(value);
            bytes.AddRange([0x1C, 2, number, (byte)(raw.Length >> 8), (byte)raw.Length]);
            bytes.AddRange(raw);
        }
        return [.. bytes];
    }

    private const string XmpText =
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"" +
        " xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmp:CreatorTool=\"Camera App 2\">" +
        "<dc:creator><rdf:Seq><rdf:li>Old Author</rdf:li></rdf:Seq></dc:creator>" +
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Old title</rdf:li></rdf:Alt></dc:title>" +
        "<exif:GPSLatitude>33,53.628N</exif:GPSLatitude>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>A noisy JPEG with EXIF (incl. GPS), IPTC, XMP and an ICC profile.</summary>
    private static string RichJpeg(string path, ushort orientation = 1, bool metadata = true)
    {
        using var img = new MagickImage(MagickColors.Gray, 120, 80);
        img.AddNoise(NoiseType.Gaussian);
        img.Quality = 90;
        img.SetProfile(ColorProfiles.SRGB);
        if (metadata)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Original Artist");
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.Model, "X100");
            exif.SetValue(ExifTag.DateTimeOriginal, "2024:05:01 10:20:30");
            exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(33, 1), new Rational(53, 1), new Rational(3768, 100)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "E");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(35, 1), new Rational(30, 1), new Rational(648, 100)]);
            img.SetProfile(exif);
            img.SetProfile(new IptcProfile(Iim((120, "Old caption"), (25, "beach"), (25, "sun"), (80, "Old Byline"))));
            img.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes(XmpText)));
        }
        if (orientation != 1)
        {
            // Magick writes image.Orientation into the EXIF Orientation tag, but only when that tag exists.
            var exif = img.GetExifProfile() ?? new ExifProfile();
            exif.SetValue(ExifTag.Orientation, orientation);
            img.SetProfile(exif);
            img.Orientation = (Orientation)orientation;
        }
        img.Write(path);
        return path;
    }

    // ---------------------------------------------------------------- an independent JPEG reader (test-owned)

    private static List<(byte Marker, byte[] Payload)> Segments(byte[] data)
    {
        var list = new List<(byte, byte[])>();
        var pos = 2;
        while (data[pos + 1] != 0xDA)
        {
            var length = (data[pos + 2] << 8) | data[pos + 3];
            list.Add((data[pos + 1], data[(pos + 4)..(pos + 2 + length)]));
            pos += 2 + length;
        }
        return list;
    }

    /// <summary>The compressed image: from the first SOS marker to the last EOI marker, inclusive.</summary>
    private static byte[] Scan(byte[] data)
    {
        var pos = 2;
        while (data[pos + 1] != 0xDA) pos += 2 + ((data[pos + 2] << 8) | data[pos + 3]);
        var end = data.Length - 2;
        while (!(data[end] == 0xFF && data[end + 1] == 0xD9)) end--;
        return data[pos..(end + 2)];
    }

    private static bool Starts(byte[] payload, string header) => payload.AsSpan().StartsWith(Encoding.ASCII.GetBytes(header));

    private static void AssertPixelsAndScanIdentical(string input, string output)
    {
        var a = File.ReadAllBytes(input);
        var b = File.ReadAllBytes(output);
        Assert.Equal(Scan(a), Scan(b));
        using var x = new MagickImage(input);
        using var y = new MagickImage(output);
        Assert.Equal(0, x.Compare(y, ErrorMetric.Absolute));

        // Every non-metadata segment (JFIF, ICC, tables, frame header) is byte-identical and in the same order.
        static bool Kept((byte Marker, byte[] Payload) s) =>
            !(s.Marker == 0xE1 && (Starts(s.Payload, "Exif\0\0") || Starts(s.Payload, "http://ns.adobe.com/xap/1.0/\0")))
            && !(s.Marker == 0xED && Starts(s.Payload, "Photoshop 3.0\0"));
        var before = Segments(a).Where(Kept).ToList();
        var after = Segments(b).Where(Kept).ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Marker, after[i].Marker);
            Assert.Equal(before[i].Payload, after[i].Payload);
        }
    }

    private static MetadataField Field(IReadOnlyList<MetadataField> fields, MetadataGroup group, string key) =>
        Assert.Single(fields, f => f.Group == group && f.Key == key);

    private static bool Has(IReadOnlyList<MetadataField> fields, MetadataGroup group, string key) =>
        fields.Any(f => f.Group == group && f.Key == key);

    private static string? XmpValue(MagickImage image, XName name)
    {
        var property = image.GetXmpProfile()?.ToXDocument()?.Descendants(name).FirstOrDefault();
        return property?.Descendants(Rdf + "li").FirstOrDefault()?.Value ?? property?.Value;
    }

    private static string Edit(TempDir dir, string input, params MetadataChange[] changes)
    {
        var output = dir.Combine("out-" + Guid.NewGuid().ToString("N")[..6] + Path.GetExtension(input));
        ImageMetadata.Apply(input, changes, output);
        return output;
    }

    // ---------------------------------------------------------------- Read

    [Fact]
    public void Read_lists_file_exif_gps_iptc_and_xmp_fields()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));

        var fields = ImageMetadata.Read(jpg);

        Assert.Equal("p.jpg", Field(fields, MetadataGroup.File, "name").Value);
        Assert.Equal("120 × 80", Field(fields, MetadataGroup.File, "dimensions").Value);
        Assert.Equal("JPEG", Field(fields, MetadataGroup.File, "format").Value);
        Assert.True(Has(fields, MetadataGroup.File, "size") && Has(fields, MetadataGroup.File, "modified"));
        Assert.All(fields.Where(f => f.Group == MetadataGroup.File), f => Assert.False(f.Editable || f.Removable));

        var artist = Field(fields, MetadataGroup.Exif, "Artist");
        Assert.Equal("Original Artist", artist.Value);
        Assert.True(artist.Editable && artist.Removable);
        Assert.Equal("2024:05:01 10:20:30", Field(fields, MetadataGroup.Exif, "DateTimeOriginal").Value);
        var exposure = Field(fields, MetadataGroup.Exif, "ExposureTime");
        Assert.Equal("1/250", exposure.Value);
        Assert.False(exposure.Editable);
        Assert.True(exposure.Removable);
        Assert.DoesNotContain(fields, f => f.Group == MetadataGroup.Exif && f.Key.StartsWith("GPS"));

        var position = Field(fields, MetadataGroup.Gps, "position");
        Assert.Equal("33.8938, 35.5018", position.Value);
        Assert.False(position.Editable);
        var latitude = Field(fields, MetadataGroup.Gps, "GPSLatitude");
        Assert.False(latitude.Editable);
        Assert.True(latitude.Removable);

        Assert.Equal("Old caption", Field(fields, MetadataGroup.Iptc, "Caption").Value);
        Assert.Equal("beach; sun", Field(fields, MetadataGroup.Iptc, "Keyword").Value);
        Assert.True(Field(fields, MetadataGroup.Iptc, "Byline").Editable);

        Assert.Equal("Old Author", Field(fields, MetadataGroup.Xmp, "dc:creator").Value);
        Assert.Equal("Old title", Field(fields, MetadataGroup.Xmp, "dc:title").Value);
        Assert.Equal("Camera App 2", Field(fields, MetadataGroup.Xmp, "xmp:CreatorTool").Value);

        var order = fields.Select(f => f.Group).Distinct().ToList();
        Assert.Equal([MetadataGroup.File, MetadataGroup.Exif, MetadataGroup.Gps, MetadataGroup.Iptc, MetadataGroup.Xmp], order);
    }

    [Fact]
    public void Read_of_a_photo_without_metadata_lists_only_the_file()
    {
        using var dir = new TempDir();
        var fields = ImageMetadata.Read(RichJpeg(dir.Combine("plain.jpg"), metadata: false));
        Assert.DoesNotContain(fields, f => f.Group is MetadataGroup.Gps or MetadataGroup.Iptc or MetadataGroup.Xmp);
        Assert.DoesNotContain(fields, f => f.Group == MetadataGroup.Exif && f.Key != "Orientation");
    }

    // ---------------------------------------------------------------- EXIF on JPEG (lossless)

    [Fact]
    public void Editing_an_exif_text_field_keeps_the_compressed_image_and_every_other_segment_identical()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));
        var before = File.ReadAllBytes(jpg);

        var output = Edit(dir, jpg, new MetadataChange(MetadataGroup.Exif, "Artist", "New Artist"));

        Assert.Equal(before, File.ReadAllBytes(jpg)); // the input is never touched
        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        var exif = result.GetExifProfile()!;
        Assert.Equal("New Artist", exif.GetValue(ExifTag.Artist)!.Value);
        Assert.Equal("TestCam", exif.GetValue(ExifTag.Make)!.Value);
        Assert.NotNull(exif.GetValue(ExifTag.GPSLatitude));
        Assert.NotNull(result.GetColorProfile());
        Assert.Equal(0xE0, Segments(File.ReadAllBytes(output))[0].Marker); // APP0 JFIF stays first
        Assert.Equal("Old caption", result.GetIptcProfile()!.GetValue(IptcTag.Caption)!.Value);
        Assert.Equal("Old title", XmpValue(result, Dc + "title"));
        Assert.Equal("New Artist", Field(ImageMetadata.Read(output), MetadataGroup.Exif, "Artist").Value);
    }

    [Fact]
    public void New_exif_fields_can_be_added_to_a_photo_that_has_no_exif()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("plain.jpg"), metadata: false);

        var output = Edit(dir, jpg,
            new MetadataChange(MetadataGroup.Exif, "Artist", "Moustafa"),
            new MetadataChange(MetadataGroup.Exif, "Copyright", "© 2026 DropFlow"),
            new MetadataChange(MetadataGroup.Exif, "ImageDescription", "Harbour at dusk"),
            new MetadataChange(MetadataGroup.Exif, "DateTimeOriginal", "2026-10-21 18:05:00"));

        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        var exif = result.GetExifProfile()!;
        Assert.Equal("Moustafa", exif.GetValue(ExifTag.Artist)!.Value);
        Assert.Equal("© 2026 DropFlow", exif.GetValue(ExifTag.Copyright)!.Value);
        Assert.Equal("Harbour at dusk", exif.GetValue(ExifTag.ImageDescription)!.Value);
        Assert.Equal("2026:10:21 18:05:00", exif.GetValue(ExifTag.DateTimeOriginal)!.Value);
        var exifSegment = Assert.Single(Segments(File.ReadAllBytes(output)), s => s.Marker == 0xE1 && Starts(s.Payload, "Exif\0\0"));
        Assert.Equal((byte)'I', exifSegment.Payload[6]); // TIFF header right after "Exif\0\0"
    }

    [Fact]
    public void A_single_exif_field_can_be_removed_and_read_only_fields_are_refused()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));

        var output = Edit(dir, jpg,
            new MetadataChange(MetadataGroup.Exif, "Make", null),
            new MetadataChange(MetadataGroup.Exif, "ExposureTime", null),
            new MetadataChange(MetadataGroup.Exif, "Model", "  "));

        using var result = new MagickImage(output);
        var exif = result.GetExifProfile()!;
        Assert.Null(exif.GetValue(ExifTag.Make));
        Assert.Null(exif.GetValue(ExifTag.ExposureTime));
        Assert.Null(exif.GetValue(ExifTag.Model)); // a blank value removes too
        Assert.Equal("Original Artist", exif.GetValue(ExifTag.Artist)!.Value);

        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Exif, "ExposureTime", "1/60")));
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Gps, "GPSLatitude", "1")));
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.File, "name", "x.jpg")));
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Exif, "DateTime", "not a date")));
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Exif, "NoSuchTag", "x")));
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Exif, "*", "x")));
        Assert.Single(Directory.GetFiles(dir.Path, "out-*")); // refused edits leave nothing behind
    }

    [Fact]
    public void Removing_the_gps_group_removes_the_location_from_exif_and_xmp_only()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("trip.jpg"));

        var output = Edit(dir, jpg, new MetadataChange(MetadataGroup.Gps, "*", null));

        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        var exif = result.GetExifProfile()!;
        Assert.DoesNotContain(exif.Values, v => v.Tag.ToString().StartsWith("GPS"));
        Assert.Equal("Original Artist", exif.GetValue(ExifTag.Artist)!.Value);
        Assert.DoesNotContain("GPSLatitude", Encoding.UTF8.GetString(result.GetXmpProfile()!.ToByteArray()));
        Assert.Equal("Old title", XmpValue(result, Dc + "title"));

        var fields = ImageMetadata.Read(output);
        Assert.DoesNotContain(fields, f => f.Group == MetadataGroup.Gps);
        Assert.Equal("Old caption", Field(fields, MetadataGroup.Iptc, "Caption").Value);
    }

    [Fact]
    public void Remove_all_drops_exif_iptc_and_xmp_but_keeps_the_colour_profile_and_the_pixels()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));

        var output = Edit(dir, jpg, [.. ImageMetadata.RemoveAll()]);

        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile());
        Assert.Null(result.GetIptcProfile());
        Assert.Null(result.GetXmpProfile());
        Assert.False(result.HasProfile("8bim"));
        Assert.NotNull(result.GetColorProfile());
        var segments = Segments(File.ReadAllBytes(output));
        Assert.DoesNotContain(segments, s => s.Marker is 0xE1 or 0xED);
        Assert.Contains(segments, s => s.Marker == 0xE2 && Starts(s.Payload, "ICC_PROFILE\0"));
        Assert.DoesNotContain(ImageMetadata.Read(output), f => f.Group != MetadataGroup.File);
    }

    [Fact]
    public void Orientation_survives_edits_and_remove_all_and_cannot_be_removed()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("portrait.jpg"), orientation: 6);
        var orientation = Field(ImageMetadata.Read(jpg), MetadataGroup.Exif, "Orientation");
        Assert.Equal("6", orientation.Value);
        Assert.False(orientation.Editable || orientation.Removable);

        var edited = Edit(dir, jpg, new MetadataChange(MetadataGroup.Exif, "Artist", "Someone"));
        var cleaned = Edit(dir, jpg, [.. ImageMetadata.RemoveAll()]);

        foreach (var output in new[] { edited, cleaned })
        {
            AssertPixelsAndScanIdentical(jpg, output);
            using var result = new MagickImage(output);
            Assert.Equal(Orientation.RightTop, result.Orientation);
            Assert.Equal((ushort)6, result.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
        }
        using (var stripped = new MagickImage(cleaned))
            Assert.Equal(["Orientation"], stripped.GetExifProfile()!.Values.Select(v => v.Tag.ToString()));
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Exif, "Orientation", null)));
    }

    // ---------------------------------------------------------------- IPTC and XMP

    [Fact]
    public void Iptc_fields_can_be_edited_added_split_and_removed()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));

        var output = Edit(dir, jpg,
            new MetadataChange(MetadataGroup.Iptc, "Caption", "New caption"),
            new MetadataChange(MetadataGroup.Iptc, "Headline", "Big news"),
            new MetadataChange(MetadataGroup.Iptc, "CopyrightNotice", "© ScoutHub"),
            new MetadataChange(MetadataGroup.Iptc, "Keyword", "sea; boat ;  harbour"),
            new MetadataChange(MetadataGroup.Iptc, "Byline", null));

        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        var iptc = result.GetIptcProfile()!;
        Assert.Equal("New caption", iptc.GetValue(IptcTag.Caption)!.Value);
        Assert.Equal("Big news", iptc.GetValue(IptcTag.Headline)!.Value);
        Assert.Equal("© ScoutHub", iptc.GetValue(IptcTag.CopyrightNotice)!.Value);
        Assert.Equal(["sea", "boat", "harbour"], iptc.GetAllValues(IptcTag.Keyword).Select(v => v.Value));
        Assert.Null(iptc.GetValue(IptcTag.Byline));
        Assert.Equal("sea; boat; harbour", Field(ImageMetadata.Read(output), MetadataGroup.Iptc, "Keyword").Value);
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Iptc, "NotAnIptcTag", "x")));
    }

    [Fact]
    public void Iptc_can_be_added_to_a_photo_without_it()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("plain.jpg"), metadata: false);

        var output = Edit(dir, jpg, new MetadataChange(MetadataGroup.Iptc, "Caption", "First caption"));

        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        Assert.Equal("First caption", result.GetIptcProfile()!.GetValue(IptcTag.Caption)!.Value);
        Assert.Single(Segments(File.ReadAllBytes(output)), s => s.Marker == 0xED && Starts(s.Payload, "Photoshop 3.0\0"));
    }

    [Fact]
    public void Xmp_fields_can_be_edited_added_and_removed()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));

        var output = Edit(dir, jpg,
            new MetadataChange(MetadataGroup.Xmp, "dc:title", "New title"),
            new MetadataChange(MetadataGroup.Xmp, "dc:rights", "All rights reserved"),
            new MetadataChange(MetadataGroup.Xmp, "dc:description", "A long description"),
            new MetadataChange(MetadataGroup.Xmp, "dc:creator", "Ann; Bob"),
            new MetadataChange(MetadataGroup.Xmp, "xmp:CreatorTool", "DropFlow"),
            new MetadataChange(MetadataGroup.Xmp, "exif:GPSLatitude", null));

        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        var doc = result.GetXmpProfile()!.ToXDocument()!;
        Assert.Equal("New title", XmpValue(result, Dc + "title"));
        Assert.Equal("All rights reserved", XmpValue(result, Dc + "rights"));
        Assert.Equal("x-default", (string?)doc.Descendants(Dc + "rights").Descendants(Rdf + "li").Single().Attribute(XNamespace.Xml + "lang"));
        Assert.Equal("A long description", XmpValue(result, Dc + "description"));
        Assert.Equal(["Ann", "Bob"], doc.Descendants(Dc + "creator").Descendants(Rdf + "li").Select(e => e.Value));
        Assert.DoesNotContain("GPSLatitude", doc.ToString());

        var fields = ImageMetadata.Read(output);
        Assert.Equal("DropFlow", Field(fields, MetadataGroup.Xmp, "xmp:CreatorTool").Value);
        Assert.Equal("Ann; Bob", Field(fields, MetadataGroup.Xmp, "dc:creator").Value);
        Assert.Throws<ArgumentException>(() => Edit(dir, jpg, new MetadataChange(MetadataGroup.Xmp, "nosuchprefix:thing", "x")));
    }

    [Fact]
    public void An_xmp_packet_is_created_when_the_photo_has_none()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("plain.jpg"), metadata: false);

        var output = Edit(dir, jpg,
            new MetadataChange(MetadataGroup.Xmp, "dc:creator", "Moustafa"),
            new MetadataChange(MetadataGroup.Xmp, "dc:title", "Title"),
            new MetadataChange(MetadataGroup.Xmp, "photoshop:City", "Beirut"));

        AssertPixelsAndScanIdentical(jpg, output);
        using var result = new MagickImage(output);
        Assert.Equal("Moustafa", XmpValue(result, Dc + "creator"));
        Assert.Equal("Title", XmpValue(result, Dc + "title"));
        Assert.Equal("Beirut", XmpValue(result, XName.Get("City", "http://ns.adobe.com/photoshop/1.0/")));
        var segment = Assert.Single(Segments(File.ReadAllBytes(output)), s => s.Marker == 0xE1 && Starts(s.Payload, "http://ns.adobe.com/xap/1.0/\0"));
        Assert.Contains("<?xpacket", Encoding.UTF8.GetString(segment.Payload));
    }

    // ---------------------------------------------------------------- Arabic / Unicode

    [Fact]
    public void Arabic_text_and_an_arabic_file_name_round_trip_in_every_group()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("صورة رحلة بيروت.jpg"));

        var output = dir.Combine("صورة رحلة بيروت (معدلة).jpg");
        ImageMetadata.Apply(jpg,
        [
            new(MetadataGroup.Exif, "Artist", Arabic),
            new(MetadataGroup.Exif, "XPAuthor", Arabic),
            new(MetadataGroup.Exif, "XPTitle", Arabic),
            new(MetadataGroup.Exif, "UserComment", Arabic),
            new(MetadataGroup.Iptc, "Caption", Arabic),
            new(MetadataGroup.Iptc, "Keyword", "بحر؛ شمس"),
            new(MetadataGroup.Xmp, "dc:title", Arabic),
            new(MetadataGroup.Xmp, "dc:creator", Arabic),
        ], output);

        AssertPixelsAndScanIdentical(jpg, output);

        // Reader 1: Magick.NET.
        using var result = new MagickImage(output);
        var exif = result.GetExifProfile()!;
        Assert.Equal(Arabic, exif.GetValue(ExifTag.Artist)!.Value); // EXIF ASCII tag: written as UTF-8 bytes
        Assert.Equal(Arabic, Encoding.Unicode.GetString(exif.GetValue(ExifTag.XPAuthor)!.Value).TrimEnd('\0'));
        Assert.Equal(Arabic, result.GetIptcProfile()!.GetValue(IptcTag.Caption)!.Value);
        Assert.Equal(["بحر", "شمس"], result.GetIptcProfile()!.GetAllValues(IptcTag.Keyword).Select(v => v.Value));
        Assert.Equal(Arabic, XmpValue(result, Dc + "title"));
        Assert.Equal(Arabic, XmpValue(result, Dc + "creator"));

        // Reader 2: the raw bytes. IPTC declares UTF-8 (1:90 = ESC % G); the EXIF Artist bytes are UTF-8.
        var bytes = File.ReadAllBytes(output);
        var app13 = Segments(bytes).Single(s => s.Marker == 0xED).Payload;
        Assert.True(app13.AsSpan().IndexOf(new byte[] { 0x1C, 1, 90, 0, 3, 0x1B, 0x25, 0x47 }) >= 0);
        Assert.True(app13.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Arabic)) >= 0);
        var app1 = Segments(bytes).Single(s => s.Marker == 0xE1 && Starts(s.Payload, "Exif\0\0")).Payload;
        Assert.True(app1.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Arabic)) >= 0);
        Assert.True(app1.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Arabic)) >= 0); // XP*/UserComment are UTF-16

        var fields = ImageMetadata.Read(output);
        Assert.Equal("صورة رحلة بيروت (معدلة).jpg", Field(fields, MetadataGroup.File, "name").Value);
        foreach (var (group, key) in new[]
                 {
                     (MetadataGroup.Exif, "Artist"), (MetadataGroup.Exif, "XPAuthor"), (MetadataGroup.Exif, "XPTitle"), (MetadataGroup.Exif, "UserComment"),
                     (MetadataGroup.Iptc, "Caption"), (MetadataGroup.Xmp, "dc:title"), (MetadataGroup.Xmp, "dc:creator"),
                 })
            Assert.Equal(Arabic, Field(fields, group, key).Value);
        Assert.Equal("بحر; شمس", Field(fields, MetadataGroup.Iptc, "Keyword").Value);
    }

    // ---------------------------------------------------------------- other formats

    [Fact]
    public void Png_metadata_can_be_edited_and_removed_without_changing_pixels()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"));

        var output = Edit(dir, png,
            new MetadataChange(MetadataGroup.Exif, "Artist", "Moustafa"),
            new MetadataChange(MetadataGroup.Xmp, "dc:title", Arabic),
            new MetadataChange(MetadataGroup.Iptc, "Caption", "PNG caption"));

        using (var original = new MagickImage(png))
        using (var result = new MagickImage(output))
        {
            Assert.Equal(MagickFormat.Png, result.Format);
            Assert.Equal(0, original.Compare(result, ErrorMetric.Absolute));
            Assert.Equal("Moustafa", result.GetExifProfile()!.GetValue(ExifTag.Artist)!.Value);
            Assert.Equal(Arabic, XmpValue(result, Dc + "title"));
            Assert.Equal("PNG caption", result.GetIptcProfile()!.GetValue(IptcTag.Caption)!.Value);
        }
        var fields = ImageMetadata.Read(output);
        Assert.Equal("Moustafa", Field(fields, MetadataGroup.Exif, "Artist").Value);
        Assert.Equal("PNG", Field(fields, MetadataGroup.File, "format").Value);

        var cleaned = Edit(dir, output, [.. ImageMetadata.RemoveAll()]);
        using var clean = new MagickImage(cleaned);
        Assert.Null(clean.GetExifProfile());
        Assert.Null(clean.GetXmpProfile());
        Assert.Null(clean.GetIptcProfile());
    }

    [Fact]
    public void Tiff_takes_xmp_and_iptc_but_refuses_exif_instead_of_losing_it()
    {
        using var dir = new TempDir();
        var input = dir.Combine("p.tiff");
        using (var img = new MagickImage(MagickColors.Teal, 40, 30)) img.Write(input);

        var output = Edit(dir, input,
            new MetadataChange(MetadataGroup.Xmp, "dc:title", Arabic),
            new MetadataChange(MetadataGroup.Iptc, "Caption", Arabic));

        using (var result = new MagickImage(output))
        {
            Assert.Equal(MagickFormat.Tiff, result.Format);
            Assert.Equal(Arabic, XmpValue(result, Dc + "title"));
            Assert.Equal(Arabic, result.GetIptcProfile()!.GetValue(IptcTag.Caption)!.Value);
        }
        Assert.Equal(Arabic, Field(ImageMetadata.Read(output), MetadataGroup.Iptc, "Caption").Value);
        var error = Assert.Throws<ArgumentException>(() => Edit(dir, input, new MetadataChange(MetadataGroup.Exif, "Artist", "x")));
        Assert.Contains("TIFF", error.Message);
    }

    [Theory]
    [InlineData("webp")]
    public void Other_formats_take_exif_and_xmp(string ext)
    {
        using var dir = new TempDir();
        var input = dir.Combine("p." + ext);
        using (var img = new MagickImage(MagickColors.Teal, 40, 30)) img.Write(input);

        var output = Edit(dir, input,
            new MetadataChange(MetadataGroup.Exif, "Artist", "Moustafa"),
            new MetadataChange(MetadataGroup.Xmp, "dc:title", Arabic));

        using var result = new MagickImage(output);
        Assert.Equal(ImageIo.FormatOf(ext), result.Format);
        Assert.Equal("Moustafa", result.GetExifProfile()!.GetValue(ExifTag.Artist)!.Value);
        Assert.Equal(Arabic, XmpValue(result, Dc + "title"));
        var fields = ImageMetadata.Read(output);
        Assert.Equal("Moustafa", Field(fields, MetadataGroup.Exif, "Artist").Value);
        Assert.Equal(Arabic, Field(fields, MetadataGroup.Xmp, "dc:title").Value);
    }

    // ---------------------------------------------------------------- failures and safety

    [Fact]
    public void A_corrupt_or_truncated_jpeg_fails_clearly_and_writes_nothing()
    {
        using var dir = new TempDir();
        var good = File.ReadAllBytes(RichJpeg(dir.Combine("good.jpg")));
        var garbage = dir.Combine("garbage.jpg");
        File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes("this is not a picture at all"));
        var truncated = dir.Combine("truncated.jpg");
        File.WriteAllBytes(truncated, good[..(good.Length / 2)]);
        var headerOnly = dir.Combine("header.jpg");
        File.WriteAllBytes(headerOnly, good[..300]);

        foreach (var bad in new[] { garbage, truncated, headerOnly })
        {
            var output = dir.Combine(Path.GetFileNameWithoutExtension(bad) + " (edited).jpg");
            var error = Assert.Throws<InvalidDataException>(() =>
                ImageMetadata.Apply(bad, [new MetadataChange(MetadataGroup.Exif, "Artist", "x")], output));
            Assert.Contains("JPEG", error.Message);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".part"));
            Assert.Throws<InvalidDataException>(() => ImageMetadata.Read(bad));
        }
    }

    [Fact]
    public void A_metadata_block_over_64_KB_fails_clearly()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));
        var output = dir.Combine("big.jpg");

        var error = Assert.Throws<InvalidDataException>(() =>
            ImageMetadata.Apply(jpg, [new MetadataChange(MetadataGroup.Xmp, "dc:description", new string('x', 70_000))], output));

        Assert.Contains("64 KB", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Apply_never_overwrites_an_existing_file_or_the_input()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));
        var taken = dir.WriteText("taken.jpg", "keep me");
        var change = new MetadataChange(MetadataGroup.Exif, "Artist", "x");

        Assert.Throws<IOException>(() => ImageMetadata.Apply(jpg, [change], taken));
        Assert.Throws<IOException>(() => ImageMetadata.Apply(jpg, [change], jpg));
        Assert.Equal("keep me", File.ReadAllText(taken));
    }

    // ---------------------------------------------------------------- JSON

    [Fact]
    public void Changes_round_trip_through_json()
    {
        IReadOnlyList<MetadataChange> changes =
        [
            new(MetadataGroup.Exif, "Artist", Arabic),
            new(MetadataGroup.Gps, "*", null),
            new(MetadataGroup.Xmp, "dc:title", "\"quoted\" & <tagged>"),
        ];

        var json = ImageMetadata.ToJson(changes);

        Assert.Equal(changes, ImageMetadata.ParseChanges(json));
        Assert.Contains("\"group\":\"Gps\"", json);
        Assert.Empty(ImageMetadata.ParseChanges(""));
        Assert.Empty(ImageMetadata.ParseChanges(null));
        Assert.Empty(ImageMetadata.ParseChanges("[]"));
        Assert.Equal([new MetadataChange(MetadataGroup.Iptc, "Caption", "x")],
            ImageMetadata.ParseChanges("[{\"Group\":\"iptc\",\"Key\":\"Caption\",\"Value\":\"x\"}]"));
        Assert.Throws<ArgumentException>(() => ImageMetadata.ParseChanges("{not json"));
        Assert.Throws<ArgumentException>(() => ImageMetadata.ParseChanges("[{\"group\":\"Nope\",\"key\":\"a\",\"value\":null}]"));
        Assert.Throws<ArgumentException>(() => ImageMetadata.ParseChanges("[{\"group\":\"Exif\",\"key\":\"\",\"value\":null}]"));
        Assert.Equal(4, ImageMetadata.RemoveAll().Count);
    }

    // ---------------------------------------------------------------- the converter

    private static Task<ConversionResult> RunMetadata(string input, Dictionary<string, object?> options) =>
        ImagesModule.Create(new FormatConverterTests.NoEngines()).Single(c => c.Operations.Contains("img.metadata"))
            .RunAsync(new ConversionJob([input], "img.metadata", options), NoProgress, CancellationToken.None);

    [Fact]
    public void The_converter_declares_the_changes_option()
    {
        var tool = ImagesModule.Create(new FormatConverterTests.NoEngines()).Single(c => c.Operations.Contains("img.metadata"));
        var option = Assert.Single(tool.OptionsFor("img.metadata"), o => o.Key == "changes");
        Assert.Equal(ToolOptionKind.Text, option.Kind);
        Assert.Equal("", option.Default);
    }

    [Fact]
    public async Task The_converter_applies_changes_json_losslessly_and_never_overwrites()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));
        var json = ImageMetadata.ToJson(
        [
            new(MetadataGroup.Exif, "Artist", "From the editor"),
            new(MetadataGroup.Gps, "*", null),
            new(MetadataGroup.Xmp, "dc:title", Arabic),
        ]);

        var first = await RunMetadata(jpg, new() { ["mode"] = "edit", ["changes"] = json });
        var second = await RunMetadata(jpg, new() { ["mode"] = "edit", ["changes"] = json });

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.EndsWith("p (edited).jpg", first.Outputs.Single());
        Assert.NotEqual(first.Outputs.Single(), second.Outputs.Single());
        AssertPixelsAndScanIdentical(jpg, first.Outputs[0]);
        using var result = new MagickImage(first.Outputs[0]);
        Assert.Equal("From the editor", result.GetExifProfile()!.GetValue(ExifTag.Artist)!.Value);
        Assert.Null(result.GetExifProfile()!.GetValue(ExifTag.GPSLatitude));
        Assert.Equal(Arabic, XmpValue(result, Dc + "title"));
    }

    [Fact]
    public async Task The_converter_strip_mode_is_lossless_on_jpeg()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));

        var r = await RunMetadata(jpg, new() { ["mode"] = "strip" });

        Assert.True(r.Success, r.Error);
        AssertPixelsAndScanIdentical(jpg, r.Outputs.Single());
        using var result = new MagickImage(r.Outputs[0]);
        Assert.Null(result.GetExifProfile());
        Assert.Null(result.GetXmpProfile());
    }

    [Fact]
    public async Task The_converter_reports_bad_changes_and_corrupt_files_as_failures()
    {
        using var dir = new TempDir();
        var jpg = RichJpeg(dir.Combine("p.jpg"));
        var garbage = dir.Combine("bad.jpg");
        File.WriteAllBytes(garbage, [0xFF, 0xD8, 0xFF, 0xE0, 0x00]);

        var badJson = await RunMetadata(jpg, new() { ["changes"] = "{oops" });
        var readOnly = await RunMetadata(jpg, new() { ["changes"] = ImageMetadata.ToJson([new(MetadataGroup.Gps, "GPSLatitude", "1")]) });
        var corrupt = await RunMetadata(garbage, new() { ["artist"] = "x" });

        Assert.False(badJson.Success);
        Assert.False(readOnly.Success);
        Assert.False(corrupt.Success);
        Assert.Contains("JPEG", corrupt.Error);
        Assert.Equal(["bad.jpg", "p.jpg"], Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order());
    }
}
