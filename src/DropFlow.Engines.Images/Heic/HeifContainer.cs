// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace DropFlow.Engines.Images.Heic;

/// <summary>
/// Writes a still-image HEIF file (ISO/IEC 23008-12, brand <c>heic</c>) around one HEVC-coded picture: <c>ftyp</c>, a <c>meta</c> box with
/// the primary item (<c>hvc1</c>), its properties (<c>hvcC</c>, <c>ispe</c>, <c>colr</c>, <c>pixi</c>, optional <c>clap</c> for odd sizes),
/// an optional Exif item linked by <c>cdsc</c>, and <c>mdat</c> with the length-prefixed slice data. Magick.NET reads HEIC but its
/// bundled libheif has no encoder, so DropFlow encodes the picture with FFmpeg's libx265 and builds the container itself.
/// </summary>
internal static class HeifContainer
{
    public sealed record Picture(
        byte[] AnnexB,
        int CodedWidth,
        int CodedHeight,
        int Width,
        int Height,
        byte[]? ExifTiff = null,
        byte[]? IccProfile = null);

    /// <summary>NAL units of an Annex-B stream (start codes removed).</summary>
    public static List<byte[]> SplitNals(ReadOnlySpan<byte> stream)
    {
        var starts = new List<(int Start, int CodeLength)>();
        for (var i = 0; i + 2 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                var four = i > 0 && stream[i - 1] == 0;
                starts.Add((i + 3, four ? 4 : 3));
                i += 2;
            }
        }
        var nals = new List<byte[]>();
        for (var n = 0; n < starts.Count; n++)
        {
            var begin = starts[n].Start;
            var end = n + 1 < starts.Count ? starts[n + 1].Start - starts[n + 1].CodeLength : stream.Length;
            while (end > begin && stream[end - 1] == 0) end--; // trailing_zero_8bits
            if (end > begin) nals.Add(stream[begin..end].ToArray());
        }
        return nals;
    }

    public static int NalType(byte[] nal) => (nal[0] >> 1) & 0x3F;

    /// <summary>The RBSP of a NAL unit: header removed, emulation-prevention bytes (00 00 03) undone.</summary>
    public static byte[] Rbsp(byte[] nal, int headerBytes = 2)
    {
        var output = new List<byte>(nal.Length);
        var zeros = 0;
        for (var i = headerBytes; i < nal.Length; i++)
        {
            var b = nal[i];
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return output.ToArray();
    }

    public static byte[] Build(Picture picture)
    {
        var nals = SplitNals(picture.AnnexB);
        var vps = nals.FirstOrDefault(n => NalType(n) == 32) ?? throw new InvalidDataException("The HEVC stream has no VPS.");
        var sps = nals.FirstOrDefault(n => NalType(n) == 33) ?? throw new InvalidDataException("The HEVC stream has no SPS.");
        var pps = nals.FirstOrDefault(n => NalType(n) == 34) ?? throw new InvalidDataException("The HEVC stream has no PPS.");
        var slices = nals.Where(n => NalType(n) < 32).ToList();
        if (slices.Count == 0) throw new InvalidDataException("The HEVC stream has no picture.");

        var mdatPayload = new MemoryStream();
        foreach (var slice in slices)
        {
            mdatPayload.Write(U32((uint)slice.Length));
            mdatPayload.Write(slice);
        }
        var imageData = mdatPayload.ToArray();
        byte[]? exifData = null;
        if (picture.ExifTiff is { Length: > 0 } tiff)
        {
            exifData = new byte[4 + tiff.Length];
            BinaryPrimitives.WriteUInt32BigEndian(exifData, 0); // exif_tiff_header_offset: the TIFF header starts right away
            tiff.CopyTo(exifData, 4);
        }

        var ftyp = Box("ftyp", Concat(Ascii("heic"), U32(0), Ascii("mif1"), Ascii("heic")));

        // The meta box holds absolute file offsets (iloc), so build it once with placeholders, then again with the real offsets.
        byte[] Meta(uint imageOffset, uint exifOffset)
        {
            var hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Ascii("pict"), U32(0), U32(0), U32(0), [0]));
            var pitm = FullBox("pitm", 0, 0, U16(1));
            var infeImage = FullBox("infe", 2, 0, Concat(U16(1), U16(0), Ascii("hvc1"), [0]));
            var iinf = exifData is null
                ? FullBox("iinf", 0, 0, Concat(U16(1), infeImage))
                : FullBox("iinf", 0, 0, Concat(U16(2), infeImage, FullBox("infe", 2, 0, Concat(U16(2), U16(0), Ascii("Exif"), [0]))));

            var ilocItems = new List<byte[]> { Concat(U16(1), U16(0), U16(1), U32(imageOffset), U32((uint)imageData.Length)) };
            if (exifData is not null) ilocItems.Add(Concat(U16(2), U16(0), U16(1), U32(exifOffset), U32((uint)exifData.Length)));
            // version 0: offset_size 4, length_size 4, base_offset_size 0, reserved 0
            var iloc = FullBox("iloc", 0, 0, Concat([0x44, 0x00], U16((ushort)ilocItems.Count), Concat([.. ilocItems])));

            var properties = new List<(byte[] Box, bool Essential)>
            {
                (HvcC(vps, sps, pps), true),
                (FullBox("ispe", 0, 0, Concat(U32((uint)picture.CodedWidth), U32((uint)picture.CodedHeight))), false),
                // nclx: BT.709 primaries, sRGB transfer, BT.601 matrix, full range (what the encoder is told to produce)
                (Box("colr", Concat(Ascii("nclx"), U16(1), U16(13), U16(6), [0x80])), false),
                (FullBox("pixi", 0, 0, [3, 8, 8, 8]), false),
            };
            if (picture.IccProfile is { Length: > 0 } icc) properties.Add((Box("colr", Concat(Ascii("prof"), icc)), false));
            if (picture.Width != picture.CodedWidth || picture.Height != picture.CodedHeight)
            {
                // Clean aperture: the visible picture is the top-left Width x Height of the padded coded picture.
                properties.Add((Box("clap", Concat(
                    U32((uint)picture.Width), U32(1), U32((uint)picture.Height), U32(1),
                    I32(picture.Width - picture.CodedWidth), U32(2), I32(picture.Height - picture.CodedHeight), U32(2))), true));
            }
            var ipco = Box("ipco", Concat([.. properties.Select(p => p.Box)]));
            var associations = properties.Select((p, i) => (byte)((p.Essential ? 0x80 : 0) | (i + 1))).ToArray();
            var ipma = FullBox("ipma", 0, 0, Concat(U32(1), U16(1), [(byte)associations.Length], associations));
            var iprp = Box("iprp", Concat(ipco, ipma));

            var children = new List<byte[]> { hdlr, pitm, iloc, iinf };
            if (exifData is not null) children.Add(Box("iref", Concat(U32(0), Box("cdsc", Concat(U16(2), U16(1), U16(1))))));
            children.Add(iprp);
            return FullBox("meta", 0, 0, Concat([.. children]));
        }

        var meta = Meta(0, 0);
        var mdatHeader = 8;
        var imageOffset = (uint)(ftyp.Length + meta.Length + mdatHeader);
        var exifOffset = imageOffset + (uint)imageData.Length;
        meta = Meta(imageOffset, exifOffset);

        var mdat = Box("mdat", exifData is null ? imageData : Concat(imageData, exifData));
        return Concat(ftyp, meta, mdat);
    }

    /// <summary>HEVCDecoderConfigurationRecord (ISO/IEC 14496-15 §8.3.3) built from the stream's own parameter sets.</summary>
    private static byte[] HvcC(byte[] vps, byte[] sps, byte[] pps)
    {
        var rbsp = Rbsp(sps);
        if (rbsp.Length < 13) throw new InvalidDataException("The HEVC SPS is too short.");
        // rbsp[0]: sps_video_parameter_set_id(4) sps_max_sub_layers_minus1(3) sps_temporal_id_nesting_flag(1)
        // rbsp[1..12]: general_profile_space(2) tier(1) profile_idc(5), 32 compatibility flags, 48 constraint flags, level_idc
        var record = new MemoryStream();
        record.WriteByte(1);
        record.Write(rbsp.AsSpan(1, 12));
        record.Write([0xF0, 0x00]);           // reserved + min_spatial_segmentation_idc 0
        record.WriteByte(0xFC);                // reserved + parallelismType 0
        record.WriteByte(0xFD);                // reserved + chroma_format_idc 1 (4:2:0)
        record.WriteByte(0xF8);                // reserved + bit_depth_luma_minus8 0
        record.WriteByte(0xF8);                // reserved + bit_depth_chroma_minus8 0
        record.Write(U16(0));                  // avgFrameRate
        record.WriteByte(0x0F);                // constantFrameRate 0, numTemporalLayers 1, temporalIdNested 1, lengthSizeMinusOne 3
        record.WriteByte(3);
        foreach (var (type, nal) in new[] { (32, vps), (33, sps), (34, pps) })
        {
            record.WriteByte((byte)(0x80 | type));
            record.Write(U16(1));
            record.Write(U16((ushort)nal.Length));
            record.Write(nal);
        }
        return Box("hvcC", record.ToArray());
    }

    private static byte[] Box(string type, byte[] payload) => Concat(U32((uint)(8 + payload.Length)), Ascii(type), payload);

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload) =>
        Box(type, Concat([version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags], payload));

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }

    private static byte[] I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts) { p.CopyTo(result, at); at += p.Length; }
        return result;
    }
}
