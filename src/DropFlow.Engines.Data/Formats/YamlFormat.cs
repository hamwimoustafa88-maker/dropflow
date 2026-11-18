// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Engines.Data.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DropFlow.Engines.Data.Formats;

/// <summary>
/// YAML to and from the tree, through YamlDotNet's event parser and emitter (never its object deserializer, so a file cannot name .NET
/// types). Plain scalars follow the YAML 1.2 core schema: null/~/empty, true/false, numbers (decimal, 0x hex, 0o octal); everything else
/// and every quoted scalar is a string. One exception keeps data: a plain number with a leading zero (a postal code, 0123) stays text.
/// Anchors, aliases and "&lt;&lt;" merge keys are expanded; a file whose aliases would expand past <see cref="MaxNodes"/> values
/// ("billion laughs") or that nests deeper than <see cref="MaxDepth"/> is refused. Several documents in one file become an array.
/// </summary>
internal static partial class YamlFormat
{
    public const int MaxDepth = 256;
    public const int MaxNodes = 5_000_000;

    private sealed class Frame(bool mapping, AnchorName anchor)
    {
        public bool Mapping { get; } = mapping;
        public AnchorName Anchor { get; } = anchor;
        public List<KeyValuePair<string, DataNode>> Entries { get; } = [];
        public List<DataNode> Items { get; } = [];
        public List<(int Position, DataNode Source)> Merges { get; } = [];
        public string? PendingKey { get; set; }
        public bool ExpectingKey { get; set; } = true;
        public long Size { get; set; } = 1;
    }

    public static DataNode Parse(string text)
    {
        var parser = new Parser(new StringReader(text));
        var anchors = new Dictionary<string, (DataNode Node, long Size)>(StringComparer.Ordinal);
        var stack = new Stack<Frame>();
        var documents = new List<DataNode>();
        DataNode? document = null;

        void Deliver(DataNode node, long size)
        {
            if (stack.Count == 0)
            {
                document = node;
                return;
            }
            var top = stack.Peek();
            top.Size += size;
            if (top.Size > MaxNodes) throw new YamlException($"The file expands to more than {MaxNodes:N0} values (too many aliases).");
            if (!top.Mapping)
            {
                top.Items.Add(node);
                return;
            }
            if (top.ExpectingKey)
            {
                top.PendingKey = node is DataValue v ? v.Text ?? "null" : JsonFormat.WriteLines([node]).TrimEnd('\n');
                top.ExpectingKey = false;
                return;
            }
            if (top.PendingKey == "<<" && node is DataObject or DataArray) top.Merges.Add((top.Entries.Count, node));
            else top.Entries.Add(new(top.PendingKey!, node));
            top.PendingKey = null;
            top.ExpectingKey = true;
        }

        while (Next(parser))
        {
            switch (parser.Current)
            {
                case Scalar scalar:
                    var value = Resolve(scalar);
                    if (!scalar.Anchor.IsEmpty) anchors[scalar.Anchor.Value] = (value, 1);
                    Deliver(value, 1);
                    break;
                case MappingStart start:
                    stack.Push(new Frame(true, start.Anchor));
                    if (stack.Count > MaxDepth) throw new YamlException($"Values are nested more than {MaxDepth} levels deep.");
                    break;
                case SequenceStart start:
                    stack.Push(new Frame(false, start.Anchor));
                    if (stack.Count > MaxDepth) throw new YamlException($"Values are nested more than {MaxDepth} levels deep.");
                    break;
                case MappingEnd or SequenceEnd:
                    var frame = stack.Pop();
                    DataNode built = frame.Mapping ? BuildObject(frame) : new DataArray(frame.Items);
                    if (!frame.Anchor.IsEmpty) anchors[frame.Anchor.Value] = (built, frame.Size);
                    Deliver(built, frame.Size);
                    break;
                case AnchorAlias alias:
                    if (!anchors.TryGetValue(alias.Value.Value, out var target)) throw new YamlException($"Unknown alias *{alias.Value.Value}.");
                    Deliver(target.Node, target.Size);
                    break;
                case DocumentEnd:
                    documents.Add(document ?? DataValue.Null);
                    document = null;
                    break;
            }
        }

        return documents.Count switch
        {
            0 => DataValue.Null,
            1 => documents[0],
            _ => new DataArray(documents),
        };
    }

    // YamlDotNet reports some malformed input as InvalidOperationException; it is still just a bad file.
    private static bool Next(Parser parser)
    {
        try
        {
            return parser.MoveNext();
        }
        catch (InvalidOperationException ex)
        {
            throw new YamlException(ex.Message);
        }
    }

    // "<<: *base" (or a list of them) adds the keys the mapping does not set itself, at the place the merge key was written.
    private static DataObject BuildObject(Frame frame)
    {
        var obj = new DataObject();
        var own = frame.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var merges = frame.Merges.ToLookup(m => m.Position, m => m.Source);
        for (var i = 0; i <= frame.Entries.Count; i++)
        {
            foreach (var source in merges[i])
                foreach (var part in source is DataArray list ? list.Items.OfType<DataObject>() : [(DataObject)source])
                    foreach (var (key, value) in part.Entries)
                        if (!own.Contains(key) && !obj.Contains(key)) obj.Set(key, value);
            if (i < frame.Entries.Count) obj.Set(frame.Entries[i].Key, frame.Entries[i].Value);
        }
        return obj;
    }

    private static DataValue Resolve(Scalar scalar)
    {
        var text = scalar.Value;
        if (!scalar.Tag.IsEmpty)
        {
            var tag = scalar.Tag.Value;
            if (tag.EndsWith(":str", StringComparison.Ordinal)) return DataValue.String(text);
            if (tag.EndsWith(":null", StringComparison.Ordinal)) return DataValue.Null;
            if (tag.EndsWith(":bool", StringComparison.Ordinal) || tag.EndsWith(":int", StringComparison.Ordinal) || tag.EndsWith(":float", StringComparison.Ordinal))
                return ResolvePlain(text);
            return DataValue.String(text);
        }
        return scalar.Style == ScalarStyle.Plain ? ResolvePlain(text) : DataValue.String(text);
    }

    /// <summary>What a plain (unquoted) scalar means under the core schema.</summary>
    internal static DataValue ResolvePlain(string text)
    {
        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL": return DataValue.Null;
            case "true" or "True" or "TRUE": return DataValue.True;
            case "false" or "False" or "FALSE": return DataValue.False;
        }

        if (DecimalInt().IsMatch(text))
        {
            var digits = text.TrimStart('+', '-');
            if (digits.Length > 1 && digits[0] == '0') return DataValue.String(text); // 0123: a code, keep every digit
            return DataValue.Number(text.TrimStart('+'));
        }
        if (text.StartsWith("0x", StringComparison.Ordinal) && HexInt().IsMatch(text[2..])
            && long.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && hex >= 0)
            return DataValue.Number(hex);
        if (text.StartsWith("0o", StringComparison.Ordinal) && OctalInt().IsMatch(text[2..]))
        {
            try { return DataValue.Number(Convert.ToInt64(text[2..], 8)); }
            catch (OverflowException) { return DataValue.String(text); }
        }
        if (Float().IsMatch(text)) return DataValue.Number(JsonNumberText(text));
        return DataValue.String(text);
    }

    // "+.5" -> "0.5", "1." -> "1.0", "-2.e3" -> "-2.0e3": the same number in JSON syntax (DataValue keeps it as text otherwise).
    private static string JsonNumberText(string text)
    {
        var sign = text.StartsWith('-') ? "-" : "";
        var body = text.TrimStart('+', '-');
        var e = body.IndexOfAny(['e', 'E']);
        var mantissa = e < 0 ? body : body[..e];
        var exponent = e < 0 ? "" : body[e..];
        if (mantissa.StartsWith('.')) mantissa = "0" + mantissa;
        if (mantissa.EndsWith('.')) mantissa += "0";
        return sign + mantissa + exponent;
    }

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex DecimalInt();

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex HexInt();

    [GeneratedRegex("^[0-7]+$")]
    private static partial Regex OctalInt();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    // ---- writing --------------------------------------------------------------------------------------------------------

    public static string Write(DataNode node)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
        {
            var emitter = new Emitter(writer, 2, int.MaxValue, false);
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart());
            Emit(emitter, node);
            emitter.Emit(new DocumentEnd(true));
            emitter.Emit(new StreamEnd());
        }
        return sb.ToString();
    }

    private static void Emit(IEmitter emitter, DataNode node)
    {
        switch (node)
        {
            case DataObject obj:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, obj.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in obj.Entries)
                {
                    EmitString(emitter, key);
                    Emit(emitter, value);
                }
                emitter.Emit(new MappingEnd());
                break;
            case DataArray array:
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, array.Items.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in array.Items) Emit(emitter, item);
                emitter.Emit(new SequenceEnd());
                break;
            case DataValue { Kind: ValueKind.Null }:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, "null", ScalarStyle.Plain, true, false));
                break;
            case DataValue { Kind: ValueKind.Bool or ValueKind.Number } plain:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, plain.Text!, ScalarStyle.Plain, true, false));
                break;
            case DataValue text:
                EmitString(emitter, text.Text ?? "");
                break;
        }
    }

    // A string that would read back as something else (true, 12, null, "", 0123 is fine but quoted anyway) is double-quoted; text with
    // line breaks uses a literal block; anything else lets the emitter choose (it quotes when a plain scalar is not allowed).
    private static void EmitString(IEmitter emitter, string text)
    {
        var style = ResolvePlain(text) is not { Kind: ValueKind.String } || LooksSpecial(text)
            ? ScalarStyle.DoubleQuoted
            : text.Contains('\n') ? ScalarStyle.Literal
            : ScalarStyle.Any;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, text, style, true, true));
    }

    // YAML 1.1 readers (still common) take these as booleans or numbers; quoting them costs nothing.
    private static bool LooksSpecial(string text) =>
        text.Length == 0 || text is "y" or "Y" or "n" or "N" or "yes" or "Yes" or "YES" or "no" or "No" or "NO" or "on" or "On" or "ON" or "off" or "Off" or "OFF"
        || text.StartsWith(".inf", StringComparison.OrdinalIgnoreCase) || text.StartsWith("-.inf", StringComparison.OrdinalIgnoreCase)
        || text.Equals(".nan", StringComparison.OrdinalIgnoreCase) || Float().IsMatch(text) || (text.Length > 1 && DecimalInt().IsMatch(text));
}
