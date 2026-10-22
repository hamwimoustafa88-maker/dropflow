using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DropFlow.Engines.Images;

/// <summary>
/// The XMP packet as an XML tree, exposing its simple properties as "prefix:name" keys. Simple properties are
/// rdf:Description attributes and child elements holding text or one rdf:Alt / rdf:Seq / rdf:Bag of text items
/// (Alt shows the x-default item; Seq/Bag items are joined with "; " and split on ';' or '؛' when set).
/// Structures and resources are kept untouched and not listed.
/// </summary>
internal sealed class XmpPacket
{
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Xml = XNamespace.Xml;

    /// <summary>Well-known namespaces, so keys are stable whatever prefix a file happens to use.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownPrefixes = new Dictionary<string, string>
    {
        ["dc"] = "http://purl.org/dc/elements/1.1/",
        ["xmp"] = "http://ns.adobe.com/xap/1.0/",
        ["xmpRights"] = "http://ns.adobe.com/xap/1.0/rights/",
        ["xmpMM"] = "http://ns.adobe.com/xap/1.0/mm/",
        ["photoshop"] = "http://ns.adobe.com/photoshop/1.0/",
        ["exif"] = "http://ns.adobe.com/exif/1.0/",
        ["exifEX"] = "http://cipa.jp/exif/1.0/",
        ["tiff"] = "http://ns.adobe.com/tiff/1.0/",
        ["aux"] = "http://ns.adobe.com/exif/1.0/aux/",
        ["Iptc4xmpCore"] = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/",
        ["Iptc4xmpExt"] = "http://iptc.org/std/Iptc4xmpExt/2008-02-29/",
        ["crs"] = "http://ns.adobe.com/camera-raw-settings/1.0/",
        ["lr"] = "http://ns.adobe.com/lightroom/1.0/",
        ["pdf"] = "http://ns.adobe.com/pdf/1.3/",
        ["xmpNote"] = "http://ns.adobe.com/xmp/note/",
    };

    /// <summary>Array type used when a property is created (properties not listed are simple text).</summary>
    private static readonly IReadOnlyDictionary<string, string> NewArrayKind = new Dictionary<string, string>
    {
        ["dc:creator"] = "Seq",
        ["dc:date"] = "Seq",
        ["dc:title"] = "Alt",
        ["dc:description"] = "Alt",
        ["dc:rights"] = "Alt",
        ["xmpRights:UsageTerms"] = "Alt",
        ["dc:subject"] = "Bag",
        ["dc:contributor"] = "Bag",
        ["dc:publisher"] = "Bag",
        ["dc:language"] = "Bag",
        ["dc:type"] = "Bag",
        ["photoshop:SupplementalCategories"] = "Bag",
    };

    private readonly XDocument _doc;

    private XmpPacket(XDocument doc) => _doc = doc;

    /// <exception cref="InvalidDataException">The packet is not well-formed XML.</exception>
    public static XmpPacket Parse(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data).TrimStart('﻿').TrimEnd('\0', ' ', '\r', '\n', '\t');
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return new XmpPacket(XDocument.Load(reader, LoadOptions.PreserveWhitespace));
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The XMP metadata is damaged ({ex.Message}).", ex);
        }
    }

    public static XmpPacket CreateEmpty() => new(new XDocument(
        new XProcessingInstruction("xpacket", "begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\""),
        new XElement(X + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
                new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))),
        new XProcessingInstruction("xpacket", "end=\"w\"")));

    public byte[] ToBytes()
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true, Indent = false };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings)) _doc.Save(writer);
        return stream.ToArray();
    }

    private IEnumerable<XElement> Descriptions => _doc.Descendants(Rdf + "RDF").Elements(Rdf + "Description");

    private string KeyOf(XName name, XElement context)
    {
        var known = KnownPrefixes.FirstOrDefault(p => p.Value == name.NamespaceName).Key;
        var prefix = known ?? context.GetPrefixOfNamespace(name.Namespace) ?? name.NamespaceName;
        return $"{prefix}:{name.LocalName}";
    }

    private XName? NameOf(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0 || colon == key.Length - 1) return null;
        var prefix = key[..colon];
        var local = key[(colon + 1)..];
        try { XmlConvert.VerifyNCName(local); }
        catch (XmlException) { return null; }

        if (KnownPrefixes.TryGetValue(prefix, out var uri)) return XName.Get(local, uri);
        foreach (var description in Descriptions)
            if (description.GetNamespaceOfPrefix(prefix) is { } ns) return ns + local;
        return null;
    }

    private static bool IsSyntax(XAttribute a) =>
        a.IsNamespaceDeclaration || a.Name.Namespace == XNamespace.None || a.Name.Namespace == Rdf || a.Name.Namespace == Xml;

    /// <summary>The container (rdf:Alt/Seq/Bag) of a property element, or null when the element is plain text.</summary>
    private static XElement? Container(XElement property) =>
        property.Elements().FirstOrDefault(e => e.Name == Rdf + "Alt" || e.Name == Rdf + "Seq" || e.Name == Rdf + "Bag");

    private static string? SimpleValue(XElement property)
    {
        if (!property.HasElements) return property.Attribute(Rdf + "resource") is null ? property.Value : null;
        if (property.Elements().Count() != 1 || Container(property) is not { } container) return null;
        var items = container.Elements(Rdf + "li").ToList();
        if (items.Any(i => i.HasElements)) return null;
        if (container.Name == Rdf + "Alt")
        {
            var preferred = items.FirstOrDefault(i => (string?)i.Attribute(Xml + "lang") == "x-default") ?? items.FirstOrDefault();
            return preferred?.Value ?? "";
        }
        return string.Join("; ", items.Select(i => i.Value));
    }

    /// <summary>Simple properties in document order, first occurrence of each key. (key, value)</summary>
    public IEnumerable<(string Key, string Value)> Fields()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var description in Descriptions)
        {
            foreach (var attribute in description.Attributes().Where(a => !IsSyntax(a)))
            {
                var key = KeyOf(attribute.Name, description);
                if (seen.Add(key)) yield return (key, attribute.Value);
            }
            foreach (var element in description.Elements())
            {
                if (SimpleValue(element) is not { } value) continue;
                var key = KeyOf(element.Name, description);
                if (seen.Add(key)) yield return (key, value);
            }
        }
    }

    /// <summary>Removes every occurrence of the property; returns whether anything was removed.</summary>
    public bool Remove(string key)
    {
        if (NameOf(key) is not { } name) return false;
        return RemoveWhere(n => n == name);
    }

    public bool RemoveWhere(Func<XName, bool> match)
    {
        var removed = false;
        foreach (var description in Descriptions.ToList())
        {
            foreach (var a in description.Attributes().Where(a => !IsSyntax(a) && match(a.Name)).ToList()) { a.Remove(); removed = true; }
            foreach (var e in description.Elements().Where(e => match(e.Name)).ToList()) { e.Remove(); removed = true; }
        }
        return removed;
    }

    /// <exception cref="ArgumentException">The key is malformed, uses an unknown prefix, or names a structured property.</exception>
    public void Set(string key, string value)
    {
        if (NameOf(key) is not { } name)
            throw new ArgumentException($"'{key}' is not a valid XMP property name (use prefix:name with a known prefix such as dc, xmp or photoshop).");

        foreach (var description in Descriptions)
        {
            if (description.Attribute(name) is { } attribute)
            {
                attribute.Value = value;
                return;
            }
            if (description.Element(name) is not { } element) continue;
            if (SimpleValue(element) is null) throw new ArgumentException($"The XMP property {key} is structured and cannot be edited as text.");
            if (Container(element) is { } container) FillContainer(container, value);
            else element.Value = value;
            return;
        }

        var target = Descriptions.FirstOrDefault() ?? AddDescription();
        EnsurePrefix(target, name.Namespace, key[..key.IndexOf(':')]);
        var property = new XElement(name);
        if (NewArrayKind.TryGetValue(key, out var kind))
        {
            var created = new XElement(Rdf + kind);
            property.Add(created);
            FillContainer(created, value);
        }
        else property.Value = value;
        target.Add(property);
    }

    private XElement AddDescription()
    {
        var rdf = _doc.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            rdf = new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName));
            if (_doc.Root is { } root) root.Add(rdf);
            else _doc.Add(rdf);
        }
        var description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
        rdf.Add(description);
        return description;
    }

    private static void EnsurePrefix(XElement element, XNamespace ns, string prefix)
    {
        if (element.GetPrefixOfNamespace(ns) is not null) return;
        if (element.GetNamespaceOfPrefix(prefix) is null) element.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
    }

    private static void FillContainer(XElement container, string value)
    {
        if (container.Name == Rdf + "Alt")
        {
            var items = container.Elements(Rdf + "li").ToList();
            var target = items.FirstOrDefault(i => (string?)i.Attribute(Xml + "lang") == "x-default") ?? items.FirstOrDefault();
            if (target is null)
            {
                target = new XElement(Rdf + "li", new XAttribute(Xml + "lang", "x-default"));
                container.Add(target);
            }
            target.Value = value;
            return;
        }
        container.RemoveNodes();
        foreach (var item in value.Split([';', '؛'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            container.Add(new XElement(Rdf + "li", item));
    }
}
