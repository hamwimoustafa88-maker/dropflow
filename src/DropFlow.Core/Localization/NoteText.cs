// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace DropFlow.Core.Localization;

/// <summary>
/// Converters stay free of UI text, yet some of their notes ("already as small as it gets") are shown to the user. Such a note is a
/// resource key that starts with <see cref="Prefix"/>, optionally followed by <c>|</c>-separated arguments for the <c>{0}</c> placeholders
/// (<c>note.img.targetNotReached|412</c>). The app translates it with <see cref="Localize"/>; any other note text is shown as it is.
/// The job queue joins the notes of several files with "; ", so each part is translated separately.
/// </summary>
public static class NoteText
{
    public const string Prefix = "note.";
    private const char ArgumentSeparator = '|';
    private const string PartSeparator = "; ";

    /// <summary>A translatable note with optional arguments (formatted with the invariant culture so the note is the same on every PC).</summary>
    public static string Key(string key, params object[] args) =>
        args.Length == 0 ? key : key + ArgumentSeparator + string.Join(ArgumentSeparator, args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)));

    public static string? Localize(string? note, ILocalizer localizer)
    {
        if (string.IsNullOrEmpty(note)) return note;
        return string.Join(PartSeparator, note.Split(PartSeparator).Select(part => One(part, localizer)));
    }

    private static string One(string part, ILocalizer localizer)
    {
        if (!part.StartsWith(Prefix, StringComparison.Ordinal)) return part;
        var pieces = part.Split(ArgumentSeparator);
        var text = localizer[pieces[0]];
        if (text == pieces[0]) return part; // unknown key: visible, never a crash
        try
        {
            return pieces.Length == 1 ? text : string.Format(CultureInfo.CurrentUICulture, text, pieces[1..]);
        }
        catch (FormatException)
        {
            return text;
        }
    }
}
