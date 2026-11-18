// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data.Formats;

/// <summary>Header matching shared by the contacts and calendar writers.</summary>
internal static class Cards
{
    /// <summary>"E-mail Address" -> "e_mail_address", "Start Date" -> "start_date": lower case, runs of spaces, dashes and dots become '_'.</summary>
    public static string HeaderKey(string header)
    {
        var sb = new StringBuilder();
        var gap = false;
        foreach (var c in header.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c) || c is '-' or '.' or '_' or '/')
            {
                gap = sb.Length > 0;
                continue;
            }
            if (gap) sb.Append('_');
            gap = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Canonical column -> index in the table; the first header that maps to a column wins.</summary>
    public static Dictionary<string, int> Map(Table table, Func<string, string?> columnFor)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < table.Columns.Count; i++)
            if (columnFor(table.Columns[i]) is { } column) map.TryAdd(column, i);
        return map;
    }
}
