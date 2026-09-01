using System;
using System.Globalization;
using System.Text;

namespace ValheimRelay.Core.Json
{
    /// <summary>
    /// Minimal allocation-conscious JSON object writer. Enough for the frames in
    /// PLAN.md §3 and nothing more: objects, arrays, strings, numbers, booleans.
    /// Exists so the plugin takes no Newtonsoft dependency (§4.4).
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb;
        private bool _needComma;

        public JsonWriter(StringBuilder? sb = null)
        {
            _sb = sb ?? new StringBuilder(256);
        }

        public JsonWriter BeginObject()
        {
            Separate();
            _sb.Append('{');
            _needComma = false;
            return this;
        }

        public JsonWriter EndObject()
        {
            _sb.Append('}');
            _needComma = true;
            return this;
        }

        public JsonWriter BeginArray()
        {
            Separate();
            _sb.Append('[');
            _needComma = false;
            return this;
        }

        public JsonWriter EndArray()
        {
            _sb.Append(']');
            _needComma = true;
            return this;
        }

        public JsonWriter Name(string name)
        {
            Separate();
            WriteQuoted(name);
            _sb.Append(':');
            _needComma = false;
            return this;
        }

        public JsonWriter Value(string? value)
        {
            Separate();
            if (value == null) _sb.Append("null");
            else WriteQuoted(value);
            _needComma = true;
            return this;
        }

        public JsonWriter Value(bool value)
        {
            Separate();
            _sb.Append(value ? "true" : "false");
            _needComma = true;
            return this;
        }

        public JsonWriter Value(long value)
        {
            Separate();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

        public JsonWriter Value(int value) => Value((long)value);

        /// <summary>
        /// Doubles are written with at most <paramref name="decimals"/> places.
        /// Position frames are the highest-rate traffic and centimetre precision
        /// is meaningless on a map, so trimming here is a real bandwidth win
        /// (§3.6). NaN and infinity are written as null — never as bare tokens,
        /// which would produce invalid JSON.
        /// </summary>
        public JsonWriter Value(double value, int decimals = 2)
        {
            Separate();
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                _sb.Append("null");
            }
            else
            {
                var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
                // "R" would give 1.2000000000000002; the fixed format plus trim
                // gives the shortest exact-enough representation.
                var text = rounded.ToString("0.##########", CultureInfo.InvariantCulture);
                if (text == "-0") text = "0";
                _sb.Append(text);
            }
            _needComma = true;
            return this;
        }

        public JsonWriter Prop(string name, string? value)
        {
            if (value == null) return this;
            return Name(name).Value(value);
        }

        public JsonWriter Prop(string name, long value) => Name(name).Value(value);

        public JsonWriter Prop(string name, bool value) => Name(name).Value(value);

        public JsonWriter Prop(string name, double value, int decimals = 2) => Name(name).Value(value, decimals);

        public JsonWriter PropIf(string name, bool condition, double value, int decimals = 2)
            => condition ? Prop(name, value, decimals) : this;

        public JsonWriter PropIf(string name, bool condition, long value)
            => condition ? Prop(name, value) : this;

        private void Separate()
        {
            if (_needComma) _sb.Append(',');
        }

        private void WriteQuoted(string value)
        {
            _sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        // Escape control characters and the line/paragraph
                        // separators that break some JS parsers.
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _sb.Append(c);
                        }
                        break;
                }
            }
            _sb.Append('"');
        }

        public override string ToString() => _sb.ToString();
    }
}
