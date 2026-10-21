namespace DropFlow.Engines.Media.Tests;

internal static class Args
{
    /// <summary>Value that follows the first occurrence of <paramref name="flag"/>.</summary>
    public static string? After(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    public static bool HasPair(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == flag && args[i + 1] == value) return true;
        return false;
    }

    public static int IndexOf(IReadOnlyList<string> args, string token) => args.ToList().IndexOf(token);
}
