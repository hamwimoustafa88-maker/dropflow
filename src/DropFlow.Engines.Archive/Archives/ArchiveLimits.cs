using DropFlow.Core.Operations;

namespace DropFlow.Engines.Archive.Archives;

/// <summary>What to do with an archive entry whose path tries to escape the destination (<c>..</c>, absolute, drive letter).</summary>
public enum UnsafePathPolicy
{
    /// <summary>Refuse the whole archive and write nothing (default: such an entry means the archive is hostile).</summary>
    Fail,

    /// <summary>Skip the offending entries, extract the rest and report how many were skipped.</summary>
    Skip,
}

/// <summary>
/// Safety limits applied to every extraction and conversion. They exist to stop decompression bombs (a tiny file that
/// expands to terabytes, or millions of entries) from filling the disk. Job option keys: <c>maxTotalMB</c>,
/// <c>maxEntries</c>, <c>unsafePaths</c> (<c>fail</c>|<c>skip</c>), <c>password</c>.
/// </summary>
public sealed record ArchiveLimits
{
    public const long DefaultMaxTotalBytes = 10L * 1024 * 1024 * 1024;
    public const int DefaultMaxEntries = 100_000;

    /// <summary>Maximum total UNCOMPRESSED bytes, counted while reading (declared sizes in the archive are not trusted).</summary>
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;

    /// <summary>Maximum number of entries (files, folders and links).</summary>
    public int MaxEntries { get; init; } = DefaultMaxEntries;

    public UnsafePathPolicy UnsafePaths { get; init; } = UnsafePathPolicy.Fail;

    /// <summary>Password supplied by the user for an encrypted archive. Never guessed or cracked.</summary>
    public string? Password { get; init; }

    public static ArchiveLimits Default { get; } = new();

    /// <summary>Reads the optional limit keys from a job. A missing, zero or unparsable value keeps the default.</summary>
    public static ArchiveLimits FromJob(ConversionJob job)
    {
        var limits = new ArchiveLimits();

        var mb = SafeDouble(job, "maxTotalMB");
        if (mb > 0) limits = limits with { MaxTotalBytes = (long)Math.Min(mb * 1024 * 1024, long.MaxValue / 2.0) };

        var entries = SafeDouble(job, "maxEntries");
        if (entries > 0) limits = limits with { MaxEntries = (int)Math.Min(entries, int.MaxValue) };

        if (string.Equals(job.GetString("unsafePaths"), "skip", StringComparison.OrdinalIgnoreCase))
            limits = limits with { UnsafePaths = UnsafePathPolicy.Skip };

        var password = job.GetString("password");
        if (!string.IsNullOrEmpty(password)) limits = limits with { Password = password };
        return limits;
    }

    private static double SafeDouble(ConversionJob job, string key)
    {
        try { return job.GetDouble(key, 0); }
        catch (FormatException) { return 0; }
        catch (OverflowException) { return 0; }
        catch (InvalidCastException) { return 0; }
    }
}

public enum ArchiveProblem
{
    Corrupt,
    Encrypted,
    UnsafePath,
    TooLarge,
    TooManyEntries,
    Conflict,
    Unsupported,
}

/// <summary>An expected, user-explainable archive failure. Converters turn it into <c>ConversionResult.Fail</c>.</summary>
public sealed class ArchiveException(ArchiveProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public ArchiveProblem Problem { get; } = problem;
}
