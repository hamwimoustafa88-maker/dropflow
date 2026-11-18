using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DropFlow.Core.Output;

/// <summary>What happens when a result's name is already taken.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NameConflict>))]
public enum NameConflict { Rename, Overwrite }

/// <summary>
/// Settings → Output: how results are named and dated. No pattern = the converter's own name ("photo (converted).jpg"), exactly as before.
/// Tokens: {name} source name, {ext} result extension, {tool} what was done ("converted", "compressed"…), {date} yyyy-MM-dd, {time} HHmmss,
/// {index} position in the batch. "Overwrite" never touches a source file or another result of the same job; it falls back to renaming.
/// </summary>
public sealed record OutputRules(string? NamePattern = null, NameConflict Conflict = NameConflict.Rename, bool KeepDates = false)
{
    public static readonly OutputRules Default = new();

    public bool IsDefault => string.IsNullOrWhiteSpace(NamePattern) && !KeepDates;

    /// <summary>True when the pattern makes usable file names ({name} or {index} is required so a batch never collides).</summary>
    public static bool IsValidPattern(string? pattern) =>
        string.IsNullOrWhiteSpace(pattern)
        || (pattern.Length <= 200 && (pattern.Contains("{name}") || pattern.Contains("{index}")) && pattern.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
}

/// <summary>Applies <see cref="OutputRules"/> to a finished job's results (renames, dates). Pure file work, no UI.</summary>
public static partial class OutputFinalizer
{
    public static IReadOnlyList<string> Apply(OutputRules rules, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, DateTime now)
    {
        if (rules.IsDefault || outputs.Count == 0) return outputs;
        var protectedPaths = new HashSet<string>(inputs.Concat(outputs).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        for (var i = 0; i < outputs.Count; i++)
        {
            var output = outputs[i];
            if (!File.Exists(output)) { result.Add(output); continue; }       // folders and missing files are left alone
            var source = inputs.Count == outputs.Count ? inputs[i] : inputs.Count > 0 ? inputs[0] : output;
            var final = output;
            if (OutputRules.IsValidPattern(rules.NamePattern) && !string.IsNullOrWhiteSpace(rules.NamePattern))
            {
                var stem = Expand(rules.NamePattern!, source, output, i, outputs.Count, now);
                var target = Path.Combine(Path.GetDirectoryName(output)!, stem + Path.GetExtension(output));
                if (!string.Equals(target, output, StringComparison.OrdinalIgnoreCase))
                {
                    target = Free(target, rules.Conflict, protectedPaths);
                    if (File.Exists(target)) File.Delete(target);            // Overwrite, and only of an unrelated file
                    File.Move(output, target);
                    final = target;
                    protectedPaths.Add(Path.GetFullPath(final));
                }
            }
            if (rules.KeepDates && File.Exists(source))
            {
                File.SetCreationTimeUtc(final, File.GetCreationTimeUtc(source));
                File.SetLastWriteTimeUtc(final, File.GetLastWriteTimeUtc(source));
            }
            result.Add(final);
        }
        return result;
    }

    public static string Expand(string pattern, string source, string output, int index, int count, DateTime now)
    {
        var tool = Suffix().Match(Path.GetFileNameWithoutExtension(output)) is { Success: true } m ? m.Groups[1].Value : "converted";
        var text = pattern
            .Replace("{name}", Path.GetFileNameWithoutExtension(source), StringComparison.Ordinal)
            .Replace("{ext}", Path.GetExtension(output).TrimStart('.'), StringComparison.Ordinal)
            .Replace("{tool}", tool, StringComparison.Ordinal)
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{time}", now.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{index}", (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(count.ToString(CultureInfo.InvariantCulture).Length, '0'), StringComparison.Ordinal);
        foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        text = text.Trim().TrimEnd('.');
        return text.Length == 0 ? Path.GetFileNameWithoutExtension(output) : text;
    }

    private static string Free(string target, NameConflict conflict, HashSet<string> protectedPaths)
    {
        var full = Path.GetFullPath(target);
        if (!File.Exists(full) && !Directory.Exists(full)) return full;
        if (conflict == NameConflict.Overwrite && File.Exists(full) && !protectedPaths.Contains(full)) return full;
        var dir = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // "photo (compressed 2)" → "compressed"
    [GeneratedRegex(@"\(([^()]+?)(?: \d+)?\)\s*$")]
    private static partial Regex Suffix();
}
