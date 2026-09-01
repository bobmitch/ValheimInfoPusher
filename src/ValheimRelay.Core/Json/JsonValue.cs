using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimRelay.Core.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// A parsed JSON value. Accessors are deliberately forgiving: asking for a
    /// field that is absent, null, or of the wrong type returns the supplied
    /// default rather than throwing. That is what makes "ignore unknown fields"
    /// (§3) the path of least resistance at every call site.
    /// </summary>
    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null, null, 0, false, null, null);

        private readonly string? _string;
        private readonly double _number;
        private readonly bool _bool;
        private readonly List<JsonValue>? _array;
        private readonly Dictionary<string, JsonValue>? _object;

        private JsonValue(
            JsonKind kind,
            string? str,
            double number,
            bool boolean,
            List<JsonValue>? array,
            Dictionary<string, JsonValue>? obj)
        {
            Kind = kind;
            _string = str;
            _number = number;
            _bool = boolean;
            _array = array;
            _object = obj;
        }

        public JsonKind Kind { get; }

        public static JsonValue String(string value) => new JsonValue(JsonKind.String, value, 0, false, null, null);
        public static JsonValue Number(double value) => new JsonValue(JsonKind.Number, null, value, false, null, null);
        public static JsonValue Bool(bool value) => new JsonValue(JsonKind.Bool, null, 0, value, null, null);
        public static JsonValue Array(List<JsonValue> items) => new JsonValue(JsonKind.Array, null, 0, false, items, null);
        public static JsonValue Object(Dictionary<string, JsonValue> fields) => new JsonValue(JsonKind.Object, null, 0, false, null, fields);

        public bool IsNull => Kind == JsonKind.Null;

        public IReadOnlyList<JsonValue> AsArray()
            => _array ?? (IReadOnlyList<JsonValue>)System.Array.Empty<JsonValue>();

        /// <summary>Field lookup. Missing fields yield <see cref="Null"/>, never an exception.</summary>
        public JsonValue this[string name]
        {
            get
            {
                if (_object != null && _object.TryGetValue(name, out var value)) return value;
                return Null;
            }
        }

        public bool Has(string name) => _object != null && _object.ContainsKey(name);

        /// <summary>
        /// Field names, in insertion order. Only needed where the keys are data
        /// rather than schema — the reclaim store keys by world UID.
        /// </summary>
        public IEnumerable<string> Keys
            => _object != null ? _object.Keys : (IEnumerable<string>)System.Array.Empty<string>();

        public string? AsString(string? fallback = null)
            => Kind == JsonKind.String ? _string : fallback;

        public double AsDouble(double fallback = 0)
            => Kind == JsonKind.Number ? _number : fallback;

        public long AsLong(long fallback = 0)
        {
            if (Kind != JsonKind.Number) return fallback;
            if (double.IsNaN(_number) || double.IsInfinity(_number)) return fallback;
            if (_number >= long.MaxValue) return long.MaxValue;
            if (_number <= long.MinValue) return long.MinValue;
            return (long)_number;
        }

        public int AsInt(int fallback = 0)
        {
            var value = AsLong(fallback);
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        public bool AsBool(bool fallback = false)
            => Kind == JsonKind.Bool ? _bool : fallback;

        public override string ToString() => Kind switch
        {
            JsonKind.Null => "null",
            JsonKind.Bool => _bool ? "true" : "false",
            JsonKind.Number => _number.ToString(CultureInfo.InvariantCulture),
            JsonKind.String => _string ?? string.Empty,
            JsonKind.Array => "[" + AsArray().Count + " items]",
            _ => "{object}"
        };
    }
}
