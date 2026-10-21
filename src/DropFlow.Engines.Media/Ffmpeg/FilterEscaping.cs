using System.Text;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>
/// Escaping for values placed inside an FFmpeg filtergraph string (<c>-vf</c>, <c>-af</c>, <c>-filter_complex</c>).
/// Arguments go to FFmpeg as a list (no shell), so only FFmpeg's own two escaping levels apply:
/// <list type="number">
/// <item>The <b>option level</b> (<c>key=value:key2=value2</c>): <c>\</c>, <c>'</c>, <c>:</c> and <c>=</c> are special.</item>
/// <item>The <b>filtergraph level</b> (chains separated by <c>,</c> and <c>;</c>, pads in <c>[]</c>): <c>\</c>, <c>'</c>,
///   <c>,</c> <c>;</c> <c>[</c> <c>]</c> are special.</item>
/// </list>
/// The classic Windows bug is a path such as <c>C:\Videos\a,b's.srt</c>: the drive colon splits the option, the backslashes
/// are eaten, and the comma ends the filter. <see cref="Value"/> avoids all of that by escaping for the option level and then
/// wrapping the result in single quotes, which makes the filtergraph level take it literally (quotes inside become <c>'\''</c>).
/// </summary>
internal static class FilterEscaping
{
    /// <summary>
    /// Option-level escaping: backslash-escape <c>\ ' : =</c>. FFmpeg also trims unescaped whitespace at both ends of a value,
    /// so leading and trailing whitespace is escaped too.
    /// </summary>
    public static string EscapeOptionValue(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var edgeSpace = char.IsWhiteSpace(c) && (i == 0 || i == value.Length - 1);
            if (c is '\\' or '\'' or ':' or '=' || edgeSpace) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Filtergraph-level quoting: wrap in single quotes, turning each inner quote into <c>'\''</c>.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>A literal text value that is safe anywhere in a filter option (file names, font names, style strings).</summary>
    public static string Value(string value) => Quote(EscapeOptionValue(value));

    /// <summary>
    /// A file path as a filter option value. Backslashes become forward slashes (Windows accepts both and it removes the worst
    /// escaping hazard), then the value is escaped like any other.
    /// </summary>
    public static string PathValue(string path) => Value(path.Replace('\\', '/'));

    /// <summary>
    /// An FFmpeg expression such as <c>between(t,1.5,2.3)</c>. Only filtergraph-level quoting is needed (commas); expressions
    /// must not contain colons, quotes or equals signs. Throws if they do, so a bug cannot silently build a broken graph.
    /// </summary>
    public static string Expression(string expression)
    {
        if (expression.IndexOfAny([':', '\'', '=', '\\']) >= 0)
            throw new ArgumentException($"Expression contains a character that needs escaping: {expression}", nameof(expression));
        return Quote(expression);
    }
}
