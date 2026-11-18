// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Xml;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data.Formats;

/// <summary>
/// XML to and from the tree. The mapping (documented in docs/data-formats.md):
/// <list type="bullet">
/// <item>The document is an object with one key, the root element's name.</item>
/// <item>An element with only text is that text (a string); an empty element is null.</item>
/// <item>Otherwise it is an object: each attribute becomes <c>@name</c>, each child element a key, text beside them <c>#text</c>.</item>
/// <item>A child name that repeats becomes an array, in document order.</item>
/// <item>Names are local names: namespace prefixes and xmlns declarations are dropped. Comments and processing instructions are dropped.
/// XML has no types, so every value is a string.</item>
/// </list>
/// Writing does the reverse: <c>@name</c> keys of scalar values are attributes, <c>#text</c> is text, an array repeats its key's element,
/// a document that is not one keyed object is wrapped in <c>&lt;root&gt;</c>, and array items without a name are <c>&lt;item&gt;</c>.
/// Reading is safe by construction: DTDs are refused (no entity expansion, no external entities) and no resolver is set.
/// </summary>
internal static class XmlFormat
{
    public const int MaxDepth = 256;

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        CloseInput = false,
    };

    /// <summary>Reads from the raw bytes, so the XML declaration's encoding is honoured.</summary>
    public static DataNode Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, Settings());
        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element) throw new XmlException("The file has no root element.");
        var name = reader.LocalName;
        var root = ReadElement(reader);
        return new DataObject().Set(name, root);
    }

    public static DataNode Parse(string text) => Read(new MemoryStream(TextDecoding.Utf8.GetBytes(text)));

    // The reader is on a start element; on return it is past the element's end.
    private static DataNode ReadElement(XmlReader reader)
    {
        if (reader.Depth > MaxDepth) throw new XmlException($"Elements are nested more than {MaxDepth} levels deep.");

        DataObject? obj = null;
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                if (reader.Prefix == "xmlns" || reader.LocalName == "xmlns") continue;
                (obj ??= new DataObject()).Set("@" + reader.LocalName, DataValue.String(reader.Value));
            }
            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            reader.Read();
            return obj ?? (DataNode)DataValue.Null;
        }

        var text = new StringBuilder();
        var children = new List<KeyValuePair<string, DataNode>>();
        reader.Read();
        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var name = reader.LocalName;
                    children.Add(new(name, ReadElement(reader)));
                    continue;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                    text.Append(reader.Value);
                    break;
            }
            reader.Read();
        }
        reader.Read(); // past the end element

        var content = children.Count > 0 ? text.ToString().Trim() : text.ToString();
        if (obj is null && children.Count == 0) return content.Length == 0 && text.Length == 0 ? DataValue.Null : DataValue.String(content);

        obj ??= new DataObject();
        foreach (var group in children.GroupBy(c => c.Key))
        {
            var values = group.Select(c => c.Value).ToList();
            obj.Set(Unique(obj, group.Key), values.Count == 1 ? values[0] : new DataArray(values));
        }
        if (content.Trim().Length > 0) obj.Set("#text", DataValue.String(content));
        return obj;
    }

    // A child element named like an attribute key ("@id" cannot be an element name, so only "#text" clashes in theory) stays distinct.
    private static string Unique(DataObject obj, string key)
    {
        var candidate = key;
        for (var n = 2; obj.Contains(candidate); n++) candidate = $"{key}_{n}";
        return candidate;
    }

    public static string Write(DataNode node)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = TextDecoding.Utf8, OmitXmlDeclaration = false, NewLineChars = "\n" };
        var sb = new StringBuilder();
        using (var stringWriter = new Utf8StringWriter(sb))
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            writer.WriteStartDocument();
            if (node is DataObject { Count: 1 } obj && obj.Entries[0].Value is not DataArray)
                WriteElement(writer, obj.Entries[0].Key, obj.Entries[0].Value);
            else
                WriteElement(writer, "root", node);
            writer.WriteEndDocument();
        }
        return sb.Append('\n').ToString();
    }

    private static void WriteElement(XmlWriter writer, string name, DataNode node)
    {
        var element = ElementName(name);
        switch (node)
        {
            case DataArray array:
                writer.WriteStartElement(element);
                foreach (var item in array.Items) WriteNamed(writer, "item", item);
                writer.WriteEndElement();
                break;
            case DataObject obj:
                writer.WriteStartElement(element);
                foreach (var (key, value) in obj.Entries)
                    if (key.StartsWith('@') && key.Length > 1 && value is DataValue v)
                        writer.WriteAttributeString(ElementName(key[1..]), Clean(v.Text ?? ""));
                foreach (var (key, value) in obj.Entries)
                {
                    if (key.StartsWith('@') && key.Length > 1 && value is DataValue) continue;
                    if (key == "#text" && value is DataValue t) { writer.WriteString(Clean(t.Text ?? "")); continue; }
                    WriteNamed(writer, key, value);
                }
                writer.WriteEndElement();
                break;
            case DataValue { Kind: ValueKind.Null }:
                writer.WriteStartElement(element);
                writer.WriteEndElement();
                break;
            case DataValue value:
                writer.WriteElementString(element, Clean(value.Text ?? ""));
                break;
        }
    }

    // A key whose value is an array repeats the element once per item (the reverse of reading).
    private static void WriteNamed(XmlWriter writer, string key, DataNode value)
    {
        if (value is DataArray array)
            foreach (var item in array.Items) WriteElement(writer, key, item is DataArray ? new DataObject().Set("item", item) : item);
        else
            WriteElement(writer, key, value);
    }

    /// <summary>A valid XML name for any key: "first name" becomes first_x0020_name, "2024" becomes _x0032_024, "" becomes "_".</summary>
    private static string ElementName(string key)
    {
        if (key.Length == 0) return "_";
        var local = key.Contains(':') ? key.Replace(':', '_') : key;
        return XmlConvert.EncodeLocalName(local) ?? "_";
    }

    // Characters XML 1.0 cannot carry at all (most control characters) are dropped rather than failing the whole file.
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
            {
                sb.Append(c).Append(text[++i]);
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }
        return sb.Length == text.Length ? text : sb.ToString();
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => TextDecoding.Utf8;
    }
}
