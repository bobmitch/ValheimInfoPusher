using System;
using System.Linq;
using ValheimRelay.Core.Identity;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    /// <summary>
    /// One test per defect found in review. Each names the failure it prevents,
    /// because none of these are obvious from the code they guard.
    /// </summary>
    public class RegressionTests
    {
        private const string WorldUid = "5713";

        private readonly FakeClock _clock = new();
        private readonly FakeLog _log = new();
        private readonly FakeTransport _transport = new();
        private readonly FakeGameChannel _channel = new();
        private readonly FakePeerView _peers = new() { SelfPeerId = 10 };
        private readonly InMemoryReclaimStorage _storage = new();
        private readonly SessionOptions _options;
        private readonly ReclaimStore _reclaim;
        private readonly RelaySession _session;

        public RegressionTests()
        {
            _options = new SessionOptions
            {
                RelayUrl = "ws://localhost:8080/ws",
                DiscoveryWindow = TimeSpan.FromSeconds(5),
                CreationStaggerSpread = TimeSpan.Zero
            };
            _reclaim = new ReclaimStore(_storage, _log);
            _session = new RelaySession(_options, _transport, _channel, _peers, _clock, _log, _reclaim, () => 0.5);
        }

        private static SessionIdentity Identity() => new(
            "Bob", "vh_bob", "1.0.0", new WorldInfo("Midgard", "seed", 1, WorldUid));

        private void Tick(double seconds = 0)
        {
            if (seconds > 0) _clock.Advance(seconds);
            _session.Tick();
        }

        private string StartAsCreator(string code = "K7MQ2XR4", string token = "tok-1")
        {
            _session.Start(Identity());
            Tick(6);
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome(code, token: token);
            Tick();
            return code;
        }

        [Fact]
        public void ARefusedFrameKeepsItsPlaceInsteadOfBeingResentOutOfOrder()
        {
            // A re-enqueue appends to the tail, so an add/remove pair refused
            // mid-drain came back as remove-then-add — leaving an undeletable
            // phantom marker on every map in the session.
            StartAsCreator();
            _transport.Sent.Clear();

            var id = _session.AddMarker(1, 2, "temporary", "ore")!;
            _session.RemoveMarker(id);

            _transport.AcceptSends = false;
            Tick();
            _transport.AcceptSends = true;
            Tick();

            var markers = _transport.SentOfType(FrameTypes.Marker);
            Assert.Equal(2, markers.Count);
            Assert.Equal(MarkerOps.Add, markers[0]["op"].AsString());
            Assert.Equal(MarkerOps.Remove, markers[1]["op"].AsString());
        }

        [Fact]
        public void ARefusedPositionIsNotPromotedIntoTheReliableLane()
        {
            // Telemetry that came back through the reliable queue would outrank
            // real frames and stop being superseded — the exact inversion of the
            // §4.2 drop policy.
            var queue = new OutboundQueue(reliableCapacity: 4);
            queue.SetPosition("stale");

            Assert.True(queue.TryPeek(out var peeked));
            Assert.Equal("stale", peeked);

            // The transport refused, so nothing is committed; a newer sample
            // arrives and must win.
            queue.SetPosition("fresh");
            queue.CommitPeek();

            Assert.True(queue.TryPeek(out var next));
            Assert.Equal("fresh", next);
        }

        [Fact]
        public void ADeliberateCloseAtShutdownDoesNotSwallowTheNextRunsFirstDrop()
        {
            // Stop() counted a close that HandleClosed never consumed, so the
            // credit survived into the next session and ate a real 1006 — the
            // session sat Active for ever behind a dead socket.
            StartAsCreator();
            _session.Stop("logout");

            _session.Start(Identity());
            Tick(6);
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("NEWCODE1", token: "tok-2");
            Tick();
            Assert.Equal(SessionState.Active, _session.State);

            _transport.DropWith(1006);
            Tick();

            Assert.Equal(SessionState.Reconnecting, _session.State);
        }

        [Fact]
        public void AfterATokenRejectionThePeersLiveCodeIsStillAdopted()
        {
            // The arbiter kept Defending the abandoned code, so this client
            // ignored every announcement of the live one — a permanent split.
            StartAsCreator("AAAAAAAA");
            _transport.DropWith(CloseCodes.TokenMismatch);
            Tick();
            Assert.Equal(SessionState.Discovering, _session.State);

            _channel.PeerAnnounces("ZZZZZZZZ", epoch: 1);
            Tick();

            Assert.Equal(SessionState.Joining, _session.State);
            Assert.Equal("ZZZZZZZZ", _transport.Connects.Last().Code);
        }

        [Fact]
        public void AFullMarkerReplayFitsInTheOutboundQueue()
        {
            // hello + 64 markers is 65 frames; the queue defaulted to 64, so a
            // reloaded map silently lost a marker — the precise failure §12.4
            // exists to prevent.
            StartAsCreator();
            for (var i = 0; i < MarkerStore.MaxOwnedMarkers; i++)
            {
                Assert.NotNull(_session.AddMarker(i, i, "m" + i, "ore"));
            }

            Tick(10);
            _transport.Sent.Clear();
            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();

            Assert.Equal(MarkerStore.MaxOwnedMarkers, _transport.SentOfType(FrameTypes.Marker).Count);
            Assert.Single(_transport.SentOfType(FrameTypes.Hello));
            Assert.DoesNotContain(_log.Lines, l => l.Contains("queue full"));
        }

        [Fact]
        public void NormaliseRaisesACapacityTooSmallToHoldAReplay()
        {
            var options = new SessionOptions { OutboundReliableCapacity = 8 };
            options.Normalise();
            Assert.True(options.OutboundReliableCapacity > MarkerStore.MaxOwnedMarkers);
        }

        [Fact]
        public void ASocketThatOpensButNeverSendsWelcomeIsRetried()
        {
            // A wedged proxy or a relay mid-restart left the session in Creating
            // for ever: no retry, no notice, nothing in the log.
            _session.Start(Identity());
            Tick(6);
            _transport.CompleteConnect();
            Tick();
            Assert.Equal(SessionState.Creating, _session.State);

            Tick(25);
            Assert.Equal(SessionState.Reconnecting, _session.State);

            Tick(5);
            Assert.True(_transport.Connects.Count > 1);
        }

        [Fact]
        public void TheCreatorsHeartbeatDoesNotRestartAJoinAlreadyInFlight()
        {
            // The creator announces every 30 s, so a second announcement during
            // a join is normal — and it was tearing the join down and starting
            // again, indefinitely.
            _session.Start(Identity());
            _channel.PeerAnnounces("PEERCODE", epoch: 1);
            Tick();
            Assert.Equal(SessionState.Joining, _session.State);

            var connects = _transport.Connects.Count;
            _channel.PeerAnnounces("PEERCODE", epoch: 1);
            Tick();
            _channel.PeerAnnounces("PEERCODE", epoch: 1);
            Tick();

            Assert.Equal(connects, _transport.Connects.Count);
            Assert.Equal(SessionState.Joining, _session.State);
        }

        [Fact]
        public void AnUnusableStoredSaltIsReplacedRatherThanReturned()
        {
            // Derive throws on a bad salt, and that exception escaped into
            // session startup on every world load with no way to recover.
            _storage.Contents = "{\"version\":1,\"salt\":\"not-valid-base64!!\",\"worlds\":{}}";

            var store = new ReclaimStore(_storage, _log);
            var salt = store.Salt;

            Assert.True(StableUid.TryDecodeSalt(salt, out var decoded));
            Assert.NotNull(StableUid.Derive("profile", decoded));
            Assert.True(_log.Contains("unusable"));
        }

        [Fact]
        public void ASaltTooShortToBeUsefulIsAlsoReplaced()
        {
            _storage.Contents = "{\"version\":1,\"salt\":\"dG9vc2hvcnQ=\",\"worlds\":{}}";

            var salt = new ReclaimStore(_storage, _log).Salt;
            Assert.True(StableUid.TryDecodeSalt(salt, out _));
        }

        [Theory]
        [InlineData("wss://host/ws?tenant=abc", "wss://host/ws?tenant=abc")]
        [InlineData("wss://host?tenant=abc", "wss://host/ws?tenant=abc")]
        [InlineData("wss://host/relay?x=1", "wss://host/relay/ws?x=1")]
        public void ThePathSuffixGoesInThePathNotAfterTheQuery(string input, string expected)
        {
            // "wss://host/ws?tenant=abc" was becoming "…?tenant=abc/ws".
            Assert.Equal(expected, RelayUrl.Normalise(input));
        }

        [Fact]
        public void NormalisedUrlsSurviveTheTransportsQueryBuilder()
        {
            var uri = ClientWebSocketTransport.BuildUri(
                RelayUrl.Normalise("wss://host/ws?tenant=abc"), "K7MQ2XR4", null);

            Assert.Equal("/ws", uri.AbsolutePath);
            Assert.Contains("tenant=abc", uri.Query);
            Assert.Contains("role=mod", uri.Query);
            Assert.Contains("code=K7MQ2XR4", uri.Query);
        }
    }
}
