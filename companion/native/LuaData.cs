using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AurifyClient
{
    // A SavedVariables reader, never a Lua interpreter. Every accepted expression is data.
    internal static class LuaData
    {
        public static Dictionary<string, object> Parse(string source)
        {
            if (source == null || Encoding.UTF8.GetByteCount(source) > 32 * 1024 * 1024)
                throw new FormatException("SavedVariables file exceeds 32 MiB");
            return new Parser(source).Parse();
        }

        // JavaScript Object.entries visits array-index keys first, then insertion-ordered keys.
        internal static IEnumerable<KeyValuePair<string, object>> Entries(Dictionary<string, object> map)
        {
            var indices = new List<KeyValuePair<uint, KeyValuePair<string, object>>>();
            foreach (var pair in map)
            {
                uint index;
                if (UInt32.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index != UInt32.MaxValue && index.ToString(CultureInfo.InvariantCulture) == pair.Key)
                    indices.Add(new KeyValuePair<uint, KeyValuePair<string, object>>(index, pair));
            }
            indices.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var pair in indices) yield return pair.Value;
            foreach (var pair in map)
            {
                uint index;
                if (!(UInt32.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index != UInt32.MaxValue && index.ToString(CultureInfo.InvariantCulture) == pair.Key)) yield return pair;
            }
        }

        private sealed class Parser
        {
            private readonly string text;
            private int position;
            private int nodes;
            private static readonly HashSet<string> Reserved = new HashSet<string>(new[] { "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while" }, StringComparer.Ordinal);
            internal Parser(string source) { text = source; }
            private FormatException Error() { return new FormatException("SavedVariables may contain bounded data assignments only (offset " + position.ToString(CultureInfo.InvariantCulture) + ")"); }
            private void Node() { if (++nodes > 600000) throw new FormatException("SavedVariables node limit exceeded"); }
            private char Current { get { return position < text.Length ? text[position] : '\0'; } }
            private bool At(string token) { return position + token.Length <= text.Length && String.CompareOrdinal(text, position, token, 0, token.Length) == 0; }
            private static bool Letter(char c) { return c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_'; }
            private static bool Digit(char c) { return c >= '0' && c <= '9'; }
            private static bool Space(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\v' || c == '\f'; }
            private int LongOpen()
            {
                if (Current != '[') return -1;
                int end = position + 1;
                while (end < text.Length && text[end] == '=') end++;
                return end < text.Length && text[end] == '[' ? end - position - 1 : -1;
            }
            private string LongString(bool retain)
            {
                int equals = LongOpen();
                if (equals < 0) throw Error();
                position += equals + 2;
                int start = position;
                string close = "]" + new string('=', equals) + "]";
                int end = text.IndexOf(close, position, StringComparison.Ordinal);
                if (end < 0) throw Error();
                position = end + close.Length;
                if (!retain) return null;
                if (start < end && text[start] == '\n') start++;
                else if (start + 1 < end && text[start] == '\r' && text[start + 1] == '\n') start += 2;
                return text.Substring(start, end - start);
            }
            private void Skip()
            {
                for (;;)
                {
                    while (position < text.Length && Space(Current)) position++;
                    if (!At("--")) return;
                    position += 2;
                    if (LongOpen() >= 0) LongString(false);
                    else while (position < text.Length && Current != '\r' && Current != '\n') position++;
                }
            }
            private bool Take(char c) { Skip(); if (Current != c || position >= text.Length) return false; position++; return true; }
            private void Need(char c) { if (!Take(c)) throw Error(); }
            private string Identifier(bool allowReserved)
            {
                Skip();
                if (!Letter(Current)) throw Error();
                int start = position++;
                while (Letter(Current) || Digit(Current)) position++;
                string result = text.Substring(start, position - start);
                if (!allowReserved && Reserved.Contains(result)) throw Error();
                return result;
            }
            internal Dictionary<string, object> Parse()
            {
                var result = Json.New();
                if (At("#!")) while (position < text.Length && Current != '\n' && Current != '\r') position++;
                Skip();
                while (position < text.Length)
                {
                    var names = new List<string>();
                    do { names.Add(Identifier(false)); Node(); } while (Take(','));
                    Need('=');
                    var values = new List<object>();
                    do { values.Add(Value(0, 0).Data); } while (Take(','));
                    if (names.Count != values.Count) throw Error();
                    Node();
                    for (int i = 0; i < names.Count; i++) result[names[i]] = values[i];
                    Take(';');
                    Skip();
                }
                Node();
                return result;
            }
            private struct Datum
            {
                internal object Data;
                internal bool NumericLiteral;
                internal Datum(object data, bool numeric) { Data = data; NumericLiteral = numeric; }
            }
            private Datum Value(int depth, int grouping)
            {
                if (depth > 64 || grouping > 128) throw new FormatException("SavedVariables nesting limit exceeded");
                Skip();
                if (Take('(')) { var inner = Value(depth, grouping + 1); Need(')'); return inner; }
                if (Take('-'))
                {
                    var argument = Value(depth, grouping + 1);
                    if (!argument.NumericLiteral) throw Error();
                    Node();
                    return new Datum(-(double)argument.Data, false);
                }
                if (Current == '{') return new Datum(Table(depth, grouping), false);
                if (Current == '\'' || Current == '"') { Node(); return new Datum(Quoted(), false); }
                if (LongOpen() >= 0) { Node(); return new Datum(LongString(true), false); }
                if (Digit(Current) || Current == '.' && position + 1 < text.Length && Digit(text[position + 1]))
                { Node(); return new Datum(Number(), true); }
                string keyword = Identifier(true);
                Node();
                if (keyword == "true") return new Datum(true, false);
                if (keyword == "false") return new Datum(false, false);
                if (keyword == "nil") return new Datum(null, false);
                throw Error();
            }
            private Dictionary<string, object> Table(int depth, int grouping)
            {
                Need('{'); Node();
                var result = Json.New();
                int index = 1;
                Skip();
                while (!Take('}'))
                {
                    string key;
                    if (Current == '[' && LongOpen() < 0)
                    {
                        position++;
                        var value = Value(depth + 1, grouping).Data;
                        if (value is string) key = (string)value;
                        else if (value is double) key = NumericKey((double)value);
                        else throw Error();
                        Need(']'); Need('=');
                    }
                    else
                    {
                        int saved = position;
                        if (Letter(Current))
                        {
                            string name = Identifier(true);
                            if (Take('='))
                            {
                                if (Reserved.Contains(name)) throw Error();
                                Node(); key = name;
                            }
                            else { position = saved; key = (index++).ToString(CultureInfo.InvariantCulture); }
                        }
                        else key = (index++).ToString(CultureInfo.InvariantCulture);
                    }
                    result[key] = Value(depth + 1, grouping).Data;
                    Node();
                    if (Take(',') || Take(';')) continue;
                    Need('}');
                    break;
                }
                return result;
            }
            private static string NumericKey(double value)
            {
                return value == 0 ? "0" : Json.Stringify(value);
            }
            private double Number()
            {
                int start = position;
                double value;
                if (At("0x") || At("0X"))
                {
                    position += 2;
                    int count = 0;
                    value = 0;
                    while (position < text.Length)
                    {
                        char c = Current;
                        int digit = Digit(c) ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                        if (digit < 0) break;
                        value = value * 16 + digit; position++; count++;
                    }
                    if (count == 0) throw Error();
                }
                else
                {
                    while (Digit(Current)) position++;
                    if (Current == '.') { position++; while (Digit(Current)) position++; }
                    if (Current == 'e' || Current == 'E')
                    {
                        position++;
                        if (Current == '+' || Current == '-') position++;
                        int digits = position;
                        while (Digit(Current)) position++;
                        if (position == digits) throw Error();
                    }
                    if (!Double.TryParse(text.Substring(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw Error();
                }
                if (Double.IsInfinity(value) || Double.IsNaN(value) || Letter(Current) || Current == '.') throw Error();
                return value;
            }
            private string Quoted()
            {
                char quote = text[position++];
                var plain = new StringBuilder();
                using (var bytes = new MemoryStream())
                {
                    while (position < text.Length)
                    {
                        char c = text[position++];
                        if (c == quote)
                        {
                            Flush(plain, bytes);
                            return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
                        }
                        if (c == '\r' || c == '\n') throw Error();
                        if (c != '\\') { plain.Append(c); continue; }
                        if (position == text.Length) throw Error();
                        c = text[position++];
                        if (Digit(c))
                        {
                            int value = c - '0'; int count = 1;
                            while (count < 3 && Digit(Current)) { value = value * 10 + text[position++] - '0'; count++; }
                            if (value > 255) throw Error();
                            Flush(plain, bytes); bytes.WriteByte((byte)value);
                        }
                        else
                        {
                            int escaped = "abfnrtv".IndexOf(c);
                            if (escaped >= 0)
                            { Flush(plain, bytes); bytes.WriteByte((byte)"\a\b\f\n\r\t\v"[escaped]); }
                            else if (c == '\r' || c == '\n')
                            { if (c == '\r' && Current == '\n') position++; plain.Append('\n'); }
                            else if (c == '\\' || c == '\'' || c == '"') plain.Append(c);
                            else throw Error();
                        }
                    }
                }
                throw Error();
            }
            private static void Flush(StringBuilder plain, Stream bytes)
            {
                if (plain.Length == 0) return;
                byte[] encoded = Encoding.UTF8.GetBytes(plain.ToString());
                bytes.Write(encoded, 0, encoded.Length); plain.Length = 0;
            }
        }
    }
}
