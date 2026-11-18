// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Fonts.Outlines;
using DropFlow.Engines.Fonts.Sfnt;
using DropFlow.Engines.Fonts.Web;

namespace DropFlow.Engines.Fonts;

/// <summary>What a font file really is, from its first bytes (the extension can lie: many ".ttf" files are CFF fonts and the other way round).</summary>
internal enum FontContainer { Sfnt, Woff, Woff2 }

/// <summary>A font read from disk: its tables, and the original sfnt bytes when the file was a plain TTF/OTF (kept to copy them unchanged).</summary>
internal sealed record LoadedFont(SfntFont Font, FontContainer Container, byte[]? OriginalSfnt);

/// <summary>
/// Font conversions between TTF, OTF, WOFF, WOFF2 and (output only) EOT, all in-process.
/// <list type="bullet">
/// <item>TTF/OTF to WOFF and WOFF2, and back. A web font becomes the desktop font it wraps; the original tables are restored exactly
/// (WOFF) or equivalently (WOFF2 rebuilds the glyph data from its transformed form).</item>
/// <item>TTF/OTF/WOFF/WOFF2 to EOT (uncompressed, for old Internet Explorer).</item>
/// <item>TTF to OTF and OTF to TTF convert the outlines: TrueType quadratic curves become CFF cubic curves exactly; CFF cubic curves become
/// quadratic ones within a fraction of a font unit. Hinting does not carry over (each format has its own).</item>
/// </list>
/// </summary>
public sealed class FontConverter : IConverter
{
    internal static readonly string[] Inputs = ["ttf", "otf", "woff", "woff2"];
    internal static readonly string[] Targets = ["ttf", "otf", "woff", "woff2", "eot"];
    private const long MaxBytes = 64L * 1024 * 1024;

    public IReadOnlyCollection<string> Operations { get; } = Targets.Select(Operation.Convert).ToArray();

    public bool Supports(string operation, string inputExtension)
    {
        if (!Operation.IsConvert(operation)) return false;
        var target = Operation.TargetOf(operation);
        var input = ToolCatalog.Normalize(inputExtension);
        return Targets.Contains(target) && Inputs.Contains(input) && input != target;
    }

    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) => [];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file was given.");
        var input = job.Inputs[0];
        var ext = ToolCatalog.ExtensionOf(input);
        if (!Supports(job.Operation, ext)) return ConversionResult.Fail($"'{job.Operation}' is not supported for .{ext} files.");
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {Path.GetFileName(input)}");
        if (new FileInfo(input).Length > MaxBytes) return ConversionResult.Fail($"{Path.GetFileName(input)} is too large to be a font.");

        var target = Operation.TargetOf(job.Operation);
        var stopwatch = Stopwatch.StartNew();
        string? output = null;
        try
        {
            progress.Report(new JobProgress(0.05, "Reading the font"));
            var bytes = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);
            var (result, note) = await Task.Run(() => Convert(bytes, target, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report(new JobProgress(0.9, "Writing"));
            output = Reserve(input, target, job.OutputDirectory);
            await File.WriteAllBytesAsync(output, result, cancellationToken).ConfigureAwait(false);
            progress.Report(new JobProgress(1, Path.GetFileName(output)));
            return ConversionResult.Ok([output], stopwatch.Elapsed, note);
        }
        catch (OperationCanceledException)
        {
            Delete(output);
            throw;
        }
        catch (FontFailure failure)
        {
            Delete(output);
            return ConversionResult.Fail($"{Path.GetFileName(input)}: {failure.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            Delete(output);
            return ConversionResult.Fail($"{Path.GetFileName(input)}: the font could not be converted ({ex.Message}).");
        }
    }

    /// <summary>The converted file's bytes and an optional note (a resource key) for the user.</summary>
    internal static (byte[] Bytes, string? Note) Convert(byte[] input, string target, CancellationToken ct = default)
    {
        var loaded = Load(input, ct);
        var font = loaded.Font;
        ct.ThrowIfCancellationRequested();
        switch (target)
        {
            case "woff":
                return (Woff.Encode(font, ct), null);
            case "woff2":
                return (Woff2.Encode(font, ct), null);
            case "eot":
                return (Eot.Write(font, loaded.OriginalSfnt ?? font.Clone().Write()), null);
            case "ttf":
                if (font.HasTrueTypeOutlines) return (Desktop(loaded), null);
                return (OutlineConverter.CffToTrueType(font, ct).Write(), Core.Localization.NoteText.Key("note.font.outlinesConverted"));
            case "otf":
                if (font.HasCffOutlines || font.HasCff2Outlines) return (Desktop(loaded), null);
                return (OutlineConverter.TrueTypeToCff(font, ct).Write(), Core.Localization.NoteText.Key("note.font.outlinesConverted"));
            default:
                throw new FontFailure($"Cannot write .{target} fonts.");
        }
    }

    /// <summary>Reads TTF, OTF, WOFF or WOFF2 bytes, whatever the file is called.</summary>
    internal static LoadedFont Load(byte[] bytes, CancellationToken ct = default)
    {
        if (bytes.Length < 12) throw new FontFailure("The file is too short to be a font.");
        var signature = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return signature switch
        {
            Woff.Signature => new LoadedFont(Woff.Decode(bytes), FontContainer.Woff, null),
            Woff2.Signature => new LoadedFont(Woff2.Decode(bytes, ct), FontContainer.Woff2, null),
            _ => new LoadedFont(SfntFont.Read(bytes), FontContainer.Sfnt, bytes),
        };
    }

    /// <summary>The desktop font: the original file unchanged when it was one, otherwise the tables written as an sfnt.</summary>
    private static byte[] Desktop(LoadedFont loaded) => loaded.OriginalSfnt ?? loaded.Font.Clone().Write();

    private static string Reserve(string input, string extension, string? outputDirectory)
    {
        var resolver = new OutputPathResolver();
        while (true)
        {
            var path = resolver.ForFile(input, extension, "converted", outputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // another job took the name between the check and the create: pick the next one
            }
        }
    }

    private static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
