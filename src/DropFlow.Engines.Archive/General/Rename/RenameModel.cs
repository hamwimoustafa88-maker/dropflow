using System.Globalization;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Archive.General.Rename;

public enum CaseMode { None, Lower, Upper, Title, Sentence }

public enum DateSource { Today, Modified, Created }

/// <summary>What the planner does when a new name is already taken.</summary>
public enum ConflictPolicy
{
    /// <summary>Flag the item with an issue and leave its name alone (used for in-place renames: the user must fix it).</summary>
    Report,

    /// <summary>Append " (2)", " (3)"... until the name is free (used for copies, which must never overwrite).</summary>
    Uniquify,
}

public enum RenameIssue
{
    None,

    /// <summary>The new name equals the old one; nothing to do.</summary>
    Unchanged,

    /// <summary>Another file or folder already has the new name.</summary>
    TargetExists,

    /// <summary>Two inputs would get the same new name.</summary>
    DuplicateTarget,

    /// <summary>The pattern produced an empty or unusable name.</summary>
    EmptyName,

    /// <summary>The pattern or a regular expression is invalid.</summary>
    InvalidPattern,

    /// <summary>The source is missing or is not a file/folder that can be renamed.</summary>
    MissingSource,
}

/// <summary>
/// Everything the user can ask of a batch rename. Steps run in this order on the file name without its extension:
/// find/replace, trimming, removal, insertion, case; the result then replaces <c>{name}</c> in <see cref="Pattern"/>.
/// <para>Pattern tokens: <c>{name}</c> (the transformed name), <c>{ext}</c> (extension WITH its dot, empty when none; a dot written
/// right before it, as in <c>.{ext}</c>, is not doubled), <c>{n}</c> (1-based position), <c>{counter}</c> / <c>{counter:000}</c>
/// (CounterStart + position * CounterStep, zero-padded by the format), <c>{date}</c> / <c>{date:yyyyMMdd}</c>, <c>{parent}</c>
/// (name of the containing folder), <c>{original}</c> (the full original file name). <c>{{</c> and <c>}}</c> are literal braces.</para>
/// </summary>
public sealed record RenameSpec
{
    public string Pattern { get; init; } = "{name}{ext}";

    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool IgnoreCase { get; init; }

    /// <summary>Also run find/replace over the extension (so ".jpeg" can be changed to ".jpg").</summary>
    public bool FindInExtension { get; init; }

    public bool TrimWhitespace { get; init; }
    public string TrimCharacters { get; init; } = "";
    public bool CollapseSpaces { get; init; }
    public int RemoveFirst { get; init; }
    public int RemoveLast { get; init; }

    public string Insert { get; init; } = "";

    /// <summary>Position of the inserted text: characters from the start (or from the end when <see cref="InsertFromEnd"/>).</summary>
    public int InsertAt { get; init; }
    public bool InsertFromEnd { get; init; }

    public CaseMode Case { get; init; } = CaseMode.None;
    public bool LowerCaseExtension { get; init; }

    public int CounterStart { get; init; } = 1;
    public int CounterStep { get; init; } = 1;

    public DateSource DateSource { get; init; } = DateSource.Today;
    public string DateFormat { get; init; } = "yyyy-MM-dd";

    /// <summary>Reads the spec from a job's option keys (see <see cref="RenameConverter"/> for the list).</summary>
    public static RenameSpec FromJob(ConversionJob job)
    {
        return new RenameSpec
        {
            Pattern = NonEmpty(job.GetString("pattern")) ?? "{name}{ext}",
            Find = job.GetString("find") ?? "",
            Replace = job.GetString("replace") ?? "",
            UseRegex = job.GetBool("regex"),
            IgnoreCase = job.GetBool("ignoreCase"),
            FindInExtension = job.GetBool("findInExtension"),
            TrimWhitespace = job.GetBool("trim"),
            TrimCharacters = job.GetString("trimChars") ?? "",
            CollapseSpaces = job.GetBool("collapseSpaces"),
            RemoveFirst = Math.Max(0, job.GetInt("removeFirst")),
            RemoveLast = Math.Max(0, job.GetInt("removeLast")),
            Insert = job.GetString("insert") ?? "",
            InsertAt = Math.Max(0, job.GetInt("insertAt")),
            InsertFromEnd = job.GetBool("insertFromEnd"),
            Case = ParseEnum(job.GetString("case"), CaseMode.None),
            LowerCaseExtension = job.GetBool("lowerExtension"),
            CounterStart = job.GetInt("start", 1),
            CounterStep = job.GetInt("step", 1),
            DateSource = ParseEnum(job.GetString("dateSource"), DateSource.Today),
            DateFormat = NonEmpty(job.GetString("dateFormat")) ?? "yyyy-MM-dd",
        };
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) ? value : fallback;
}

public sealed record RenamePlanOptions
{
    /// <summary>Put the results in this folder (copy mode). Null renames each file where it is.</summary>
    public string? DestinationDirectory { get; init; }

    public ConflictPolicy Conflicts { get; init; } = ConflictPolicy.Report;

    /// <summary>Existence test, replaceable so planning stays pure in tests. Defaults to the file system.</summary>
    public Func<string, bool>? Exists { get; init; }

    /// <summary>"Today" for the {date} token. Defaults to the current time.</summary>
    public DateTime? Now { get; init; }
}

/// <param name="Adjusted">True when a conflict was resolved by adding " (n)" to the requested name.</param>
public sealed record RenameItem(string SourcePath, string OldName, string NewName, string TargetPath, RenameIssue Issue, bool Adjusted = false)
{
    public bool IsChange => Issue is RenameIssue.None;
    public bool IsBlocked => Issue is not (RenameIssue.None or RenameIssue.Unchanged);
}

/// <summary>The preview: for every input, its old name, its new name and whether anything is wrong.</summary>
public sealed record RenamePlan(IReadOnlyList<RenameItem> Items, IReadOnlyList<string> Errors, string? DestinationDirectory)
{
    /// <summary>True when applying the plan would fail: some name is taken, duplicated, empty or the pattern is invalid.</summary>
    public bool HasConflicts => Errors.Count > 0 || Items.Any(i => i.IsBlocked);

    public int ChangeCount => Items.Count(i => i.IsChange);

    /// <summary>"old -> new" lines for display.</summary>
    public IEnumerable<string> Preview() => Items.Select(i => $"{i.OldName} -> {i.NewName}");

    public string Describe()
    {
        var problems = Errors.Concat(Items.Where(i => i.IsBlocked).Take(5).Select(i => $"{i.OldName}: {i.Issue}"));
        return string.Join("; ", problems);
    }
}

public sealed record RenameMove(string From, string To);

public sealed record RenameResult(bool Success, string? Error, IReadOnlyList<RenameMove> Moves)
{
    public static RenameResult Ok(IReadOnlyList<RenameMove> moves) => new(true, null, moves);
    public static RenameResult Fail(string error, IReadOnlyList<RenameMove>? moves = null) => new(false, error, moves ?? []);
}

/// <summary>Compares names the way Explorer shows them: case-insensitive, digit runs by value ("file2" before "file10").</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i; var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var cmp = string.CompareOrdinal(a, b);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.Compare(x, i, y, j, 1, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
