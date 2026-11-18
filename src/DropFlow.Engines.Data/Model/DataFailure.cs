// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Localization;

namespace DropFlow.Engines.Data.Model;

/// <summary>
/// An expected failure (bad file, not a table, too big). Its message is a translatable note key with arguments
/// (<see cref="NoteText.Key"/>), so the app and the CLI show it in the user's language.
/// </summary>
internal sealed class DataFailure(string message) : Exception(message)
{
    public const string TooLarge = "note.data.tooLarge";
    public const string Invalid = "note.data.invalid";
    public const string NotTable = "note.data.notTable";
    public const string NoContacts = "note.data.noContacts";
    public const string NoEvents = "note.data.noEvents";

    /// <summary>"name.json could not be read: reason". The reason is the parser's own (English) words.</summary>
    public static DataFailure BadFile(string path, string reason) =>
        new(NoteText.Key(Invalid, Clean(Path.GetFileName(path)), Clean(reason)));

    public static DataFailure NotATable(string path) => new(NoteText.Key(NotTable, Clean(Path.GetFileName(path))));

    // The note format separates arguments with '|' and parts with "; ", so neither may appear inside an argument.
    internal static string Clean(string reason) => reason.Replace('|', '/').Replace("; ", ", ").ReplaceLineEndings(" ").Trim();
}
