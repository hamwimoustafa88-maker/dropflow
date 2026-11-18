// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data.Formats;

/// <summary>JSON and JSON Lines (one JSON value per line, also called NDJSON) to and from the tree.</summary>
internal static class JsonFormat
{
    public const int MaxDepth = 256;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = MaxDepth,
    };

    // Arabic and every other script stay readable; only what JSON requires (quotes, backslash, control characters) is escaped.
    private static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = MaxDepth + 8 };
    private static readonly JsonWriterOptions Compact = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = MaxDepth + 8 };

    public static DataNode Parse(string text)
    {
        using var document = JsonDocument.Parse(text, ReadOptions);
        return FromElement(document.RootElement);
    }

    /// <summary>JSON Lines: every non-empty line is one value; the result is an array of them. Errors name the line.</summary>
    public static DataArray ParseLines(string text, out int badLine, out string? error)
    {
        var array = new DataArray();
        badLine = 0;
        error = null;
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            try
            {
                array.Items.Add(Parse(trimmed));
            }
            catch (JsonException ex)
            {
                badLine = number;
                error = ex.Message;
                return array;
            }
        }
        return array;
    }

    private static DataNode FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new DataObject();
                foreach (var p in e.EnumerateObject()) obj.Set(p.Name, FromElement(p.Value));
                return obj;
            case JsonValueKind.Array:
                var array = new DataArray();
                foreach (var item in e.EnumerateArray()) array.Items.Add(FromElement(item));
                return array;
            case JsonValueKind.String:
                return DataValue.String(e.GetString() ?? "");
            case JsonValueKind.Number:
                return DataValue.Number(e.GetRawText());
            case JsonValueKind.True:
                return DataValue.True;
            case JsonValueKind.False:
                return DataValue.False;
            default:
                return DataValue.Null;
        }
    }

    public static string Write(DataNode node) => Serialize(node, Indented) + "\n";

    /// <summary>One compact line per record: the items of the table array (see <see cref="Tables.Records"/>), or the whole value.</summary>
    public static string WriteLines(IEnumerable<DataNode> records) =>
        string.Concat(records.Select(r => Serialize(r, Compact) + "\n"));

    private static string Serialize(DataNode node, JsonWriterOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options)) WriteNode(writer, node);
        return TextDecoding.Utf8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, DataNode node)
    {
        switch (node)
        {
            case DataObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.Entries)
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, value);
                }
                writer.WriteEndObject();
                break;
            case DataArray array:
                writer.WriteStartArray();
                foreach (var item in array.Items) WriteNode(writer, item);
                writer.WriteEndArray();
                break;
            case DataValue { Kind: ValueKind.Null }:
                writer.WriteNullValue();
                break;
            case DataValue { Kind: ValueKind.Bool } b:
                writer.WriteBooleanValue(b.Text == "true");
                break;
            case DataValue { Kind: ValueKind.Number } n:
                writer.WriteRawValue(n.Text!, skipInputValidation: false);
                break;
            case DataValue s:
                writer.WriteStringValue(s.Text);
                break;
        }
    }
}
