// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Engines.Fonts.Sfnt;

namespace DropFlow.Engines.Fonts.Outlines;

/// <summary>The Private DICT values a charstring needs: local subroutines and the two width defaults.</summary>
internal sealed record CffPrivate(byte[][] LocalSubrs, double DefaultWidthX, double NominalWidthX);

/// <summary>
/// A parsed 'CFF ' table (Adobe Technical Note #5176, "The Compact Font Format Specification"): charstrings, global and local subroutines,
/// and for CID-keyed fonts the Font DICT of each glyph. Only what drawing the outlines needs is read.
/// </summary>
internal sealed class CffFont
{
    public required byte[][] CharStrings { get; init; }
    public required byte[][] GlobalSubrs { get; init; }
    public required CffPrivate[] Privates { get; init; }
    /// <summary>Font DICT index per glyph (all zero for a name-keyed font).</summary>
    public required byte[] FdSelect { get; init; }
    public int CharstringType { get; init; } = 2;

    public static CffFont Parse(byte[] data)
    {
        if (data.Length < 4) throw new FontFailure("The font's CFF table is too short.");
        if (data[0] != 1) throw new FontFailure($"CFF version {data[0]} is not supported.");
        var position = (int)data[2];
        ReadIndex(data, ref position); // Name INDEX
        var topDicts = ReadIndex(data, ref position);
        ReadIndex(data, ref position); // String INDEX: names are not needed to draw
        var globalSubrs = ReadIndex(data, ref position);
        if (topDicts.Length == 0) throw new FontFailure("The font's CFF table has no Top DICT.");

        var top = CffDict.Parse(topDicts[0]);
        if (!top.TryGetValue(17, out var charStringsOffset)) throw new FontFailure("The font's CFF table has no charstrings.");
        var charStringsPosition = Offset(charStringsOffset[0], data);
        var charStrings = ReadIndex(data, ref charStringsPosition);
        var type = top.TryGetValue(1206, out var t) ? (int)t[0] : 2;

        CffPrivate[] privates;
        var fdSelect = new byte[charStrings.Length];
        if (top.ContainsKey(1230)) // ROS: a CID-keyed font
        {
            if (!top.TryGetValue(1236, out var fdArrayOffset) || !top.TryGetValue(1237, out var fdSelectOffset))
                throw new FontFailure("The CID-keyed CFF font has no FDArray or FDSelect.");
            var fdPosition = Offset(fdArrayOffset[0], data);
            var fontDicts = ReadIndex(data, ref fdPosition);
            privates = fontDicts.Select(d => ReadPrivate(data, CffDict.Parse(d))).ToArray();
            ReadFdSelect(data, Offset(fdSelectOffset[0], data), fdSelect, privates.Length);
        }
        else
        {
            privates = [ReadPrivate(data, top)];
        }

        return new CffFont { CharStrings = charStrings, GlobalSubrs = globalSubrs, Privates = privates, FdSelect = fdSelect, CharstringType = type };
    }

    private static CffPrivate ReadPrivate(byte[] data, Dictionary<int, List<double>> dict)
    {
        if (!dict.TryGetValue(18, out var p) || p.Count < 2) return new CffPrivate([], 0, 0);
        var size = (int)p[0];
        var offset = Offset(p[1], data);
        if (size < 0 || offset + size > data.Length) throw new FontFailure("The font's CFF Private DICT is outside the table.");
        var priv = CffDict.Parse(data.AsSpan(offset, size).ToArray());
        byte[][] subrs = [];
        if (priv.TryGetValue(19, out var subrsOffset))
        {
            var position = offset + (int)subrsOffset[0];
            if (position < 0 || position >= data.Length) throw new FontFailure("The font's CFF local subroutines are outside the table.");
            subrs = ReadIndex(data, ref position);
        }
        return new CffPrivate(subrs, priv.TryGetValue(20, out var dw) ? dw[0] : 0, priv.TryGetValue(21, out var nw) ? nw[0] : 0);
    }

    private static void ReadFdSelect(byte[] data, int position, byte[] fdSelect, int fdCount)
    {
        var reader = new SpanReader(data, position);
        var format = reader.U8();
        if (format == 0)
        {
            for (var i = 0; i < fdSelect.Length; i++) fdSelect[i] = reader.U8();
        }
        else if (format == 3)
        {
            int ranges = reader.U16();
            int first = reader.U16();
            for (var r = 0; r < ranges; r++)
            {
                var fd = reader.U8();
                int next = reader.U16();
                for (var g = first; g < next && g < fdSelect.Length; g++) fdSelect[g] = fd;
                first = next;
            }
        }
        else
        {
            throw new FontFailure($"CFF FDSelect format {format} is not supported.");
        }
        if (fdSelect.Any(fd => fd >= fdCount)) throw new FontFailure("The CFF FDSelect refers to a missing Font DICT.");
    }

    private static int Offset(double value, byte[] data)
    {
        var offset = (int)value;
        if (offset < 0 || offset >= data.Length) throw new FontFailure("The font's CFF table refers outside itself.");
        return offset;
    }

    /// <summary>An INDEX: count, offset size, count + 1 offsets (1-based), then the data.</summary>
    internal static byte[][] ReadIndex(byte[] data, ref int position)
    {
        var reader = new SpanReader(data, position);
        int count = reader.U16();
        if (count == 0)
        {
            position = reader.Position;
            return [];
        }
        var offSize = reader.U8();
        if (offSize is < 1 or > 4) throw new FontFailure("The font's CFF table has a damaged INDEX.");
        var offsets = new long[count + 1];
        for (var i = 0; i <= count; i++)
        {
            long v = 0;
            for (var k = 0; k < offSize; k++) v = (v << 8) | reader.U8();
            offsets[i] = v;
        }
        var dataStart = reader.Position - 1;
        var items = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var start = dataStart + offsets[i];
            var end = dataStart + offsets[i + 1];
            if (end < start || end > data.Length) throw new FontFailure("The font's CFF table has a damaged INDEX.");
            items[i] = data.AsSpan((int)start, (int)(end - start)).ToArray();
        }
        position = (int)(dataStart + offsets[count]);
        return items;
    }

    /// <summary>The outlines and advance width of glyph <paramref name="glyph"/>.</summary>
    public (List<CubicContour> Contours, double Width) Draw(int glyph)
    {
        if (CharstringType != 2) throw new FontFailure($"CFF charstring type {CharstringType} is not supported.");
        var priv = Privates[FdSelect[glyph]];
        var interpreter = new Type2Charstring(GlobalSubrs, priv);
        interpreter.Run(CharStrings[glyph]);
        return (interpreter.Contours, interpreter.Width ?? priv.DefaultWidthX);
    }
}

/// <summary>DICT data: operands (integers and reals) followed by one- or two-byte operators. Two-byte operators are stored as 1200 + the second byte.</summary>
internal static class CffDict
{
    public static Dictionary<int, List<double>> Parse(byte[] data)
    {
        var result = new Dictionary<int, List<double>>();
        var operands = new List<double>();
        var i = 0;
        while (i < data.Length)
        {
            int b0 = data[i++];
            if (b0 <= 21)
            {
                var op = b0;
                if (b0 == 12)
                {
                    if (i >= data.Length) throw new FontFailure("A CFF DICT is cut short.");
                    op = 1200 + data[i++];
                }
                result[op] = operands;
                operands = [];
            }
            else if (b0 == 28)
            {
                Need(data, i, 2);
                operands.Add((short)((data[i] << 8) | data[i + 1]));
                i += 2;
            }
            else if (b0 == 29)
            {
                Need(data, i, 4);
                operands.Add((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
                i += 4;
            }
            else if (b0 == 30)
            {
                operands.Add(ReadReal(data, ref i));
            }
            else if (b0 is >= 32 and <= 246)
            {
                operands.Add(b0 - 139);
            }
            else if (b0 is >= 247 and <= 250)
            {
                Need(data, i, 1);
                operands.Add((b0 - 247) * 256 + data[i++] + 108);
            }
            else if (b0 is >= 251 and <= 254)
            {
                Need(data, i, 1);
                operands.Add(-(b0 - 251) * 256 - data[i++] - 108);
            }
            else
            {
                throw new FontFailure("A CFF DICT holds a reserved byte.");
            }
        }
        return result;
    }

    private static double ReadReal(byte[] data, ref int i)
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            Need(data, i, 1);
            var b = data[i++];
            foreach (var nibble in new[] { b >> 4, b & 0x0F })
            {
                switch (nibble)
                {
                    case <= 9: text.Append((char)('0' + nibble)); break;
                    case 0xA: text.Append('.'); break;
                    case 0xB: text.Append('E'); break;
                    case 0xC: text.Append("E-"); break;
                    case 0xE: text.Append('-'); break;
                    case 0xF:
                        return double.TryParse(text.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
                }
            }
        }
    }

    private static void Need(byte[] data, int i, int count)
    {
        if (i + count > data.Length) throw new FontFailure("A CFF DICT is cut short.");
    }
}

/// <summary>
/// The Type 2 charstring interpreter (Adobe Technical Note #5177): path operators, hints (counted, not used), subroutines, flex, and the
/// arithmetic operators. Produces cubic contours and the advance width.
/// </summary>
internal sealed class Type2Charstring(byte[][] globalSubrs, CffPrivate priv)
{
    private const int MaxStack = 513;
    private readonly double[] _stack = new double[MaxStack];
    private readonly double[] _transient = new double[32];
    private int _count;
    private int _stems;
    private bool _widthKnown;
    private bool _ended;
    private Pt _current;
    private CubicContour? _contour;

    public List<CubicContour> Contours { get; } = [];
    public double? Width { get; private set; }

    public void Run(byte[] code)
    {
        Execute(code, 0);
        Close();
    }

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private void Execute(byte[] code, int depth)
    {
        if (depth > 10) throw new FontFailure("A CFF charstring nests subroutines too deeply.");
        var i = 0;
        while (i < code.Length && !_ended)
        {
            int b0 = code[i++];
            if (b0 >= 32 || b0 == 28)
            {
                double value;
                if (b0 == 28) { Need(code, i, 2); value = (short)((code[i] << 8) | code[i + 1]); i += 2; }
                else if (b0 <= 246) value = b0 - 139;
                else if (b0 <= 250) { Need(code, i, 1); value = (b0 - 247) * 256 + code[i++] + 108; }
                else if (b0 <= 254) { Need(code, i, 1); value = -(b0 - 251) * 256 - code[i++] - 108; }
                else { Need(code, i, 4); value = ((code[i] << 24) | (code[i + 1] << 16) | (code[i + 2] << 8) | code[i + 3]) / 65536.0; i += 4; }
                Push(value);
                continue;
            }

            switch (b0)
            {
                case 1 or 3 or 18 or 23: // hstem, vstem, hstemhm, vstemhm
                    TakeWidth(_count % 2 == 1);
                    _stems += _count / 2;
                    _count = 0;
                    break;
                case 19 or 20: // hintmask, cntrmask (operands here are implied vstems)
                    TakeWidth(_count % 2 == 1);
                    _stems += _count / 2;
                    _count = 0;
                    i += (_stems + 7) / 8;
                    break;
                case 21: // rmoveto
                    TakeWidth(_count > 2);
                    MoveTo(Arg(0), Arg(1));
                    break;
                case 22: // hmoveto
                    TakeWidth(_count > 1);
                    MoveTo(Arg(0), 0);
                    break;
                case 4: // vmoveto
                    TakeWidth(_count > 1);
                    MoveTo(0, Arg(0));
                    break;
                case 5: // rlineto
                    for (var k = 0; k + 1 < _count; k += 2) LineTo(_stack[k], _stack[k + 1]);
                    _count = 0;
                    break;
                case 6 or 7: // hlineto, vlineto
                {
                    var horizontal = b0 == 6;
                    for (var k = 0; k < _count; k++, horizontal = !horizontal)
                        if (horizontal) LineTo(_stack[k], 0); else LineTo(0, _stack[k]);
                    _count = 0;
                    break;
                }
                case 8: // rrcurveto
                    for (var k = 0; k + 5 < _count; k += 6) CurveTo(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    _count = 0;
                    break;
                case 24: // rcurveline
                {
                    var k = 0;
                    for (; k + 7 < _count; k += 6) CurveTo(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    if (k + 1 < _count) LineTo(_stack[k], _stack[k + 1]);
                    _count = 0;
                    break;
                }
                case 25: // rlinecurve
                {
                    var k = 0;
                    for (; k + 7 < _count; k += 2) LineTo(_stack[k], _stack[k + 1]);
                    if (k + 5 < _count) CurveTo(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    _count = 0;
                    break;
                }
                case 26: // vvcurveto
                {
                    var k = 0;
                    var dx1 = 0.0;
                    if (_count % 2 == 1) dx1 = _stack[k++];
                    for (; k + 3 < _count; k += 4)
                    {
                        CurveTo(dx1, _stack[k], _stack[k + 1], _stack[k + 2], 0, _stack[k + 3]);
                        dx1 = 0;
                    }
                    _count = 0;
                    break;
                }
                case 27: // hhcurveto
                {
                    var k = 0;
                    var dy1 = 0.0;
                    if (_count % 2 == 1) dy1 = _stack[k++];
                    for (; k + 3 < _count; k += 4)
                    {
                        CurveTo(_stack[k], dy1, _stack[k + 1], _stack[k + 2], _stack[k + 3], 0);
                        dy1 = 0;
                    }
                    _count = 0;
                    break;
                }
                case 30 or 31: // vhcurveto, hvcurveto
                {
                    var horizontal = b0 == 31;
                    var k = 0;
                    while (k + 3 < _count)
                    {
                        var last = _count - k == 5 ? _stack[k + 4] : 0;
                        if (horizontal) CurveTo(_stack[k], 0, _stack[k + 1], _stack[k + 2], last, _stack[k + 3]);
                        else CurveTo(0, _stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], last);
                        k += _count - k == 5 ? 5 : 4;
                        horizontal = !horizontal;
                    }
                    _count = 0;
                    break;
                }
                case 10 or 29: // callsubr, callgsubr
                {
                    var subrs = b0 == 10 ? priv.LocalSubrs : globalSubrs;
                    if (_count < 1) throw new FontFailure("A CFF charstring calls a subroutine without an index.");
                    var index = (int)_stack[--_count] + Bias(subrs.Length);
                    if (index < 0 || index >= subrs.Length) throw new FontFailure("A CFF charstring calls a missing subroutine.");
                    Execute(subrs[index], depth + 1);
                    break;
                }
                case 11: // return
                    return;
                case 14: // endchar
                    TakeWidth(_count is 1 or 5);
                    if (_count >= 4) throw new FontFailure("The font uses the old CFF accent operator (seac), which is not supported.");
                    _count = 0;
                    _ended = true;
                    return;
                case 12:
                    Need(code, i, 1);
                    Escape(code[i++]);
                    break;
                default:
                    throw new FontFailure($"A CFF charstring uses the reserved operator {b0}.");
            }
        }
    }

    private void Escape(int op)
    {
        switch (op)
        {
            case 35: // flex
                Require(13);
                CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                _count = 0;
                break;
            case 34: // hflex
                Require(7);
                CurveTo(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                CurveTo(_stack[4], 0, _stack[5], -_stack[2], _stack[6], 0);
                _count = 0;
                break;
            case 36: // hflex1
                Require(9);
                CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                CurveTo(_stack[5], 0, _stack[6], _stack[7], _stack[8], -(_stack[1] + _stack[3] + _stack[7]));
                _count = 0;
                break;
            case 37: // flex1
            {
                Require(11);
                double dx = 0, dy = 0;
                for (var k = 0; k < 10; k += 2) { dx += _stack[k]; dy += _stack[k + 1]; }
                CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                if (Math.Abs(dx) > Math.Abs(dy)) CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], -dy);
                else CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], -dx, _stack[10]);
                _count = 0;
                break;
            }
            case 3: Binary((a, b) => a != 0 && b != 0 ? 1 : 0); break; // and
            case 4: Binary((a, b) => a != 0 || b != 0 ? 1 : 0); break; // or
            case 5: Unary(a => a == 0 ? 1 : 0); break; // not
            case 9: Unary(Math.Abs); break;
            case 10: Binary((a, b) => a + b); break;
            case 11: Binary((a, b) => a - b); break;
            case 12: Binary((a, b) => b == 0 ? 0 : a / b); break;
            case 14: Unary(a => -a); break;
            case 15: Binary((a, b) => a == b ? 1 : 0); break;
            case 18: Require(1); _count--; break; // drop
            case 20: // put
            {
                Require(2);
                var index = (int)_stack[--_count];
                var value = _stack[--_count];
                if (index is >= 0 and < 32) _transient[index] = value;
                break;
            }
            case 21: // get
            {
                Require(1);
                var index = (int)_stack[_count - 1];
                _stack[_count - 1] = index is >= 0 and < 32 ? _transient[index] : 0;
                break;
            }
            case 22: // ifelse
            {
                Require(4);
                var v2 = _stack[--_count];
                var v1 = _stack[--_count];
                var s2 = _stack[--_count];
                var s1 = _stack[--_count];
                Push(v1 <= v2 ? s1 : s2);
                break;
            }
            case 23: Push(0.5); break; // random: any value in (0, 1]; a fixed one keeps the output reproducible
            case 24: Binary((a, b) => a * b); break;
            case 26: Unary(a => a < 0 ? 0 : Math.Sqrt(a)); break;
            case 27: Require(1); Push(_stack[_count - 1]); break; // dup
            case 28: // exch
            {
                Require(2);
                (_stack[_count - 1], _stack[_count - 2]) = (_stack[_count - 2], _stack[_count - 1]);
                break;
            }
            case 29: // index
            {
                Require(1);
                var n = (int)_stack[_count - 1];
                var source = _count - 2 - Math.Max(0, n);
                _stack[_count - 1] = source >= 0 ? _stack[source] : 0;
                break;
            }
            case 30: // roll
            {
                Require(2);
                var j = (int)_stack[--_count];
                var n = (int)_stack[--_count];
                if (n <= 0 || n > _count) break;
                var items = new double[n];
                Array.Copy(_stack, _count - n, items, 0, n);
                for (var k = 0; k < n; k++) _stack[_count - n + ((k + j) % n + n) % n] = items[k];
                break;
            }
            case 0: _count = 0; break; // dotsection (deprecated, ignored)
            default:
                throw new FontFailure($"A CFF charstring uses the reserved operator 12 {op}.");
        }
    }

    private void Unary(Func<double, double> f)
    {
        Require(1);
        _stack[_count - 1] = f(_stack[_count - 1]);
    }

    private void Binary(Func<double, double, double> f)
    {
        Require(2);
        var b = _stack[--_count];
        _stack[_count - 1] = f(_stack[_count - 1], b);
    }

    private void TakeWidth(bool present)
    {
        if (_widthKnown) return;
        _widthKnown = true;
        if (!present || _count == 0) return;
        Width = priv.NominalWidthX + _stack[0];
        Array.Copy(_stack, 1, _stack, 0, _count - 1);
        _count--;
    }

    private double Arg(int k) => k < _count ? _stack[k] : 0;

    private void MoveTo(double dx, double dy)
    {
        Close();
        _current = new Pt(_current.X + dx, _current.Y + dy);
        _contour = new CubicContour(_current);
        _count = 0;
    }

    private void LineTo(double dx, double dy)
    {
        _contour ??= new CubicContour(_current);
        _current = new Pt(_current.X + dx, _current.Y + dy);
        _contour.Segments.Add(Segment.Line(_current));
    }

    private void CurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        _contour ??= new CubicContour(_current);
        var c1 = new Pt(_current.X + dx1, _current.Y + dy1);
        var c2 = new Pt(c1.X + dx2, c1.Y + dy2);
        _current = new Pt(c2.X + dx3, c2.Y + dy3);
        _contour.Segments.Add(Segment.Curve(c1, c2, _current));
    }

    private void Close()
    {
        if (_contour is { Segments.Count: > 0 }) Contours.Add(_contour);
        _contour = null;
    }

    private void Push(double value)
    {
        if (_count >= MaxStack) throw new FontFailure("A CFF charstring overflows its stack.");
        _stack[_count++] = value;
    }

    private void Require(int count)
    {
        if (_count < count) throw new FontFailure("A CFF charstring has too few operands.");
    }

    private static void Need(byte[] code, int i, int count)
    {
        if (i + count > code.Length) throw new FontFailure("A CFF charstring is cut short.");
    }
}
