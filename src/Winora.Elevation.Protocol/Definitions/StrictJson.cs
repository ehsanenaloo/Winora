// SPDX-License-Identifier: GPL-3.0-only
using System.Text;

namespace Winora.Elevation.Protocol.Definitions;

/// <summary>Raised for any violation of the restricted JSON profile or of the schema; the message names the offending path.</summary>
public sealed class DefinitionFormatException(string message) : Exception(message);

public enum JKind { Object, Array, String, Integer, True, False, Null }

/// <summary>One node of the parsed document. Objects keep their members in file order; duplicate keys never reach this type.</summary>
public sealed class JNode
{
    public JKind Kind { get; }
    public string? Text { get; }
    public long Number { get; }
    public IReadOnlyList<KeyValuePair<string, JNode>> Members { get; } = [];
    public IReadOnlyList<JNode> Items { get; } = [];

    private JNode(JKind kind, string? text, long number, IReadOnlyList<KeyValuePair<string, JNode>>? members, IReadOnlyList<JNode>? items)
    { Kind = kind; Text = text; Number = number; Members = members ?? []; Items = items ?? []; }

    public static JNode Obj(List<KeyValuePair<string, JNode>> m) => new(JKind.Object, null, 0, m, null);
    public static JNode Arr(List<JNode> i) => new(JKind.Array, null, 0, null, i);
    public static JNode Str(string s) => new(JKind.String, s, 0, null, null);
    public static JNode Int(long n) => new(JKind.Integer, null, n, null, null);
    public static JNode Const(JKind k) => new(k, null, 0, null, null);

    public bool TryGet(string name, out JNode value)
    {
        foreach (var m in Members) if (m.Key == name) { value = m.Value; return true; }
        value = this;
        return false;
    }
}

/// <summary>
/// The restricted JSON profile of trust.md 6.1, implemented by hand so the elevated worker needs no general JSON library: UTF-8 without BOM, no comments,
/// no trailing commas, no duplicate keys, printable ASCII keys, depth at most 8, size at most 256 KiB, integers only (no fraction, exponent, leading zero or
/// negative zero), well-formed string escapes. The caller has verified the signature over the same bytes before calling.
/// </summary>
public static class StrictJson
{
    public static JNode Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > DefinitionLimits.MaxBytes) throw new DefinitionFormatException("size out of range");
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) throw new DefinitionFormatException("byte order mark");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (ArgumentException) { throw new DefinitionFormatException("not valid UTF-8"); }
        var p = new Reader(text);
        p.SkipSpace();
        var root = p.Value(1);
        p.SkipSpace();
        if (!p.AtEnd) throw new DefinitionFormatException("data after the document");
        return root;
    }

    private sealed class Reader(string s)
    {
        private int i;
        public bool AtEnd => i >= s.Length;

        public void SkipSpace() { while (i < s.Length && s[i] is ' ' or '\t' or '\r' or '\n') i++; }

        private char Peek() => i < s.Length ? s[i] : throw new DefinitionFormatException("unexpected end");

        private void Expect(char c)
        {
            if (Peek() != c) throw new DefinitionFormatException($"expected '{c}' at {i}");
            i++;
        }

        public JNode Value(int depth)
        {
            if (depth > DefinitionLimits.MaxDepth) throw new DefinitionFormatException("nesting too deep");
            switch (Peek())
            {
                case '{': return ObjectValue(depth);
                case '[': return ArrayValue(depth);
                case '"': return JNode.Str(StringValue());
                case 't': Literal("true"); return JNode.Const(JKind.True);
                case 'f': Literal("false"); return JNode.Const(JKind.False);
                case 'n': Literal("null"); return JNode.Const(JKind.Null);
                default: return IntegerValue();
            }
        }

        private void Literal(string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new DefinitionFormatException($"unexpected token at {i}");
            i += word.Length;
        }

        private JNode ObjectValue(int depth)
        {
            Expect('{');
            var members = new List<KeyValuePair<string, JNode>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            SkipSpace();
            if (Peek() == '}') { i++; return JNode.Obj(members); }
            while (true)
            {
                SkipSpace();
                if (Peek() != '"') throw new DefinitionFormatException($"expected a key at {i}");
                var key = StringValue();
                if (key.Length is 0 or > 64) throw new DefinitionFormatException("key length out of range");
                foreach (var c in key) if (c < 0x21 || c > 0x7E) throw new DefinitionFormatException("key is not printable ASCII");
                if (!seen.Add(key)) throw new DefinitionFormatException($"duplicate key {key}");
                SkipSpace(); Expect(':'); SkipSpace();
                members.Add(new(key, Value(depth + 1)));
                SkipSpace();
                if (Peek() == ',') { i++; continue; }
                Expect('}');
                return JNode.Obj(members);
            }
        }

        private JNode ArrayValue(int depth)
        {
            Expect('[');
            var items = new List<JNode>();
            SkipSpace();
            if (Peek() == ']') { i++; return JNode.Arr(items); }
            while (true)
            {
                SkipSpace();
                items.Add(Value(depth + 1));
                SkipSpace();
                if (Peek() == ',') { i++; continue; }
                Expect(']');
                return JNode.Arr(items);
            }
        }

        private string StringValue()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                var c = Peek(); i++;
                if (c == '"') break;
                if (c < 0x20) throw new DefinitionFormatException("control character in a string");
                if (c != '\\') { sb.Append(c); continue; }
                var e = Peek(); i++;
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new DefinitionFormatException("short \\u escape");
                        var code = 0;
                        for (var k = 0; k < 4; k++)
                        {
                            var h = s[i + k];
                            var d = h is >= '0' and <= '9' ? h - '0' : h is >= 'a' and <= 'f' ? h - 'a' + 10 : h is >= 'A' and <= 'F' ? h - 'A' + 10 : -1;
                            if (d < 0) throw new DefinitionFormatException("bad \\u escape");
                            code = code * 16 + d;
                        }
                        i += 4; sb.Append((char)code);
                        break;
                    default: throw new DefinitionFormatException("unknown escape");
                }
            }
            var result = sb.ToString();
            for (var k = 0; k < result.Length; k++)
            {
                if (char.IsHighSurrogate(result[k])) { if (k + 1 >= result.Length || !char.IsLowSurrogate(result[k + 1])) throw new DefinitionFormatException("string is not well-formed UTF-16"); k++; }
                else if (char.IsLowSurrogate(result[k])) throw new DefinitionFormatException("string is not well-formed UTF-16");
            }
            return result;
        }

        private JNode IntegerValue()
        {
            var start = i;
            if (Peek() == '-') i++;
            if (Peek() is < '0' or > '9') throw new DefinitionFormatException($"unexpected token at {start}");
            if (s[i] == '0')
            {
                i++;
                if (i < s.Length && s[i] is >= '0' and <= '9') throw new DefinitionFormatException("leading zero");
                if (s[start] == '-') throw new DefinitionFormatException("negative zero");
            }
            else while (i < s.Length && s[i] is >= '0' and <= '9') i++;
            if (i < s.Length && s[i] is '.' or 'e' or 'E') throw new DefinitionFormatException("number is not an integer");
            return long.TryParse(s.AsSpan(start, i - start), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? JNode.Int(n) : throw new DefinitionFormatException("number does not fit 64 bits");
        }
    }
}
