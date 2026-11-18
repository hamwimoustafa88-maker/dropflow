namespace DropFlow.Core.Sources;

/// <summary>
/// Windows' "Mark of the Web": a <c>Zone.Identifier</c> stream saying a file came from the internet, so Office opens it in Protected View
/// and SmartScreen checks programs. Written on every link download, and copied from a source to what DropFlow makes from it (a converted
/// copy of a downloaded file is just as untrusted). Best effort: drives without alternate streams (FAT, network shares) simply skip it.
/// </summary>
public static class MarkOfTheWeb
{
    private const string Stream = ":Zone.Identifier";

    /// <summary>Marks a downloaded file (zone 3 = internet) with where it came from (the address without query or user info).</summary>
    public static void Write(string path, Uri? source = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = source is null ? null : UrlDownloader.ForLog(source);
        var text = "[ZoneTransfer]\r\nZoneId=3\r\n" + (host is null ? "" : $"HostUrl={host}\r\n");
        try { File.WriteAllText(path + Stream, text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { /* no streams on this drive */ }
    }

    /// <summary>The source's mark, if it has one (null otherwise).</summary>
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return File.Exists(path + Stream) ? File.ReadAllText(path + Stream) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Gives every output the mark of the first input that has one.</summary>
    public static void Propagate(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs)
    {
        if (!OperatingSystem.IsWindows() || outputs.Count == 0) return;
        var mark = inputs.Select(Read).FirstOrDefault(m => m is not null);
        if (mark is null) return;
        foreach (var output in outputs.Where(File.Exists))
        {
            try { File.WriteAllText(output + Stream, mark); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { /* best effort */ }
        }
    }
}
