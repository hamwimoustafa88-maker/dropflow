// SPDX-License-Identifier: GPL-3.0-or-later
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// <c>doc.remove-macros</c>: a macro-enabled Word, Excel or PowerPoint file (docm, dotm, xlsm, xltm, pptm, potm, ppsm) becomes a
/// plain DOCX, XLSX or PPTX beside it. The copy loses the VBA project (<c>vbaProject.bin</c> with its data and signature parts and every
/// relationship to them), Word's macro key bindings and legacy toolbars, Excel 4.0 macro sheets (with the names that pointed into them),
/// and PowerPoint's "run macro" actions; the main part gets the macro-free content type. Templates become documents. Everything else
/// (text, sheets, slides, styles, pictures) is kept. The original is never changed. Open XML SDK plus a rewrite of [Content_Types].xml
/// (<see cref="MacroFreeContentTypes"/>), no engine.
/// </summary>
internal static class MacroTools
{
    public const string Operation = "doc.remove-macros";

    public static readonly IReadOnlyList<string> Inputs = ["docm", "dotm", "xlsm", "xltm", "pptm", "potm", "ppsm"];

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return new OperationSpec(Operation, Inputs, ctx => Task.FromResult(Run(ctx)));
    }

    /// <summary>docx for Word, xlsx for Excel, pptx for PowerPoint.</summary>
    public static string TargetExtension(string inputExtension) => inputExtension switch
    {
        "docm" or "dotm" => "docx",
        "xlsm" or "xltm" => "xlsx",
        _ => "pptx",
    };

    private static ConversionResult Run(JobContext ctx)
    {
        var ext = ToolCatalog.ExtensionOf(ctx.Input);
        if (!IsZip(ctx.Input))
            return ctx.Fail("This file is not an Office Open XML package: it may be password-protected (open it in Office, remove the password and try again) or damaged.");

        var output = ctx.ReserveFile(ctx.Input, TargetExtension(ext), "no macros");
        using (var source = new FileStream(ctx.Input, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var target = new FileStream(output, FileMode.Truncate, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(target);
        }
        ctx.Ct.ThrowIfCancellationRequested();
        ctx.Report(0.3);

        int removed;
        try
        {
            removed = ext switch
            {
                "docm" or "dotm" => CleanWord(output),
                "xlsm" or "xltm" => CleanExcel(output),
                _ => CleanPowerPoint(output),
            };
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report(0.8);
            MacroFreeContentTypes.Apply(output, ctx.TempFile("macro-free.zip"));
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            return ctx.Fail($"{Path.GetFileName(ctx.Input)} could not be read as an Office document: {ex.Message}");
        }

        ctx.Ct.ThrowIfCancellationRequested();
        ctx.Report(1, removed > 0 ? $"Removed {removed} macro part(s) or reference(s)." : "The file had no macros.");
        return removed > 0 ? ctx.Ok(output) : ctx.OkWithNote("note.doc.noMacros", output);
    }

    private static bool IsZip(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;
    }

    // ------------------------------------------------------------------ Word

    private static int CleanWord(string path)
    {
        using var doc = WordprocessingDocument.Open(path, true);
        var main = doc.MainDocumentPart ?? throw new ConversionFailure("The Word file has no document part.");
        var removed = 0;
        if (main.VbaProjectPart is { } vba) removed += Delete(main, vba);
        foreach (var keys in main.GetPartsOfType<CustomizationPart>().ToList()) removed += Delete(main, keys);
        foreach (var toolbars in main.GetPartsOfType<WordAttachedToolbarsPart>().ToList()) removed += Delete(main, toolbars);
        return removed;
    }

    // ------------------------------------------------------------------ Excel

    private static readonly string[] AutoRunNames = ["auto_open", "auto_close", "auto_activate", "auto_deactivate"];

    private static int CleanExcel(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, true);
        var wb = doc.WorkbookPart ?? throw new ConversionFailure("The Excel file has no workbook part.");
        var removed = 0;
        if (wb.VbaProjectPart is { } vba) removed += Delete(wb, vba);

        var workbook = wb.Workbook;
        var sheets = workbook.Sheets?.Elements<S.Sheet>().ToList() ?? [];
        var gone = new List<int>();
        var goneNames = new List<string>();
        for (var i = 0; i < sheets.Count; i++)
        {
            var id = sheets[i].Id?.Value;
            if (id is null || !wb.TryGetPartById(id, out var part) || part is not (MacroSheetPart or InternationalMacroSheetPart)) continue;
            gone.Add(i);
            goneNames.Add(sheets[i].Name?.Value ?? "");
            removed += Delete(wb, part);
        }

        if (gone.Count > 0)
        {
            if (gone.Count == sheets.Count)
                throw new ConversionFailure("This workbook holds nothing but Excel 4.0 macro sheets: nothing would be left once the macros are removed.");
            foreach (var i in gone) sheets[i].Remove();

            // A local name belongs to a sheet by its position: drop the names of removed sheets, renumber the rest.
            int Shift(uint index) => (int)index - gone.Count(g => g < index);
            if (workbook.DefinedNames is { } names)
            {
                foreach (var name in names.Elements<S.DefinedName>().ToList())
                {
                    if (name.LocalSheetId?.Value is { } local)
                    {
                        if (gone.Contains((int)local)) { name.Remove(); removed++; continue; }
                        name.LocalSheetId = (uint)Shift(local);
                    }
                    if (goneNames.Any(n => RefersTo(name.Text, n))) { name.Remove(); removed++; }
                }
            }
            foreach (var view in workbook.BookViews?.Elements<S.WorkbookView>() ?? [])
            {
                var remaining = sheets.Count - gone.Count;
                if (view.ActiveTab?.Value is { } active) view.ActiveTab = (uint)Math.Clamp(gone.Contains((int)active) ? 0 : Shift(active), 0, remaining - 1);
                if (view.FirstSheet?.Value is { } first) view.FirstSheet = (uint)Math.Clamp(gone.Contains((int)first) ? 0 : Shift(first), 0, remaining - 1);
            }
            // The calculation chain lists cells by sheet; Excel rebuilds it when it is missing.
            if (wb.CalculationChainPart is { } chain) wb.DeletePart(chain);

            // A workbook needs one visible sheet.
            var left = workbook.Sheets!.Elements<S.Sheet>().ToList();
            if (left.All(s => s.State is not null && s.State.Value != S.SheetStateValues.Visible)) left[0].State = null;
        }

        // Excel runs names that start with Auto_Open (and friends) as macros.
        if (workbook.DefinedNames is { } all)
        {
            foreach (var name in all.Elements<S.DefinedName>().ToList())
            {
                var n = name.Name?.Value ?? "";
                if (AutoRunNames.Any(a => n.StartsWith(a, StringComparison.OrdinalIgnoreCase))) { name.Remove(); removed++; }
            }
            if (!all.HasChildren) all.Remove();
        }

        workbook.Save();
        return removed;
    }

    /// <summary>True when a defined name's formula points into <paramref name="sheet"/> (<c>Macro1!A1</c> or <c>'Macro 1'!A1</c>).</summary>
    private static bool RefersTo(string formula, string sheet)
    {
        if (sheet.Length == 0) return false;
        var quoted = "'" + sheet.Replace("'", "''", StringComparison.Ordinal) + "'!";
        if (formula.Contains(quoted, StringComparison.OrdinalIgnoreCase)) return true;
        var bare = sheet + "!";
        var at = formula.IndexOf(bare, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            // Not part of a longer sheet name ("OldMacro1!A1" is not "Macro1!A1").
            if (at == 0 || !(char.IsLetterOrDigit(formula[at - 1]) || formula[at - 1] is '_' or '.')) return true;
            at = formula.IndexOf(bare, at + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // ------------------------------------------------------------------ PowerPoint

    private static int CleanPowerPoint(string path)
    {
        using var doc = PresentationDocument.Open(path, true);
        var presentation = doc.PresentationPart ?? throw new ConversionFailure("The PowerPoint file has no presentation part.");
        var removed = 0;
        if (presentation.VbaProjectPart is { } vba) removed += Delete(presentation, vba);

        // Shapes whose click or hover action is "run macro X" (ppaction://macro?name=X): the action is dropped, the shape stays.
        IEnumerable<OpenXmlPartRootElement?> roots = presentation.SlideParts.Select(p => (OpenXmlPartRootElement?)p.Slide)
            .Concat(presentation.SlideMasterParts.Select(p => (OpenXmlPartRootElement?)p.SlideMaster))
            .Concat(presentation.SlideMasterParts.SelectMany(m => m.SlideLayoutParts).Select(p => (OpenXmlPartRootElement?)p.SlideLayout));
        foreach (var root in roots)
        {
            if (root is null) continue;
            var actions = root.Descendants<A.HyperlinkOnClick>().Cast<OpenXmlElement>()
                .Concat(root.Descendants<A.HyperlinkOnHover>())
                .Where(IsMacroAction)
                .ToList();
            if (actions.Count == 0) continue;
            foreach (var action in actions) action.Remove();
            removed += actions.Count;
            root.Save();
        }

        return removed;
    }

    private static bool IsMacroAction(OpenXmlElement link) =>
        link.GetAttributes().Any(a => a.LocalName == "action" && (a.Value ?? "").StartsWith("ppaction://macro", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ shared

    /// <summary>Deletes <paramref name="part"/> (and the parts only it refers to, such as vbaData.xml) with its relationship. Returns 1.</summary>
    private static int Delete(OpenXmlPartContainer owner, OpenXmlPart part)
    {
        owner.DeletePart(part);
        return 1;
    }
}
