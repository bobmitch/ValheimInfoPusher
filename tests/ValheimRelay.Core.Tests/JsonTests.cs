using System;
using ValheimRelay.Core.Json;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    public class JsonWriterTests
    {
        [Fact]
        public void WritesNestedObjects()
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", "hello")
                .Prop("v", 1)
                .Name("world").BeginObject().Prop("name", "Midgard").Prop("seedInt", -1234567L).EndObject()
                .EndObject();

            Assert.Equal("{\"type\":\"hello\",\"v\":1,\"world\":{\"name\":\"Midgard\",\"seedInt\":-1234567}}", w.ToString());
        }

        [Theory]
        [InlineData("plain", "\"plain\"")]
        [InlineData("with \"quotes\"", "\"with \\\"quotes\\\"\"")]
        [InlineData("back\\slash", "\"back\\\\slash\"")]
        [InlineData("line\nbreak", "\"line\\nbreak\"")]
        [InlineData("\u0001", "\"\\u0001\"")]
        public void EscapesStrings(string input, string expected)
        {
            var w = new JsonWriter();
            w.Value(input);
            Assert.Equal(expected, w.ToString());
        }

        [Fact]
        public void EscapesLineSeparatorsThatBreakJavaScriptParsers()
        {
            var w = new JsonWriter();
            w.Value("a\u2028b\u2029c");
            Assert.Equal("\"a\\u2028b\\u2029c\"", w.ToString());
        }

        [Fact]
        public void TrimsCoordinatePrecision()
        {
            var w = new JsonWriter();
            w.BeginObject().Prop("x", 123.456789).Prop("y", 31.24, 1).EndObject();
            Assert.Equal("{\"x\":123.46,\"y\":31.2}", w.ToString());
        }

        [Fact]
        public void WritesNegativeZeroAsZero()
        {
            var w = new JsonWriter();
            w.Value(-0.001);
            Assert.Equal("0", w.ToString());
        }

        [Fact]
        public void WritesNonFiniteNumbersAsNullRatherThanInvalidJson()
        {
            var w = new JsonWriter();
            w.BeginObject().Prop("x", double.NaN).Prop("z", double.PositiveInfinity).EndObject();
            var text = w.ToString();

            Assert.Equal("{\"x\":null,\"z\":null}", text);
            Assert.True(JsonParser.TryParse(text, out _));
        }

        [Fact]
        public void UsesInvariantFormattingRegardlessOfCulture()
        {
            var original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                // A comma decimal separator would produce invalid JSON, and plenty
                // of players run the game under exactly this culture.
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var w = new JsonWriter();
                w.Value(1234.5);
                Assert.Equal("1234.5", w.ToString());
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void OmitsNullStringProperties()
        {
            var w = new JsonWriter();
            w.BeginObject().Prop("a", "x").Prop("b", (string?)null).Prop("c", "y").EndObject();
            Assert.Equal("{\"a\":\"x\",\"c\":\"y\"}", w.ToString());
        }
    }

    public class JsonParserTests
    {
        [Fact]
        public void RoundTripsThroughTheWriter()
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", "marker")
                .Prop("label", "silver \"here\"\n")
                .Prop("x", -456.75)
                .Prop("ok", true)
                .EndObject();

            var parsed = JsonParser.Parse(w.ToString());
            Assert.Equal("marker", parsed["type"].AsString());
            Assert.Equal("silver \"here\"\n", parsed["label"].AsString());
            Assert.Equal(-456.75, parsed["x"].AsDouble(), 5);
            Assert.True(parsed["ok"].AsBool());
        }

        [Fact]
        public void MissingFieldsYieldDefaultsRatherThanThrowing()
        {
            var parsed = JsonParser.Parse("{\"a\":1}");
            Assert.True(parsed["nope"].IsNull);
            Assert.Equal("fallback", parsed["nope"].AsString("fallback"));
            Assert.Equal(7, parsed["nope"].AsInt(7));
            Assert.False(parsed["nope"].AsBool());
            Assert.Empty(parsed["nope"].AsArray());
            Assert.True(parsed["a"]["deep"]["deeper"].IsNull);
        }

        [Fact]
        public void WrongTypedFieldFallsBackInsteadOfThrowing()
        {
            var parsed = JsonParser.Parse("{\"x\":\"not-a-number\"}");
            Assert.Equal(0, parsed["x"].AsDouble());
            Assert.Equal(-1, parsed["x"].AsInt(-1));
        }

        [Fact]
        public void ParsesArraysAndUnicodeEscapes()
        {
            var parsed = JsonParser.Parse("{\"players\":[{\"name\":\"\\u00c5sa\"},{\"name\":\"Bob\"}]}");
            var players = parsed["players"].AsArray();
            Assert.Equal(2, players.Count);
            Assert.Equal("Åsa", players[0]["name"].AsString());
        }

        [Fact]
        public void ExposesObjectKeys()
        {
            var parsed = JsonParser.Parse("{\"a\":1,\"b\":2}");
            Assert.Equal(new[] { "a", "b" }, parsed.Keys);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("{\"a\":1,}")]
        [InlineData("{\"a\" 1}")]
        [InlineData("[1,2")]
        [InlineData("{\"a\":\"unterminated")]
        [InlineData("{\"a\":01x}")]
        [InlineData("nope")]
        [InlineData("{\"a\":1} trailing")]
        public void RejectsMalformedInputWithoutThrowing(string input)
        {
            Assert.False(JsonParser.TryParse(input, out var value));
            Assert.True(value.IsNull);
        }

        [Fact]
        public void RejectsDeeplyNestedInputInsteadOfBlowingTheStack()
        {
            var deep = new string('[', 5000) + new string(']', 5000);
            Assert.False(JsonParser.TryParse(deep, out _));
        }

        [Fact]
        public void ParsesScientificNotation()
        {
            var parsed = JsonParser.Parse("{\"t\":1.7251488e12}");
            Assert.Equal(1_725_148_800_000L, parsed["t"].AsLong());
        }

        [Fact]
        public void LastDuplicateKeyWinsMatchingJavaScript()
        {
            var parsed = JsonParser.Parse("{\"code\":\"AAA\",\"code\":\"BBB\"}");
            Assert.Equal("BBB", parsed["code"].AsString());
        }
    }
}
