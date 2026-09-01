using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimRelay.Core.Json
{
    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message, int position)
            : base(message + " at offset " + position.ToString(CultureInfo.InvariantCulture))
        {
            Position = position;
        }

        public int Position { get; }
    }

    /// <summary>
    /// Recursive-descent JSON parser sized for §3 frames. Depth is capped so a
    /// hostile or corrupt frame cannot blow the stack — every inbound frame has
    /// crossed the network and a relay that never inspects payloads (§1) is not
    /// going to filter this for us.
    /// </summary>
    public static class JsonParser
    {
        public const int MaxDepth = 24;

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var index = 0;
            var value = ParseValue(text, ref index, 0);
            SkipWhitespace(text, ref index);
            if (index != text.Length) throw new JsonParseException("trailing content", index);
            return value;
        }

        /// <summary>Parses, returning false instead of throwing. The inbound path uses this.</summary>
        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (JsonParseException)
            {
                value = JsonValue.Null;
                return false;
            }
            catch (ArgumentNullException)
            {
                value = JsonValue.Null;
                return false;
            }
        }

        private static JsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new JsonParseException("nesting too deep", i);
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new JsonParseException("unexpected end of input", i);

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i, depth);
                case '[': return ParseArray(s, ref i, depth);
                case '"': return JsonValue.String(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return JsonValue.Bool(true);
                case 'f': Expect(s, ref i, "false"); return JsonValue.Bool(false);
                case 'n': Expect(s, ref i, "null"); return JsonValue.Null;
                default: return JsonValue.Number(ParseNumber(s, ref i));
            }
        }

        private static JsonValue ParseObject(string s, ref int i, int depth)
        {
            i++; // '{'
            var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return JsonValue.Object(fields); }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new JsonParseException("expected object key", i);
                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new JsonParseException("expected ':'", i);
                i++;
                // Last duplicate key wins, matching JavaScript's behaviour so the
                // mod and a browser map never disagree about a malformed frame.
                fields[key] = ParseValue(s, ref i, depth + 1);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new JsonParseException("unterminated object", i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return JsonValue.Object(fields); }
                throw new JsonParseException("expected ',' or '}'", i);
            }
        }

        private static JsonValue ParseArray(string s, ref int i, int depth)
        {
            i++; // '['
            var items = new List<JsonValue>();
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return JsonValue.Array(items); }

            while (true)
            {
                items.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new JsonParseException("unterminated array", i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return JsonValue.Array(items); }
                throw new JsonParseException("expected ',' or ']'", i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new JsonParseException("unterminated string", i);
                var c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c != '\\') { sb.Append(c); i++; continue; }

                i++;
                if (i >= s.Length) throw new JsonParseException("unterminated escape", i);
                var e = s[i++];
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
                        if (i + 4 > s.Length) throw new JsonParseException("truncated \\u escape", i);
                        var hex = s.Substring(i, 4);
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                            throw new JsonParseException("bad \\u escape", i);
                        i += 4;
                        sb.Append((char)cp);
                        break;
                    default:
                        throw new JsonParseException("unknown escape '" + e + "'", i);
                }
            }
        }

        private static double ParseNumber(string s, ref int i)
        {
            var start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                    ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
            {
                i++;
            }

            var text = s.Substring(start, i - start);
            if (text.Length == 0 ||
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new JsonParseException("invalid number '" + text + "'", start);
            }

            return value;
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new JsonParseException("expected '" + literal + "'", i);
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }
    }
}
