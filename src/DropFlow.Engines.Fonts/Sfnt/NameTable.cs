// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace DropFlow.Engines.Fonts.Sfnt;

/// <summary>Reads strings from a 'name' table (OpenType specification, "name - Naming Table").</summary>
internal static class NameTable
{
    public const int Family = 1;
    public const int Subfamily = 2;
    public const int FullName = 4;
    public const int Version = 5;
    public const int PostScriptName = 6;

    /// <summary>
    /// The string with name id <paramref name="nameId"/>: Windows Unicode US English first, then any Windows Unicode record, then Unicode
    /// platform records, then Macintosh Roman. Null when the font has none.
    /// </summary>
    public static string? Get(SfntFont font, int nameId)
    {
        var table = font.Table("name");
        if (table is null || table.Length < 6) return null;
        int count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        int storage = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4));
        (int Score, string Value)? best = null;
        for (var i = 0; i < count && 6 + i * 12 + 12 <= table.Length; i++)
        {
            var record = table.AsSpan(6 + i * 12, 12);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
            var language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
            var id = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (id != nameId || storage + offset + length > table.Length) continue;

            var bytes = table.AsSpan(storage + offset, length);
            var score = (platform, encoding, language) switch
            {
                (3, 1 or 10, 0x0409) => 4,
                (3, 1 or 10, _) => 3,
                (0, _, _) => 2,
                (1, 0, _) => 1,
                _ => 0,
            };
            if (score == 0 || (best is { } b && b.Score >= score)) continue;
            var value = platform == 1 ? Encoding.Latin1.GetString(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
            best = (score, value);
        }
        return best?.Value;
    }
}
