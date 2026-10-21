using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Engines.Archive.Archives;

namespace DropFlow.Engines.Archive.General.Rename;

/// <summary>
/// Batch rename in two steps. <see cref="Plan"/> is pure: it computes the old-to-new mapping and every conflict without
/// touching the disk (beyond the optional existence test). <see cref="Apply"/> performs a plan in place, all or nothing:
/// it refuses a plan with any conflict, never overwrites, and rolls back if a rename fails half-way.
/// </summary>
public static class BatchRename
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // ------------------------------------------------------------------ planning

    public static RenamePlan Plan(IReadOnlyList<string> paths, RenameSpec spec, RenamePlanOptions? options = null)
    {
        options ??= new RenamePlanOptions();
        var exists = options.Exists ?? (p => File.Exists(p) || Directory.Exists(p));
        var now = options.Now ?? DateTime.Now;
        var errors = new List<string>();

        var tokens = ParsePattern(spec.Pattern, errors);
        var find = BuildFinder(spec, errors);
        var dateFormatError = ValidateDateFormat(spec.DateFormat, now);
        if (dateFormatError is not null) errors.Add(dateFormatError);

        var items = new List<RenameItem>(paths.Count);
        for (var index = 0; index < paths.Count; index++)
        {
            var source = Path.GetFullPath(paths[index]);
            var oldName = Path.GetFileName(source);
            var sourceDir = Path.GetDirectoryName(source) ?? ".";
            var targetDir = options.DestinationDirectory is null ? sourceDir : Path.GetFullPath(options.DestinationDirectory);

            if (errors.Count > 0)
            {
                items.Add(new RenameItem(source, oldName, oldName, Path.Combine(targetDir, oldName), RenameIssue.InvalidPattern));
                continue;
            }

            if (!exists(source))
            {
                items.Add(new RenameItem(source, oldName, oldName, Path.Combine(targetDir, oldName), RenameIssue.MissingSource));
                continue;
            }

            string newName;
            try
            {
                newName = BuildName(source, index, spec, tokens, find, now);
            }
            catch (FormatException ex)
            {
                errors.Add(ex.Message);
                items.Add(new RenameItem(source, oldName, oldName, Path.Combine(targetDir, oldName), RenameIssue.InvalidPattern));
                continue;
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add("The regular expression took too long to run on a file name.");
                items.Add(new RenameItem(source, oldName, oldName, Path.Combine(targetDir, oldName), RenameIssue.InvalidPattern));
                continue;
            }

            if (newName.Length == 0)
            {
                items.Add(new RenameItem(source, oldName, oldName, Path.Combine(targetDir, oldName), RenameIssue.EmptyName));
                continue;
            }

            var target = Path.Combine(targetDir, newName);
            var issue = options.DestinationDirectory is null && string.Equals(newName, oldName, StringComparison.Ordinal)
                ? RenameIssue.Unchanged
                : RenameIssue.None;
            items.Add(new RenameItem(source, oldName, newName, target, issue));
        }

        if (errors.Count == 0) ResolveConflicts(items, options, exists);
        return new RenamePlan(items, errors, options.DestinationDirectory);
    }

    /// <summary>Finds duplicate targets and targets that already exist, then either flags or uniquifies them.</summary>
    private static void ResolveConflicts(List<RenameItem> items, RenamePlanOptions options, Func<string, bool> exists)
    {
        var inPlace = options.DestinationDirectory is null;

        // Sources that will vanish from their old name, so their old names are free for others (chains, swaps).
        var vacated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (inPlace)
            foreach (var item in items.Where(i => i.IsChange)) vacated.Add(item.SourcePath);

        // Names that stay put (unchanged files) are not available either; names given to earlier items are "claimed".
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Issue == RenameIssue.Unchanged)) held.Add(item.TargetPath);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Taken(string path) => held.Contains(path) || claimed.Contains(path) || (exists(path) && !vacated.Contains(path));

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Issue != RenameIssue.None) continue;

            var blocked = Taken(item.TargetPath);
            if (!blocked)
            {
                claimed.Add(item.TargetPath);
                continue;
            }

            // The very same file keeping its place (case-only changes are renames, handled by 'vacated').
            if (options.Conflicts == ConflictPolicy.Uniquify)
            {
                var (name, path) = FindFreeName(item, Taken);
                claimed.Add(path);
                items[i] = item with { NewName = name, TargetPath = path, Adjusted = true };
            }
            else
            {
                var duplicate = claimed.Contains(item.TargetPath);
                items[i] = item with { Issue = duplicate ? RenameIssue.DuplicateTarget : RenameIssue.TargetExists };
            }
        }
    }

    private static (string Name, string Path) FindFreeName(RenameItem item, Func<string, bool> taken)
    {
        var dir = Path.GetDirectoryName(item.TargetPath)!;
        var ext = Path.GetExtension(item.NewName);
        var stem = item.NewName[..^ext.Length];
        if (stem.Length == 0) { stem = item.NewName; ext = ""; }
        for (var n = 2; ; n++)
        {
            var name = $"{stem} ({n}){ext}";
            var path = Path.Combine(dir, name);
            if (!taken(path)) return (name, path);
        }
    }

    // ------------------------------------------------------------------ name building

    private static string BuildName(string source, int index, RenameSpec spec, IReadOnlyList<PatternToken> tokens, Func<string, string>? find, DateTime now)
    {
        var fileName = Path.GetFileName(source);
        var isFolder = Directory.Exists(source) && !File.Exists(source);

        var ext = isFolder ? "" : Path.GetExtension(fileName);
        var stem = ext.Length == 0 ? fileName : fileName[..^ext.Length];
        if (stem.Length == 0 && ext.Length > 0) { stem = ext; ext = ""; } // ".gitignore" is a name, not an extension

        if (find is not null)
        {
            if (spec.FindInExtension)
            {
                var replaced = find(stem + ext);
                var newExt = Path.GetExtension(replaced);
                ext = newExt;
                stem = newExt.Length == 0 ? replaced : replaced[..^newExt.Length];
            }
            else
            {
                stem = find(stem);
            }
        }

        if (spec.TrimWhitespace) stem = stem.Trim();
        if (spec.TrimCharacters.Length > 0) stem = stem.Trim(spec.TrimCharacters.ToCharArray());
        if (spec.CollapseSpaces) stem = Regex.Replace(stem, @"\s{2,}", " ", RegexOptions.None, RegexTimeout);
        if (spec.RemoveFirst > 0) stem = stem[Math.Min(spec.RemoveFirst, stem.Length)..];
        if (spec.RemoveLast > 0) stem = stem[..Math.Max(0, stem.Length - spec.RemoveLast)];

        if (spec.Insert.Length > 0)
        {
            var at = spec.InsertFromEnd ? stem.Length - Math.Min(spec.InsertAt, stem.Length) : Math.Min(spec.InsertAt, stem.Length);
            stem = stem.Insert(at, spec.Insert);
        }

        stem = ApplyCase(stem, spec.Case);
        if (spec.LowerCaseExtension) ext = ext.ToLowerInvariant();

        var parent = Path.GetFileName(Path.GetDirectoryName(source)) ?? "";
        var built = Expand(tokens, stem, ext, index, spec, source, parent, now);
        return CleanFileName(built);
    }

    private static string Expand(IReadOnlyList<PatternToken> tokens, string name, string ext, int index, RenameSpec spec, string source, string parent, DateTime now)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Literal:
                    sb.Append(token.Text);
                    break;
                case TokenKind.Name:
                    sb.Append(name);
                    break;
                case TokenKind.Ext:
                    // "{name}.{ext}" is a common way to write it: do not produce "photo..jpg"
                    sb.Append(sb.Length > 0 && sb[^1] == '.' && ext.StartsWith('.') ? ext[1..] : ext);
                    break;
                case TokenKind.Index:
                    sb.Append((index + 1).ToString(CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Counter:
                    var value = (long)spec.CounterStart + (long)index * spec.CounterStep;
                    sb.Append(token.Format is null ? value.ToString(CultureInfo.InvariantCulture) : value.ToString(token.Format, CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Date:
                    sb.Append(DateFor(spec.DateSource, source, now).ToString(token.Format ?? spec.DateFormat, CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Parent:
                    sb.Append(parent);
                    break;
                case TokenKind.Original:
                    sb.Append(Path.GetFileName(source));
                    break;
            }
        }
        return sb.ToString();
    }

    private static DateTime DateFor(DateSource kind, string source, DateTime now) => kind switch
    {
        DateSource.Modified => File.Exists(source) ? File.GetLastWriteTime(source) : Directory.GetLastWriteTime(source),
        DateSource.Created => File.Exists(source) ? File.GetCreationTime(source) : Directory.GetCreationTime(source),
        _ => now,
    };

    private static string ApplyCase(string text, CaseMode mode) => mode switch
    {
        CaseMode.Lower => text.ToLowerInvariant(),
        CaseMode.Upper => text.ToUpperInvariant(),
        CaseMode.Title => Capitalize(text, everyWord: true),
        CaseMode.Sentence => Capitalize(text, everyWord: false),
        _ => text,
    };

    private static string Capitalize(string text, bool everyWord)
    {
        var chars = text.ToLowerInvariant().ToCharArray();
        var startOfWord = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                if (startOfWord) chars[i] = char.ToUpperInvariant(chars[i]);
                startOfWord = false;
                if (!everyWord) break;
            }
            else if (everyWord)
            {
                startOfWord = !char.IsDigit(chars[i]) && chars[i] != '\'';
            }
        }
        return new string(chars);
    }

    /// <summary>Makes a name valid on Windows: forbidden characters become '_', device names are prefixed, trailing dots/spaces go.</summary>
    internal static string CleanFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var cleaned = SafeEntryPath.CleanFileName(name);
        return cleaned is "." or ".." ? "" : cleaned;
    }

    // ------------------------------------------------------------------ find / replace

    private static Func<string, string>? BuildFinder(RenameSpec spec, List<string> errors)
    {
        if (spec.Find.Length == 0) return null;

        if (!spec.UseRegex)
        {
            var comparison = spec.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return text => text.Replace(spec.Find, spec.Replace, comparison);
        }

        try
        {
            var regex = new Regex(spec.Find, spec.IgnoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant, RegexTimeout);
            return text => regex.Replace(text, spec.Replace);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"Invalid regular expression: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ pattern parsing

    private enum TokenKind { Literal, Name, Ext, Index, Counter, Date, Parent, Original }

    private sealed record PatternToken(TokenKind Kind, string Text = "", string? Format = null);

    private static List<PatternToken> ParsePattern(string pattern, List<string> errors)
    {
        var tokens = new List<PatternToken>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            tokens.Add(new PatternToken(TokenKind.Literal, literal.ToString()));
            literal.Clear();
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '{' && i + 1 < pattern.Length && pattern[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < pattern.Length && pattern[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c == '}') { errors.Add("Unmatched '}' in the pattern. Use '}}' for a literal brace."); continue; }
            if (c != '{') { literal.Append(c); continue; }

            var close = pattern.IndexOf('}', i + 1);
            if (close < 0) { errors.Add("Unclosed '{' in the pattern. Use '{{' for a literal brace."); break; }

            var body = pattern[(i + 1)..close];
            var colon = body.IndexOf(':');
            var key = (colon < 0 ? body : body[..colon]).Trim().ToLowerInvariant();
            var format = colon < 0 ? null : body[(colon + 1)..];
            i = close;

            FlushLiteral();
            switch (key)
            {
                case "name": tokens.Add(new PatternToken(TokenKind.Name)); break;
                case "ext": tokens.Add(new PatternToken(TokenKind.Ext)); break;
                case "n": tokens.Add(new PatternToken(TokenKind.Index)); break;
                case "counter": tokens.Add(new PatternToken(TokenKind.Counter, Format: string.IsNullOrEmpty(format) ? null : format)); break;
                case "date": tokens.Add(new PatternToken(TokenKind.Date, Format: string.IsNullOrEmpty(format) ? null : format)); break;
                case "parent": tokens.Add(new PatternToken(TokenKind.Parent)); break;
                case "original": tokens.Add(new PatternToken(TokenKind.Original)); break;
                default: errors.Add($"Unknown token '{{{body}}}'. Use {{name}}, {{ext}}, {{n}}, {{counter:000}}, {{date}}, {{parent}} or {{original}}."); break;
            }
        }

        FlushLiteral();
        foreach (var token in tokens.Where(t => t is { Kind: TokenKind.Counter or TokenKind.Date, Format: not null }))
        {
            var error = token.Kind == TokenKind.Counter ? ValidateCounterFormat(token.Format!) : ValidateDateFormat(token.Format!, DateTime.Now);
            if (error is not null) errors.Add(error);
        }
        return tokens;
    }

    private static string? ValidateCounterFormat(string format)
    {
        try { _ = 1L.ToString(format, CultureInfo.InvariantCulture); return null; }
        catch (FormatException) { return $"Invalid counter format '{format}'. Use zeros for padding, e.g. {{counter:000}}."; }
    }

    private static string? ValidateDateFormat(string format, DateTime sample)
    {
        try { _ = sample.ToString(format, CultureInfo.InvariantCulture); return null; }
        catch (FormatException) { return $"Invalid date format '{format}'. Example: yyyy-MM-dd."; }
    }

    // ------------------------------------------------------------------ applying

    /// <summary>
    /// Renames the files of a plan in place. Fails without changing anything when the plan has conflicts or went stale
    /// (a source vanished, a target appeared). Never overwrites. If a rename fails part-way, completed renames are reverted.
    /// Renames that swap or chain names (a to b while b goes to c) are done through temporary names.
    /// </summary>
    public static RenameResult Apply(RenamePlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.DestinationDirectory is not null)
            return RenameResult.Fail("This plan targets another folder: use Copy for it, Apply renames in place.");
        if (plan.HasConflicts)
            return RenameResult.Fail($"The rename has conflicts and nothing was changed: {plan.Describe()}");

        var changes = plan.Items.Where(i => i.IsChange).Select(i => new RenameMove(i.SourcePath, i.TargetPath)).ToList();
        return ExecuteMoves(changes, cancellationToken);
    }

    /// <summary>Renames finished files back to their original names (the undo of <see cref="Apply"/>). Same safety rules.</summary>
    public static RenameResult Reverse(IReadOnlyList<RenameMove> moves, CancellationToken cancellationToken = default) =>
        ExecuteMoves(moves.Select(m => new RenameMove(m.To, m.From)).ToList(), cancellationToken);

    /// <summary>Performs source-to-target moves all or nothing, never overwriting, via temporary names where names swap or chain.</summary>
    private static RenameResult ExecuteMoves(IReadOnlyList<RenameMove> moves, CancellationToken cancellationToken)
    {
        if (moves.Count == 0) return RenameResult.Ok([]);

        // Re-check the disk: the plan may be minutes old.
        var moving = new HashSet<string>(moves.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            if (!Exists(move.From))
                return RenameResult.Fail($"'{Path.GetFileName(move.From)}' no longer exists. Nothing was changed.");
            if (Exists(move.To) && !moving.Contains(move.To))
                return RenameResult.Fail($"'{Path.GetFileName(move.To)}' already exists. Nothing was changed.");
        }

        var done = new List<(string From, string To)>();
        var staged = new List<(RenameMove Move, string Temp)>();
        try
        {
            // Phase 1: moves whose target is another moving file's current name go to a temporary name first.
            foreach (var move in moves.Where(m => moving.Contains(m.To)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temp = Path.Combine(Path.GetDirectoryName(move.From)!, $".dropflow-rename-{Guid.NewGuid():N}.tmp");
                Move(move.From, temp);
                done.Add((move.From, temp));
                staged.Add((move, temp));
            }

            // Phase 2: everything else goes straight to its final name.
            foreach (var move in moves.Where(m => !moving.Contains(m.To)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Move(move.From, move.To);
                done.Add((move.From, move.To));
            }

            // Phase 3: staged items reach their final names (all the names they wanted are vacated by now).
            foreach (var (move, temp) in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Move(temp, move.To);
                done.Add((temp, move.To));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var failures = Rollback(done);
            if (ex is OperationCanceledException) throw;
            var suffix = failures == 0 ? "Everything was put back." : $"{failures} file(s) could not be put back; look for names starting with '.dropflow-rename-'.";
            return RenameResult.Fail($"Renaming failed ({ex.Message}). {suffix}");
        }

        // Report original-to-final pairs; the temporary hops are an implementation detail.
        return RenameResult.Ok(moves);
    }

    private static int Rollback(List<(string From, string To)> done)
    {
        var failures = 0;
        for (var i = done.Count - 1; i >= 0; i--)
        {
            try { Move(done[i].To, done[i].From); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; }
        }
        return failures;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Moves a file or folder; throws if the destination exists (File.Move without overwrite).</summary>
    private static void Move(string from, string to)
    {
        if (Directory.Exists(from) && !File.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: false);
    }
}