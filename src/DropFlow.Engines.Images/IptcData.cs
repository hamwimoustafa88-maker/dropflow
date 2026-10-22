using System.Text;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>
/// IPTC-IIM datasets, read and written by hand so the character set is under our control: everything is written as UTF-8 with
/// the "ESC % G" marker (dataset 1:90) that tells readers so, which is what lets Arabic captions and keywords survive.
/// Repeatable datasets (keywords, by-lines…) are shown as one value joined with "; " and split on ';' or '؛' when set.
/// </summary>
internal sealed class IptcData
{
    private sealed class DataSet(byte record, byte number, byte[] raw)
    {
        public byte Record { get; } = record;
        public byte Number { get; } = number;
        public byte[] Raw { get; set; } = raw;
        public string? Text { get; set; }
    }

    private static readonly byte[] Utf8Marker = [0x1B, 0x25, 0x47];

    /// <summary>IIM datasets that may occur more than once.</summary>
    private static readonly HashSet<int> Repeatable = [4, 12, 20, 25, 26, 27, 45, 47, 50, 80, 85, 118, 122];

    private readonly List<DataSet> _sets = [];

    public static IptcData Parse(byte[] data)
    {
        var result = new IptcData();
        var pos = 0;
        while (pos + 5 <= data.Length)
        {
            if (data[pos] != 0x1C) { pos++; continue; } // tolerate padding between datasets
            var record = data[pos + 1];
            var number = data[pos + 2];
            var length = (data[pos + 3] << 8) | data[pos + 4];
            pos += 5;
            if ((length & 0x8000) != 0)
            {
                // Extended dataset: the low 15 bits give how many bytes hold the real length.
                var n = length & 0x7FFF;
                if (n > 4 || pos + n > data.Length) break;
                length = 0;
                for (var i = 0; i < n; i++) length = (length << 8) | data[pos + i];
                pos += n;
            }
            if (length < 0 || pos + length > data.Length) break;
            result._sets.Add(new DataSet(record, number, data[pos..(pos + length)]));
            pos += length;
        }

        var utf8 = result._sets.Any(s => s.Record == 1 && s.Number == 90 && s.Raw.AsSpan().SequenceEqual(Utf8Marker));
        foreach (var set in result._sets.Where(IsText)) set.Text = Decode(set.Raw, utf8);
        return result;
    }

    public bool IsEmpty => !_sets.Any(s => s.Record == 2 && s.Number != 0);

    private static bool IsText(DataSet s) => s.Record == 2 && s.Number is > 0 and < 200;

    private static string Decode(byte[] raw, bool utf8)
    {
        if (utf8) return Encoding.UTF8.GetString(raw);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw); }
    }

    /// <summary>The stable key for a record-2 dataset: the Magick.NET <see cref="IptcTag"/> name, or "2:N" for numbers it lacks.</summary>
    public static string KeyOf(int number) =>
        Enum.IsDefined(typeof(IptcTag), number) && number != (int)IptcTag.Unknown ? ((IptcTag)number).ToString() : $"2:{number}";

    public static int? NumberOf(string key)
    {
        if (key.StartsWith("2:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(2), out var n) && n is > 0 and < 256) return n;
        if (Enum.TryParse<IptcTag>(key, ignoreCase: false, out var tag) && tag != IptcTag.Unknown && tag != IptcTag.RecordVersion
            && Enum.IsDefined(tag)) return (int)tag;
        return null;
    }

    public static bool IsRepeatable(int number) => Repeatable.Contains(number);

    /// <summary>Record-2 fields in file order, repeatable ones joined. (key, number, display value, editable)</summary>
    public IEnumerable<(string Key, int Number, string Value, bool Editable)> Fields()
    {
        var seen = new HashSet<int>();
        foreach (var set in _sets.Where(s => s.Record == 2 && s.Number != 0))
        {
            if (!seen.Add(set.Number)) continue;
            if (set.Text is null)
            {
                yield return (KeyOf(set.Number), set.Number, $"{set.Raw.Length:N0} bytes", false);
                continue;
            }
            var all = _sets.Where(s => s.Record == 2 && s.Number == set.Number).Select(s => s.Text ?? "");
            yield return (KeyOf(set.Number), set.Number, string.Join("; ", all), true);
        }
    }

    public bool Remove(int number) => _sets.RemoveAll(s => s.Record == 2 && s.Number == number) > 0;

    public void Set(int number, string value)
    {
        if (number is <= 0 or >= 200) throw new ArgumentException($"IPTC dataset 2:{number} is not a text field.");
        var values = IsRepeatable(number)
            ? value.Split([';', '؛'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [value];
        var created = values.Select(v =>
        {
            var raw = Encoding.UTF8.GetBytes(v);
            if (raw.Length > 0x7FFF) throw new ArgumentException($"The IPTC value for {KeyOf(number)} is too long ({raw.Length:N0} bytes; at most 32,767).");
            return new DataSet(2, (byte)number, raw) { Text = v };
        }).ToList();

        var at = _sets.FindIndex(s => s.Record == 2 && s.Number == number);
        Remove(number);
        if (at < 0)
        {
            var lastOfRecord2 = _sets.FindLastIndex(s => s.Record <= 2);
            at = lastOfRecord2 + 1;
        }
        _sets.InsertRange(Math.Min(at, _sets.Count), created);
    }

    /// <summary>Serialises as UTF-8 IIM: record 1 (with the UTF-8 marker), record 2 (version first), then the rest in order.</summary>
    public byte[] ToBytes()
    {
        using var output = new MemoryStream();
        void Write(byte record, byte number, byte[] raw)
        {
            if (raw.Length > 0x7FFF) throw new InvalidDataException("An IPTC value is longer than 32,767 bytes.");
            output.WriteByte(0x1C);
            output.WriteByte(record);
            output.WriteByte(number);
            output.WriteByte((byte)(raw.Length >> 8));
            output.WriteByte((byte)raw.Length);
            output.Write(raw);
        }

        foreach (var set in _sets.Where(s => s.Record == 1 && s.Number != 90)) Write(1, set.Number, set.Raw);
        Write(1, 90, Utf8Marker);

        var version = _sets.FirstOrDefault(s => s.Record == 2 && s.Number == 0)?.Raw ?? [0x00, 0x04];
        Write(2, 0, version);
        foreach (var set in _sets.Where(s => s.Record == 2 && s.Number != 0))
            Write(2, set.Number, set.Text is { } text ? Encoding.UTF8.GetBytes(text) : set.Raw);

        foreach (var set in _sets.Where(s => s.Record > 2).OrderBy(s => s.Record)) Write(set.Record, set.Number, set.Raw);
        return output.ToArray();
    }
}

/// <summary>Photoshop "image resource blocks" (the 8BIM list inside APP13 / the TIFF Photoshop tag). IPTC lives in resource 0x0404.</summary>
internal sealed class PhotoshopResources
{
    public const ushort IptcId = 0x0404;

    private sealed record Resource(byte[] Signature, ushort Id, byte[] Name, byte[] Data);

    private readonly List<Resource> _resources = [];

    public static PhotoshopResources Parse(byte[] data)
    {
        var result = new PhotoshopResources();
        var pos = 0;
        while (pos + 12 <= data.Length)
        {
            var signature = data[pos..(pos + 4)];
            var id = (ushort)((data[pos + 4] << 8) | data[pos + 5]);
            var nameStart = pos + 6;
            var nameLength = 1 + data[nameStart];
            if (nameLength % 2 == 1) nameLength++;
            var sizeAt = nameStart + nameLength;
            if (sizeAt + 4 > data.Length) break;
            var size = (data[sizeAt] << 24) | (data[sizeAt + 1] << 16) | (data[sizeAt + 2] << 8) | data[sizeAt + 3];
            var dataAt = sizeAt + 4;
            if (size < 0 || dataAt + size > data.Length) break;
            result._resources.Add(new Resource(signature, id, data[nameStart..sizeAt], data[dataAt..(dataAt + size)]));
            pos = dataAt + size + (size % 2);
        }
        return result;
    }

    public bool IsEmpty => _resources.Count == 0;

    public byte[]? Get(ushort id) => _resources.FirstOrDefault(r => r.Id == id)?.Data;

    /// <summary>Replaces (or adds, or with null removes) the resource <paramref name="id"/>.</summary>
    public void Set(ushort id, byte[]? data)
    {
        var at = _resources.FindIndex(r => r.Id == id);
        _resources.RemoveAll(r => r.Id == id);
        if (data is null) return;
        var resource = new Resource("8BIM"u8.ToArray(), id, [0, 0], data);
        if (at < 0) _resources.Add(resource);
        else _resources.Insert(at, resource);
    }

    public byte[] ToBytes()
    {
        using var output = new MemoryStream();
        foreach (var r in _resources)
        {
            output.Write(r.Signature);
            output.WriteByte((byte)(r.Id >> 8));
            output.WriteByte((byte)r.Id);
            output.Write(r.Name);
            output.WriteByte((byte)(r.Data.Length >> 24));
            output.WriteByte((byte)(r.Data.Length >> 16));
            output.WriteByte((byte)(r.Data.Length >> 8));
            output.WriteByte((byte)r.Data.Length);
            output.Write(r.Data);
            if (r.Data.Length % 2 == 1) output.WriteByte(0);
        }
        return output.ToArray();
    }
}
