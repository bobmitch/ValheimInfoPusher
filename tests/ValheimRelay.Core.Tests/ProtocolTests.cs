using System;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    public class FrameCodecTests
    {
        private static readonly WorldInfo World = new WorldInfo("Midgard", "hAbC12dEf", -1234567, "5713");

        [Fact]
        public void HelloCarriesEverythingTheMapNeedsToDrawTheWorld()
        {
            var text = FrameCodec.WriteHello(new HelloFrame("Bob", "vh_7f3c9a21", "1.0.0", World));
            var frame = JsonParser.Parse(text);

            Assert.Equal(FrameTypes.Hello, frame["type"].AsString());
            Assert.Equal(ProtocolVersion.Current, frame["v"].AsInt());
            Assert.Equal("Bob", frame["name"].AsString());
            Assert.Equal("vh_7f3c9a21", frame["uid"].AsString());
            Assert.Equal("Midgard", frame["world"]["name"].AsString());
            Assert.Equal("hAbC12dEf", frame["world"]["seed"].AsString());
            Assert.Equal(-1234567L, frame["world"]["seedInt"].AsLong());
        }

        [Fact]
        public void HelloOmitsShareWhenSharingAndSetsItWhenNot()
        {
            var sharing = JsonParser.Parse(FrameCodec.WriteHello(new HelloFrame("Bob", "u", "1.0.0", World)));
            Assert.False(sharing.Has("share"));

            var hidden = JsonParser.Parse(
                FrameCodec.WriteHello(new HelloFrame("Bob", "u", "1.0.0", World, sharingPosition: false)));
            Assert.False(hidden["share"].AsBool(true));
        }

        [Fact]
        public void PositionOmitsHealthWhenNotShared()
        {
            var withHealth = Sample(includeHealth: true);
            var withoutHealth = Sample(includeHealth: false);

            Assert.True(JsonParser.Parse(FrameCodec.WritePosition(withHealth)).Has("hp"));
            Assert.False(JsonParser.Parse(FrameCodec.WritePosition(withoutHealth)).Has("hp"));
        }

        [Fact]
        public void PositionCarriesNoPlayerIdOrUidBecauseTheRelayOwnsIdentity()
        {
            var frame = JsonParser.Parse(FrameCodec.WritePosition(Sample()));
            Assert.False(frame.Has("playerId"));
            Assert.False(frame.Has("uid"));
        }

        [Fact]
        public void PositionMarksDeathOnlyWhenDead()
        {
            Assert.False(JsonParser.Parse(FrameCodec.WritePosition(Sample())).Has("dead"));
            Assert.True(JsonParser.Parse(FrameCodec.WritePosition(Sample(dead: true)))["dead"].AsBool());
        }

        [Fact]
        public void PositionStaysWellUnderTheFrameBudget()
        {
            // §3.6 budgets ~130 bytes for a position frame at 16 players x 1 Hz.
            var text = FrameCodec.WritePosition(Sample(includeHealth: true));
            Assert.InRange(FrameCodec.MeasureBytes(text), 1, 160);
        }

        [Fact]
        public void MarkerRemoveCarriesOnlyTheId()
        {
            var text = FrameCodec.WriteMarker(new MarkerFrame(MarkerOps.Remove, "vh_a:m4", 1, 2, "x", "ore", 5));
            var frame = JsonParser.Parse(text);

            Assert.Equal("vh_a:m4", frame["id"].AsString());
            Assert.False(frame.Has("x"));
            Assert.False(frame.Has("label"));
            Assert.False(frame.Has("icon"));
        }

        [Fact]
        public void UnknownMarkerIconDegradesToDot()
        {
            var text = FrameCodec.WriteMarker(new MarkerFrame(MarkerOps.Add, "id", 1, 2, null, "sparkles", 5));
            Assert.Equal(MarkerIcons.Dot, JsonParser.Parse(text)["icon"].AsString());

            var read = FrameCodec.ReadMarker(JsonParser.Parse("{\"type\":\"marker\",\"id\":\"x\",\"op\":\"add\",\"x\":1,\"z\":2,\"icon\":\"nonsense\"}"));
            Assert.Equal(MarkerIcons.Dot, read!.Icon);
        }

        [Fact]
        public void WelcomeWithoutATokenIsNotACreator()
        {
            var joiner = FrameCodec.ReadWelcome(JsonParser.Parse(
                "{\"type\":\"welcome\",\"code\":\"K7MQ2XR4\",\"playerId\":\"p1\"}"))!;
            Assert.False(joiner.IsCreator);
            Assert.Null(joiner.Token);

            // An empty-string token must not be mistaken for ownership of the room.
            var empty = FrameCodec.ReadWelcome(JsonParser.Parse(
                "{\"type\":\"welcome\",\"code\":\"K7MQ2XR4\",\"playerId\":\"p1\",\"token\":\"\"}"))!;
            Assert.False(empty.IsCreator);

            var creator = FrameCodec.ReadWelcome(JsonParser.Parse(
                "{\"type\":\"welcome\",\"code\":\"K7MQ2XR4\",\"playerId\":\"p1\",\"token\":\"9f2b\"}"))!;
            Assert.True(creator.IsCreator);
        }

        [Fact]
        public void WelcomeReadsTheRosterAndSkipsEntriesWithoutAPlayerId()
        {
            var welcome = FrameCodec.ReadWelcome(JsonParser.Parse(
                "{\"type\":\"welcome\",\"code\":\"C\",\"playerId\":\"p1\",\"players\":[" +
                "{\"playerId\":\"p2\",\"name\":\"Bob\",\"uid\":\"vh_b\"}," +
                "{\"name\":\"Nameless\"}]}"))!;

            Assert.Single(welcome.Players);
            Assert.Equal("vh_b", welcome.Players[0].Uid);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"no\":\"type\"}")]
        [InlineData("{\"type\":42}")]
        [InlineData("")]
        public void UnparseableFramesAreRejectedWithoutThrowing(string input)
        {
            Assert.Null(FrameCodec.ParseFrame(input));
        }

        [Fact]
        public void MarkerAddWithoutCoordinatesIsRejected()
        {
            Assert.Null(FrameCodec.ReadMarker(JsonParser.Parse("{\"id\":\"x\",\"op\":\"add\"}")));
            Assert.Null(FrameCodec.ReadMarker(JsonParser.Parse("{\"op\":\"add\",\"x\":1,\"z\":2}")));
            Assert.Null(FrameCodec.ReadMarker(JsonParser.Parse("{\"id\":\"x\",\"op\":\"toggle\",\"x\":1,\"z\":2}")));

            // A remove needs nothing but the id (§3.4).
            Assert.NotNull(FrameCodec.ReadMarker(JsonParser.Parse("{\"id\":\"x\",\"op\":\"remove\"}")));
        }

        [Fact]
        public void PingWithoutCoordinatesIsRejected()
        {
            Assert.Null(FrameCodec.ReadPing(JsonParser.Parse("{\"type\":\"ping\",\"x\":1}")));
            Assert.NotNull(FrameCodec.ReadPing(JsonParser.Parse("{\"type\":\"ping\",\"x\":1,\"z\":2}")));
        }

        [Fact]
        public void FrameSizeIsMeasuredInUtf8BytesNotCharacters()
        {
            // A name of emoji is 4 bytes per character; a char count would let an
            // over-cap frame through and the relay would drop the connection.
            var name = new string('å', 100);
            var text = FrameCodec.WriteHello(new HelloFrame(name, "u", "1.0.0", World));
            Assert.True(FrameCodec.MeasureBytes(text) > text.Length);
        }

        private static PositionSample Sample(bool includeHealth = false, bool dead = false)
            => new PositionSample(123.4, -456.7, 31.2, 183.5, "BlackForest", 78, 100, includeHealth, dead, 1_725_148_800_123);
    }

    public class CloseCodeTests
    {
        [Fact]
        public void OnlyTheTwoNonLadderCodesNeedSpecialHandling()
        {
            Assert.True(CloseCodes.RequiresSpecialHandling(CloseCodes.RoomFull));
            Assert.True(CloseCodes.RequiresSpecialHandling(CloseCodes.RelayFull));
            Assert.False(CloseCodes.RequiresSpecialHandling(CloseCodes.UnknownCode));
            Assert.False(CloseCodes.RequiresSpecialHandling(1006));
        }
    }
}
