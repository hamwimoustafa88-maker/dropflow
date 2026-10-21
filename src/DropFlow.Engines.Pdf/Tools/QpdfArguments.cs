using System.Security.Cryptography;
using DropFlow.Engines.Common;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>What a protected PDF still allows. Everything not listed is switched off.</summary>
public sealed record QpdfPermissions(bool Print, bool Copy, bool Edit, bool Annotate)
{
    public static readonly QpdfPermissions AllowAll = new(true, true, true, true);

    public bool RestrictsSomething => !(Print && Copy && Edit && Annotate);

    /// <summary>Parses "print,copy,edit,annotate" (any order, any separator). Blank means everything is allowed. Unknown words are an error.</summary>
    public static bool TryParse(string? text, out QpdfPermissions permissions, out string error)
    {
        permissions = AllowAll;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            permissions = new QpdfPermissions(false, false, false, false);
            return true;
        }

        bool print = false, copy = false, edit = false, annotate = false;
        foreach (var word in text.Split([',', ';', ' ', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (word.ToLowerInvariant())
            {
                case "print": print = true; break;
                case "copy": copy = true; break;
                case "edit": edit = true; break;
                case "annotate": annotate = true; break;
                case "all": print = copy = edit = annotate = true; break;
                default:
                    error = $"Unknown permission '{word}'. Use print, copy, edit and annotate.";
                    return false;
            }
        }
        permissions = new QpdfPermissions(print, copy, edit, annotate);
        return true;
    }
}

/// <summary>
/// Builds qpdf command lines and reads qpdf's result. Pure functions (no process is started here) so every argument list is unit-tested;
/// <see cref="QpdfTools"/> runs them through <see cref="ProcessRunner"/>.
/// </summary>
public static class QpdfArguments
{
    /// <summary>AES-256 (R6) encryption with the given passwords and permissions.</summary>
    public static IReadOnlyList<string> Protect(string input, string output, string userPassword, string ownerPassword, QpdfPermissions permissions) =>
    [
        "--encrypt", userPassword, ownerPassword, "256",
        "--print=" + (permissions.Print ? "full" : "none"),
        "--extract=" + (permissions.Copy ? "y" : "n"),
        "--modify=" + (permissions.Edit ? "all" : permissions.Annotate ? "annotate" : "none"),
        "--accessibility=y", // screen readers must keep working
        "--",
        input, output,
    ];

    /// <summary>Removes encryption. The password is the one the user typed; it is never guessed.</summary>
    public static IReadOnlyList<string> Unlock(string input, string output, string? password)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(password)) args.Add("--password=" + password);
        args.Add("--decrypt");
        args.Add(input);
        args.Add(output);
        return args;
    }

    /// <summary>Rewrites the file; qpdf rebuilds a damaged cross-reference table or recovers objects on the way.</summary>
    public static IReadOnlyList<string> Repair(string input, string output) => [input, output];

    /// <summary>Lossless size reduction: object streams, maximum Flate compression, unreferenced objects dropped.</summary>
    public static IReadOnlyList<string> Optimize(string input, string output) =>
        ["--object-streams=generate", "--compress-streams=y", "--recompress-flate", "--compression-level=9", input, output];

    /// <summary>A strong random password for the owner role when the user only asked for a user password.</summary>
    public static string GenerateOwnerPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 32);
    }

    /// <summary>
    /// qpdf exits 0 on success, 3 when it succeeded but printed warnings (the output exists), and 2 on errors.
    /// </summary>
    public static bool Succeeded(ProcessResult result, bool outputWritten) =>
        !result.TimedOut && !result.Cancelled && (result.ExitCode == 0 || (result.ExitCode == 3 && outputWritten));

    /// <summary>A short, user-facing explanation of a failed qpdf run.</summary>
    public static string Explain(ProcessResult result, string fileName)
    {
        if (result.TimedOut) return $"{fileName}: qpdf took too long and was stopped.";
        var text = result.StdErr.Length > 0 ? result.StdErr : result.StdOut;
        var lower = text.ToLowerInvariant();

        if (lower.Contains("invalid password"))
            return $"{fileName} needs a password, or the password is wrong. Enter the correct password and try again.";
        if (lower.Contains("can't find pdf header") || lower.Contains("not a pdf") || lower.Contains("unable to find trailer"))
            return $"{fileName} is not a valid PDF or is badly damaged. Try Repair PDF.";
        if (lower.Contains("damaged") || lower.Contains("xref") || lower.Contains("recovery"))
            return $"{fileName} is damaged and could not be processed ({result.LastMessage}). Try Repair PDF.";
        return $"{fileName}: qpdf failed ({result.LastMessage}).";
    }
}
