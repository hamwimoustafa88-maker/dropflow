// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Xml.Linq;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// Rewrites a package's <c>[Content_Types].xml</c> so its main part has the macro-free content type (a .docm/.dotm main part becomes a
/// DOCX one, .xlsm/.xltm an XLSX one, .pptm/.potm/.ppsm a PPTX one) while keeping every part name. A <c>Default</c> that still gives the
/// macro-enabled type or the VBA project type to an extension is fixed (xml goes back to <c>application/xml</c>) or dropped when no part
/// uses it any more. Every other entry is copied unchanged, in order.
/// </summary>
internal static class MacroFreeContentTypes
{
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const string WordMain = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string ExcelMain = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string PowerPointMain = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
    private const string VbaProject = "application/vnd.ms-office.vbaProject";

    /// <summary>Macro-enabled (and template) main-part content types and their macro-free replacement.</summary>
    internal static readonly IReadOnlyDictionary<string, string> MainTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.ms-word.document.macroEnabled.main+xml"] = WordMain,
        ["application/vnd.ms-word.template.macroEnabledTemplate.main+xml"] = WordMain,
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml"] = WordMain,
        ["application/vnd.ms-excel.sheet.macroEnabled.main+xml"] = ExcelMain,
        ["application/vnd.ms-excel.template.macroEnabled.main+xml"] = ExcelMain,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.template.main+xml"] = ExcelMain,
        ["application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml"] = PowerPointMain,
        ["application/vnd.ms-powerpoint.template.macroEnabled.main+xml"] = PowerPointMain,
        ["application/vnd.ms-powerpoint.slideshow.macroEnabled.main+xml"] = PowerPointMain,
        ["application/vnd.openxmlformats-officedocument.presentationml.template.main+xml"] = PowerPointMain,
        ["application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml"] = PowerPointMain,
    };

    /// <summary>Rewrites <paramref name="package"/> in place, going through <paramref name="scratch"/> (a path in a temp folder).</summary>
    public static void Apply(string package, string scratch)
    {
        using (var source = ZipFile.OpenRead(package))
        {
            var typesEntry = source.GetEntry("[Content_Types].xml") ?? throw new InvalidDataException("The package has no [Content_Types].xml.");
            var mainPart = MainPartName(source);
            XDocument types;
            using (var s = typesEntry.Open()) types = XDocument.Load(s);
            Fix(types, mainPart, source.Entries.Select(e => e.FullName).ToList());

            using var target = ZipFile.Open(scratch, ZipArchiveMode.Create);
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                using var to = copy.Open();
                if (entry == typesEntry) types.Save(to, SaveOptions.DisableFormatting);
                else
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                }
            }
        }
        File.Copy(scratch, package, overwrite: true);
    }

    internal static void Fix(XDocument types, string mainPart, IReadOnlyList<string> entryNames)
    {
        var root = types.Root ?? throw new InvalidDataException("[Content_Types].xml is empty.");
        string? plain = null;

        foreach (var o in root.Elements(Ct + "Override").ToList())
        {
            var type = (string?)o.Attribute("ContentType") ?? "";
            if (MainTypes.TryGetValue(type, out var replacement))
            {
                o.SetAttributeValue("ContentType", replacement);
                plain ??= replacement;
            }
        }

        foreach (var d in root.Elements(Ct + "Default").ToList())
        {
            var type = (string?)d.Attribute("ContentType") ?? "";
            var extension = (string?)d.Attribute("Extension") ?? "";
            if (MainTypes.TryGetValue(type, out var replacement))
            {
                // Some writers type every .xml file as the main document by default: the main part gets its own Override below.
                plain ??= replacement;
                if (extension.Equals("xml", StringComparison.OrdinalIgnoreCase)) d.SetAttributeValue("ContentType", "application/xml");
                else d.Remove();
            }
            else if (type.Equals(VbaProject, StringComparison.OrdinalIgnoreCase) && !entryNames.Any(n => UsesDefault(n, extension)))
            {
                d.Remove();
            }
        }

        // A part with this extension and no Override of its own would get its type from the Default.
        bool UsesDefault(string entry, string extension) =>
            entry.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase)
            && !root.Elements(Ct + "Override").Any(o => string.Equals((string?)o.Attribute("PartName"), "/" + entry, StringComparison.OrdinalIgnoreCase));

        var main = root.Elements(Ct + "Override").FirstOrDefault(o => string.Equals((string?)o.Attribute("PartName"), mainPart, StringComparison.OrdinalIgnoreCase));
        if (main is null && plain is not null)
            root.Add(new XElement(Ct + "Override", new XAttribute("PartName", mainPart), new XAttribute("ContentType", plain)));
    }

    /// <summary>"/word/document.xml": the target of the package's officeDocument relationship.</summary>
    private static string MainPartName(ZipArchive zip)
    {
        var rels = zip.GetEntry("_rels/.rels") ?? throw new InvalidDataException("The package has no _rels/.rels.");
        XDocument doc;
        using (var s = rels.Open()) doc = XDocument.Load(s);
        var target = doc.Root?.Elements(Rel + "Relationship")
            .FirstOrDefault(r => ((string?)r.Attribute("Type") ?? "").EndsWith("/officeDocument", StringComparison.Ordinal))
            ?.Attribute("Target")?.Value ?? throw new InvalidDataException("The package has no main document.");
        return "/" + target.TrimStart('/');
    }
}
