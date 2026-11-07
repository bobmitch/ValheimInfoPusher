using System;
using System.Collections.Generic;
using System.Linq;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    /// <summary>The tile encoding of PLAN.md §3.7, against the reference decoder.</summary>
    public class FogTileTests
    {
        private const int N = FogTiles.TileSize;
        private readonly FogTileEncoder _encoder = new();

        private static bool[] Decode(string encoding, string data)
        {
            Assert.True(FogTiles.TryDecode(encoding, data, out var cells));
            return cells;
        }

        [Fact]
        public void AnEmptyTileIsNotEncoded()
        {
            var explored = new bool[512 * 512];
            Assert.False(_encoder.TryEncode(explored, 512, 1, 1, out _, out _));
        }

        [Fact]
        public void ABlobRoundTripsThroughRle()
        {
            const int size = 512;
            var explored = new bool[size * size];
            // A disc, the shape the game actually reveals.
            for (var cy = 0; cy < size; cy++)
            for (var cx = 0; cx < size; cx++)
            {
                var dx = cx - 200;
                var dy = cy - 180;
                if ((dx * dx) + (dy * dy) <= 40 * 40) explored[(cy * size) + cx] = true;
            }

            Assert.True(_encoder.TryEncode(explored, size, 1, 1, out var encoding, out var data));
            Assert.Equal(FogEncodings.Rle, encoding);

            var cells = Decode(encoding, data);
            for (var i = 0; i < FogTiles.CellsPerTile; i++)
            {
                var cx = N + (i % N);
                var cy = N + (i / N);
                Assert.Equal(explored[(cy * size) + cx], cells[i]);
            }
        }

        [Fact]
        public void ANoisyTileFallsBackToBitsAndStillRoundTrips()
        {
            const int size = 256;
            var explored = new bool[size * size];
            for (var i = 0; i < explored.Length; i++) explored[i] = ((i / size) + (i % size)) % 2 == 0;

            Assert.True(_encoder.TryEncode(explored, size, 0, 0, out var encoding, out var data));
            Assert.Equal(FogEncodings.Bits, encoding);

            var cells = Decode(encoding, data);
            for (var i = 0; i < FogTiles.CellsPerTile; i++)
            {
                Assert.Equal(explored[((i / N) * size) + (i % N)], cells[i]);
            }
        }

        [Fact]
        public void AFullyExploredTileCostsAFewBytes()
        {
            const int size = 256;
            var explored = Enumerable.Repeat(true, size * size).ToArray();

            Assert.True(_encoder.TryEncode(explored, size, 1, 0, out var encoding, out var data));
            Assert.Equal(FogEncodings.Rle, encoding);
            Assert.True(data.Length <= 8, "a solid tile should be a zero run and one long run, not " + data);
            Assert.All(Decode(encoding, data), Assert.True);
        }

        /// <summary>
        /// The whole point of describing tiles in the game's index order: cell
        /// (cx, cy) of the array is cell (cx − tx·n) + (cy − tz·n)·n of the tile,
        /// with no flip and no transpose anywhere.
        /// </summary>
        [Fact]
        public void TileCellsAreTheArrayInItsOwnIndexOrder()
        {
            const int size = 2048;
            var explored = new bool[size * size];
            var marked = new[] { (cx: 1153, cy: 515), (cx: 1279, cy: 512), (cx: 1152, cy: 639) };
            foreach (var (cx, cy) in marked) explored[(cy * size) + cx] = true;

            Assert.True(_encoder.TryEncode(explored, size, 9, 4, out var encoding, out var data));
            var cells = Decode(encoding, data);

            Assert.Equal(marked.Length, cells.Count(c => c));
            foreach (var (cx, cy) in marked)
            {
                Assert.True(cells[((cy - (4 * N)) * N) + (cx - (9 * N))]);
            }
        }

        [Fact]
        public void CellsBeyondTheEdgeOfAnOddSizedMapAreUnexplored()
        {
            const int size = 200; // tiles 0 and 1 per side; tile 1 is 72 cells of map
            var explored = Enumerable.Repeat(true, size * size).ToArray();

            Assert.Equal(2, FogTiles.TilesPerSide(size));
            Assert.True(_encoder.TryEncode(explored, size, 1, 1, out var encoding, out var data));

            var cells = Decode(encoding, data);
            for (var i = 0; i < FogTiles.CellsPerTile; i++)
            {
                var inMap = N + (i % N) < size && N + (i / N) < size;
                Assert.Equal(inMap, cells[i]);
            }
        }

        [Fact]
        public void TheWorstCaseTileFitsInOneFrame()
        {
            const int size = FogTiles.MaxMapSize;
            var explored = new bool[N * 2 * (N * 2)];
            for (var i = 0; i < explored.Length; i++) explored[i] = i % 2 == 0;

            Assert.True(_encoder.TryEncode(explored, N * 2, 0, 0, out var encoding, out var data));
            var frame = FrameCodec.WriteFog(size, 123.4567, 127, 127, encoding, data);

            Assert.True(FrameCodec.FitsInFrame(frame), FrameCodec.MeasureBytes(frame) + " bytes");
            Assert.True(FrameCodec.MeasureBytes(frame) < 3000);
        }

        [Fact]
        public void TheDecoderRejectsRunsThatDoNotCoverTheTile()
        {
            Assert.False(FogTiles.TryDecode(FogEncodings.Rle, Convert.ToBase64String(new byte[] { 0, 5 }), out _));

            // 0, 16384, 1: one cell too many.
            var overrun = new byte[] { 0, 0x80, 0x80, 0x01, 1 };
            Assert.False(FogTiles.TryDecode(FogEncodings.Rle, Convert.ToBase64String(overrun), out _));

            Assert.False(FogTiles.TryDecode("zstd", Convert.ToBase64String(new byte[] { 0 }), out _));
            Assert.False(FogTiles.TryDecode(FogEncodings.Bits, Convert.ToBase64String(new byte[10]), out _));
            Assert.False(FogTiles.TryDecode(FogEncodings.Rle, "not base64!", out _));
        }

        [Fact]
        public void TheFogFrameCarriesItsGeometryAndNoLayer()
        {
            var frame = JsonParser.Parse(FrameCodec.WriteFog(2048, 12, 9, 4, FogEncodings.Rle, "AAE="));

            Assert.Equal("fog", frame["type"].AsString());
            Assert.Equal(1, frame["v"].AsLong());
            Assert.Equal(2048, frame["w"].AsLong());
            Assert.Equal(12, frame["px"].AsDouble());
            Assert.Equal(N, frame["n"].AsLong());
            Assert.Equal(9, frame["tx"].AsLong());
            Assert.Equal(4, frame["tz"].AsLong());
            Assert.Equal("rle", frame["enc"].AsString());
            Assert.Equal("AAE=", frame["d"].AsString());
            Assert.Equal(JsonKind.Null, frame["layer"].Kind);
            Assert.Equal(JsonKind.Null, frame["playerId"].Kind);
        }

        [Fact]
        public void HelloAdvertisesFogOnlyWhenARadiusIsGiven()
        {
            var world = new WorldInfo("Midgard", "seed", 1, "5713");

            var off = JsonParser.Parse(FrameCodec.WriteHello(new HelloFrame("Bob", "vh_1", "1.0", world)));
            Assert.Equal(JsonKind.Null, off["fog"].Kind);

            var on = JsonParser.Parse(FrameCodec.WriteHello(new HelloFrame("Bob", "vh_1", "1.0", world, true, 100)));
            Assert.Equal(100, on["fog"]["r"].AsDouble());
        }

        [Fact]
        public void RequestFogIsForEveryoneUnlessItNamesAUid()
        {
            Assert.True(FrameCodec.IsRequestFogFor(JsonParser.Parse("{\"type\":\"request_fog\",\"v\":1}"), "vh_a"));
            Assert.True(FrameCodec.IsRequestFogFor(JsonParser.Parse("{\"type\":\"request_fog\",\"uid\":\"vh_a\"}"), "vh_a"));
            Assert.False(FrameCodec.IsRequestFogFor(JsonParser.Parse("{\"type\":\"request_fog\",\"uid\":\"vh_b\"}"), "vh_a"));
            Assert.False(FrameCodec.IsRequestFogFor(JsonParser.Parse("{\"type\":\"request_fog\",\"uid\":\"vh_b\"}"), null));
        }
    }

    public sealed class FakeFogSource : IFogSource
    {
        public FakeFogSource(int size = 512, double metresPerPixel = 12, double revealRadius = 100)
        {
            Size = size;
            MetresPerPixel = metresPerPixel;
            RevealRadius = revealRadius;
            Explored = new bool[size * size];
        }

        public int Size { get; }
        public double MetresPerPixel { get; }
        public double RevealRadius { get; set; }
        public bool[] Explored { get; set; }
        public bool Available { get; set; } = true;
        public bool Throws { get; set; }

        public bool TryGetFogMap(out FogMap map)
        {
            if (Throws) throw new MissingFieldException("Minimap", "m_explored");
            map = new FogMap(Explored, Size, MetresPerPixel, RevealRadius);
            return Available;
        }

        /// <summary>Reveals a disc the way the game does, centred on a world position.</summary>
        public void Reveal(double x, double z, double radius)
        {
            var cx = (x / MetresPerPixel) + (Size / 2.0);
            var cy = (z / MetresPerPixel) + (Size / 2.0);
            var r = radius / MetresPerPixel;
            for (var y = 0; y < Size; y++)
            for (var xx = 0; xx < Size; xx++)
            {
                var dx = xx - cx;
                var dy = y - cy;
                if ((dx * dx) + (dy * dy) <= r * r) Explored[(y * Size) + xx] = true;
            }
        }
    }

    /// <summary>§3.7 end to end through the session, against a fake transport and a hand-moved clock.</summary>
    public class FogSessionTests
    {
        private const string Uid = "vh_7f3c9a21";

        private readonly FakeClock _clock = new();
        private readonly FakeLog _log = new();
        private readonly FakeTransport _transport = new();
        private readonly FakeGameChannel _channel = new();
        private readonly FakePeerView _peers = new() { SelfPeerId = 10 };
        private readonly FakeFogSource _source = new();
        private readonly SessionOptions _options;
        private readonly RelaySession _session;

        public FogSessionTests()
        {
            _options = new SessionOptions
            {
                RelayUrl = "ws://localhost:8080/ws",
                CreationStaggerSpread = TimeSpan.Zero,
                ShareFog = true
            };
            _session = new RelaySession(
                _options, _transport, _channel, _peers, _clock, _log,
                new ReclaimStore(new InMemoryReclaimStorage(), _log), random: () => 0.5, fogSource: _source);
        }

        private void Tick(double seconds = 0)
        {
            if (seconds > 0) _clock.Advance(seconds);
            _session.Tick();
        }

        private void StartActive()
        {
            _session.Start(new SessionIdentity("Bob", Uid, "1.0.0", new WorldInfo("Midgard", "s", 1, "5713")));
            Tick(6);
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("K7MQ2XR4", token: "tok");
            Tick();
        }

        private void At(double x, double z)
        {
            _session.SubmitPosition(new PositionSample(x, z, 30, 0, "Meadows", 0, 0, false, false, 0));
        }

        private void RequestFog(string? uid = null)
        {
            _transport.Deliver(uid == null
                ? "{\"type\":\"request_fog\",\"v\":1}"
                : "{\"type\":\"request_fog\",\"v\":1,\"uid\":\"" + uid + "\"}");
        }

        /// <summary>Ticks in 100 ms steps, long enough for any snapshot here to drain.</summary>
        private void Run(double seconds)
        {
            for (var t = 0.0; t < seconds; t += 0.1) Tick(0.1);
        }

        private List<JsonValue> FogFrames() => _transport.SentOfType(FrameTypes.Fog);

        private JsonValue LastHello() => _transport.SentOfType(FrameTypes.Hello).Last();

        /// <summary>ORs every fog frame sent into a map, as §3.7 tells a map to.</summary>
        private bool[] Reassemble(IEnumerable<JsonValue> frames)
        {
            var map = new bool[_source.Size * _source.Size];
            foreach (var frame in frames)
            {
                Assert.True(FogTiles.TryDecode(frame["enc"].AsString(), frame["d"].AsString(), out var cells));
                var tx = (int)frame["tx"].AsLong();
                var tz = (int)frame["tz"].AsLong();
                for (var i = 0; i < cells.Length; i++)
                {
                    if (!cells[i]) continue;
                    var cx = (tx * FogTiles.TileSize) + (i % FogTiles.TileSize);
                    var cy = (tz * FogTiles.TileSize) + (i / FogTiles.TileSize);
                    map[(cy * _source.Size) + cx] = true;
                }
            }
            return map;
        }

        // ------------------------------------------------------------- opt-in

        [Fact]
        public void HelloCarriesTheRevealRadiusWhileSharing()
        {
            StartActive();

            var hello = Assert.Single(_transport.SentOfType(FrameTypes.Hello));
            Assert.Equal(100, hello["fog"]["r"].AsDouble());
        }

        [Fact]
        public void FogIsOffByDefault()
        {
            Assert.False(new SessionOptions().ShareFog);
        }

        [Fact]
        public void HidingPositionHidesFogWhateverTheFogSettingSays()
        {
            _options.SharePosition = false;
            StartActive();
            _source.Reveal(0, 0, 500);
            RequestFog();
            Run(5);

            Assert.Equal(JsonKind.Null, LastHello()["fog"].Kind);
            Assert.Empty(FogFrames());
        }

        [Fact]
        public void TurningSharingOffSaysSoAtOnceAndStopsMidSnapshot()
        {
            _source.Reveal(0, 0, 3000);
            StartActive();
            RequestFog();
            Tick(0.1);
            Tick(0.1);
            var sentBefore = FogFrames().Count;
            Assert.True(sentBefore > 0);

            _session.SetFogSharing(false);
            Run(5);

            Assert.Equal(JsonKind.Null, LastHello()["fog"].Kind);
            Assert.Equal(sentBefore, FogFrames().Count);
        }

        [Fact]
        public void TurningSharingOnMidSessionAdvertisesItWithoutWaitingForTheHeartbeat()
        {
            _session.SetFogSharing(false);
            StartActive();
            Assert.Equal(JsonKind.Null, LastHello()["fog"].Kind);

            _session.SetFogSharing(true);
            Tick();

            Assert.Equal(100, LastHello()["fog"]["r"].AsDouble());
        }

        [Fact]
        public void HelloSaysNothingAboutFogUntilThereIsAMapToShare()
        {
            _source.Available = false;
            StartActive();
            Assert.Equal(JsonKind.Null, LastHello()["fog"].Kind);

            _source.Available = true;
            Tick();
            Assert.Equal(100, LastHello()["fog"]["r"].AsDouble());
        }

        [Fact]
        public void ABrokenSourceCostsFogAndNothingElse()
        {
            _source.Throws = true;
            StartActive();
            RequestFog();
            Run(2);

            Assert.Equal(SessionState.Active, _session.State);
            Assert.Equal(JsonKind.Null, LastHello()["fog"].Kind);
            Assert.Empty(FogFrames());
            Assert.True(_log.Contains("explored map"));
        }

        [Fact]
        public void AnArrayOfTheWrongSizeIsNotIndexed()
        {
            _source.Explored = new bool[10];
            StartActive();
            RequestFog();
            Run(2);

            Assert.Empty(FogFrames());
            Assert.Equal(SessionState.Active, _session.State);
        }

        // ----------------------------------------------------------- snapshots

        [Fact]
        public void NothingIsSentOnConnectTheMapAsks()
        {
            _source.Reveal(0, 0, 2000);
            StartActive();
            Run(5);

            Assert.Empty(FogFrames());
        }

        [Fact]
        public void ASnapshotIsEveryExploredTileAndReassemblesExactly()
        {
            _source.Reveal(0, 0, 1000);
            _source.Reveal(-2000, 1500, 300);
            StartActive();
            RequestFog();
            Run(5);

            var frames = FogFrames();
            Assert.NotEmpty(frames);
            Assert.Equal(_source.Explored, Reassemble(frames));
            Assert.All(frames, f => Assert.True(FrameCodec.FitsInFrame(f.ToString())));

            // No empty tile went out: the map assumes those already.
            var tiles = frames.Select(f => (f["tx"].AsLong(), f["tz"].AsLong())).ToList();
            Assert.Equal(tiles.Count, tiles.Distinct().Count());
        }

        [Fact]
        public void ASnapshotStartsNearestThePlayer()
        {
            _source.Explored = Enumerable.Repeat(true, _source.Size * _source.Size).ToArray();
            StartActive();
            // Bottom-left corner of a 512-cell map at 12 m: cell (10, 10).
            At(-2950, -2950);
            RequestFog();
            Run(3);

            var first = FogFrames().First();
            Assert.Equal(0, first["tx"].AsLong());
            Assert.Equal(0, first["tz"].AsLong());
        }

        [Fact]
        public void SnapshotFramesArePaced()
        {
            _source.Explored = Enumerable.Repeat(true, _source.Size * _source.Size).ToArray();
            StartActive();
            RequestFog();

            Tick();
            Tick();
            Tick();
            Assert.Single(FogFrames());

            Run(3);
            Assert.Equal(16, FogFrames().Count);
        }

        [Fact]
        public void ARequestForSomeoneElseIsIgnored()
        {
            _source.Reveal(0, 0, 500);
            StartActive();

            RequestFog("vh_someoneelse");
            Run(3);
            Assert.Empty(FogFrames());

            RequestFog(Uid);
            Run(3);
            Assert.NotEmpty(FogFrames());
        }

        [Fact]
        public void ARequestInsideTheCooldownIsAnsweredWhenItExpiresNotDropped()
        {
            _source.Reveal(0, 0, 500);
            StartActive();

            RequestFog();
            Run(3);
            var first = FogFrames().Count;

            RequestFog();
            RequestFog();
            Run(10);
            Assert.Equal(first, FogFrames().Count);

            Run(20);
            Assert.Equal(first * 2, FogFrames().Count);

            Run(40);
            Assert.Equal(first * 2, FogFrames().Count);
        }

        [Fact]
        public void ARequestBeforeTheMinimapExistsIsOwedNotDropped()
        {
            _source.Reveal(0, 0, 500);
            _source.Available = false;
            StartActive();
            RequestFog();
            Run(2);
            Assert.Empty(FogFrames());

            _source.Available = true;
            Run(3);
            Assert.NotEmpty(FogFrames());
        }

        // ---------------------------------------------------------------- lanes

        [Fact]
        public void FogWaitsBehindEverythingElse()
        {
            _source.Reveal(0, 0, 2000);
            StartActive();
            RequestFog();
            _session.AddMarker(1, 2, "silver", "ore");
            At(5, 5);
            Tick();

            var types = _transport.SentFrames().Select(f => f["type"].AsString()).ToList();
            var firstFog = types.IndexOf(FrameTypes.Fog);
            Assert.True(firstFog > types.IndexOf(FrameTypes.Marker));
            Assert.True(firstFog > types.IndexOf(FrameTypes.Position));
        }

        [Fact]
        public void ARefusedTileIsSentLaterNotLost()
        {
            _source.Reveal(0, 0, 1500);
            StartActive();
            RequestFog();

            _transport.AcceptSends = false;
            Run(1);
            _transport.AcceptSends = true;
            Run(5);

            Assert.Equal(_source.Explored, Reassemble(FogFrames()));
        }

        // ---------------------------------------------------------------- live

        [Fact]
        public void ExploringSendsTheTilesUnderThePlayerOnTheNextUpdate()
        {
            StartActive();
            At(0, 0);
            Run(25);
            Assert.Empty(FogFrames());

            _source.Reveal(0, 0, 100);
            At(0, 0);
            Run(21);

            var frames = FogFrames();
            Assert.NotEmpty(frames);
            Assert.Equal(_source.Explored, Reassemble(frames));
        }

        [Fact]
        public void AnUnchangedTileIsNotResent()
        {
            StartActive();
            _source.Reveal(0, 0, 100);
            At(0, 0);
            Run(21);
            var sent = FogFrames().Count;
            Assert.True(sent > 0);

            for (var i = 0; i < 60; i++)
            {
                At(0, 0);
                Tick(1);
            }

            Assert.Equal(sent, FogFrames().Count);
        }

        [Fact]
        public void LiveUpdatesStopWhileSharingIsOff()
        {
            _session.SetFogSharing(false);
            StartActive();
            _source.Reveal(0, 0, 100);
            At(0, 0);
            Run(25);

            Assert.Empty(FogFrames());
        }

        [Fact]
        public void TheSweepCatchesExplorationAwayFromThePlayerInTilesTheMapHas()
        {
            _source.Reveal(-2000, -2000, 200);
            _source.Reveal(2000, 2000, 200);
            StartActive();
            At(0, 0);
            RequestFog();
            Run(3);
            var before = FogFrames().Count;

            // Explored without the player going there: a console command, say.
            _source.Reveal(2200, 2200, 200);
            Run(60);
            Assert.Equal(before, FogFrames().Count);

            Run(5 * 60);
            Assert.True(FogFrames().Count > before);
            Assert.Equal(_source.Explored, Reassemble(FogFrames()));
        }

        // --------------------------------------------------------------- reconnect

        [Fact]
        public void AReconnectForgetsWhatWasSentAndAnswersTheMapsNewRequestAtOnce()
        {
            _source.Reveal(0, 0, 500);
            StartActive();
            RequestFog();
            Run(3);
            var first = FogFrames().Count;

            _transport.DropWith(1006);
            Tick();
            Tick(10);
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("K7MQ2XR4", playerId: "p2");
            Tick();

            RequestFog(Uid);
            Run(3);
            Assert.Equal(first * 2, FogFrames().Count);
        }
    }
}
