using System.Globalization;
using System.IO;
using DropFlow.Core.Operations;

namespace DropFlow.Overlay;

public sealed record OptionError(string Key, string Code);

/// <summary>
/// The non-visual side of the generated options dialog: defaults, parsing (including Arabic-Indic digits) and validation of what the
/// user typed. Pure, so it is unit-tested; the dialog only binds controls to it.
/// </summary>
public static class OptionForm
{
    /// <summary>Resource key for an option's label: <c>opt.{operation}.{key}</c>, falling back to <c>opt.{key}</c>.</summary>
    public static string[] LabelKeys(string operation, ToolOption option) => [$"opt.{operation}.{option.Key}", $"opt.{option.Key}"];

    public static IReadOnlyDictionary<string, object?> Defaults(IEnumerable<ToolOption> options) =>
        options.ToDictionary(o => o.Key, o => o.Default);

    /// <summary>Converts Arabic-Indic and Persian digits and separators to ASCII so numbers typed on an Arabic keyboard parse.</summary>
    public static string NormalizeDigits(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                '٫' => '.',          // Arabic decimal separator
                '٬' => ',',          // Arabic thousands separator
                _ => ch,
            });
        }
        return sb.ToString();
    }

    public static bool TryParseNumber(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = NormalizeDigits(raw.Trim()).Replace(" ", "");
        // Commas: "1,000" and "1,000,000" are thousands separators, "1,5" is a decimal comma, "1,000.5" has both.
        // A lone comma followed by exactly three digits is read as thousands (so "1,500" is 1500, never 1.5).
        if (text.Contains(',') && !text.Contains('.'))
        {
            var parts = text.Split(',');
            var thousands = parts.Length > 2 || (parts[1].Length == 3 && parts[0].Length is >= 1 and <= 3 && parts[1].All(char.IsDigit));
            text = thousands ? text.Replace(",", "") : text.Replace(',', '.');
        }
        else
        {
            text = text.Replace(",", "");
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>Parses one field. Returns null and an error code ("required", "number", "min", "max", "choice", "folder") on failure.</summary>
    public static (object? Value, string? Error) Parse(ToolOption option, string? raw)
    {
        var empty = string.IsNullOrWhiteSpace(raw);
        if (empty)
        {
            if (option.Required) return (null, "required");
            return (option.Default, null);
        }

        switch (option.Kind)
        {
            // A Range with numeric limits is a slider-like number (ZIP level); without limits it is a page selection such as
            // "1-3,5", "odd" or "3r90" that the tool parses itself, so it is kept as typed.
            case ToolOptionKind.Range when option.Min is null && option.Max is null:
                return (NormalizeDigits(raw!.Trim()), null);

            case ToolOptionKind.Number:
            case ToolOptionKind.Range:
                if (!TryParseNumber(raw, out var n)) return (null, "number");
                if (option.Min is { } min && n < min) return (null, "min");
                if (option.Max is { } max && n > max) return (null, "max");
                return (n, null);

            case ToolOptionKind.Bool:
                return raw!.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on" => (true, null),
                    "false" or "0" or "no" or "off" => (false, null),
                    _ => (null, "choice"),
                };

            case ToolOptionKind.Choice:
                var picked = raw!.Trim();
                return option.Choices is { } choices && !choices.Contains(picked) ? (null, "choice") : (picked, null);

            case ToolOptionKind.Folder:
                return Directory.Exists(raw!.Trim()) ? (raw.Trim(), null) : (null, "folder");

            default:   // Text, Password: kept exactly as typed (passwords must not be trimmed)
                return (option.Kind == ToolOptionKind.Password ? raw : raw!.Trim(), null);
        }
    }

    /// <summary>Validates every field. <paramref name="raw"/> holds what the user entered, by option key; missing keys use defaults.</summary>
    public static (IReadOnlyDictionary<string, object?> Values, IReadOnlyList<OptionError> Errors) Validate(
        IEnumerable<ToolOption> options, IReadOnlyDictionary<string, string?> raw)
    {
        var values = new Dictionary<string, object?>();
        var errors = new List<OptionError>();
        foreach (var option in options)
        {
            raw.TryGetValue(option.Key, out var text);
            var (value, error) = Parse(option, text);
            if (error is not null) errors.Add(new OptionError(option.Key, error));
            else values[option.Key] = value;
        }
        return (values, errors);
    }

    /// <summary>Text to show in an input box for a default value.</summary>
    public static string ToText(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
