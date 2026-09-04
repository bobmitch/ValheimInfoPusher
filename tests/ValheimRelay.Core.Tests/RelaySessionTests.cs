using System;
using System.Collections.Generic;
using System.Linq;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    /// <summary>
    /// The whole session lifecycle from PLAN.md §5, driven against a fake
    /// transport and a clock the test moves by hand — the M1 done-criterion.
    /// </summary>
    public class RelaySessionTests
    {
        private const string WorldUid = "5713";

        private readonly FakeClock _clock = new();
        private readonly FakeLog _log = new();
        private readonly FakeTransport _transport = new();
        private readonly FakeGameChannel _channel = new();
        private readonly FakePeerView _peers = new() { SelfPeerId = 10 };
        private readonly InMemoryReclaimStorage _storage = new();
        private readonly List<SessionNotice> _notices = new();
        private readonly SessionOptions _options;
        private readonly ReclaimStore _reclaim;
        private readonly RelaySession _session;

        public RelaySessionTests()
        {
            _options = new SessionOptions
            {
                RelayUrl = "ws://localhost:8080/ws",
                DiscoveryWindow = TimeSpan.FromSeconds(5),
                CreationStaggerSpread = TimeSpan.Zero,
                PositionInterval = TimeSpan.FromSeconds(1)
            };

            _reclaim = new ReclaimStore(_storage, _log);
            _session = new RelaySession(_options, _transport, _channel, _peers, _clock, _log, _reclaim, random: () => 0.5);
            _session.Notice += _notices.Add;
        }

        private static SessionIdentity Identity() => new(
            "Bob", "vh_7f3c9a21", "1.0.0", new WorldInfo("Midgard", "hAbC12dEf", -1234567, WorldUid));

        private void Tick(double seconds = 0)
        {
            if (seconds > 0) _clock.Advance(seconds);
            _session.Tick();
        }

        /// <summary>Runs the create path to a live session and returns the code.</summary>
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

        // --------------------------------------------------------------- create

        [Fact]
        public void StartEntersDiscoveryAndAsksPeersForACode()
        {
            _session.Start(Identity());

            Assert.Equal(SessionState.Discovering, _session.State);
            Assert.Equal(1, _channel.RequestCount);
            Assert.Empty(_transport.Connects);
        }

        [Fact]
        public void DoesNotConnectUntilTheDiscoveryWindowCloses()
        {
            _session.Start(Identity());
            Tick(4);
            Assert.Empty(_transport.Connects);

            Tick(2);
            Assert.Single(_transport.Connects);
        }

        [Fact]
        public void AnElectedCreatorConnectsWithNoCode()
        {
            _session.Start(Identity());
            Tick(6);

            var attempt = Assert.Single(_transport.Connects);
            Assert.Null(attempt.Code);
            Assert.Null(attempt.Token);
            Assert.Equal(SessionState.Creating, _session.State);
        }

        [Fact]
        public void WelcomeWithATokenMakesUsTheCreatorAndGoesActive()
        {
            var code = StartAsCreator();

            Assert.Equal(SessionState.Active, _session.State);
            Assert.Equal(code, _session.Code);
            Assert.True(_session.IsCreator);
        }

        [Fact]
        public void TheCreatorAnnouncesTheCodeOverTheGameChannelImmediately()
        {
            var code = StartAsCreator();
            Assert.Contains(_channel.Announced, a => a.Code == code);
        }

        [Fact]
        public void TheCodeIsSurfacedToThePlayerExactlyOnce()
        {
            var code = StartAsCreator();

            var started = _notices.Where(n => n.Kind == NoticeKind.SessionStarted).ToList();
            Assert.Single(started);
            Assert.Equal(code, started[0].Code);
        }

        [Fact]
        public void HelloIsTheFirstFrameSentOnEveryConnection()
        {
            StartAsCreator();
            Tick();

            Assert.NotEmpty(_transport.Sent);
            Assert.Equal(FrameTypes.Hello, JsonParser.Parse(_transport.Sent[0])["type"].AsString());
        }

        [Fact]
        public void TheReclaimTokenIsPersistedButNeverLogged()
        {
            StartAsCreator(token: "super-secret-token");

            var entry = _reclaim.Get(WorldUid);
            Assert.NotNull(entry);
            Assert.Equal("super-secret-token", entry!.Token);

            // §5.3/§8: BepInEx logs get pasted into support threads.
            Assert.DoesNotContain(_log.Lines, l => l.Contains("super-secret-token"));
            Assert.DoesNotContain(_notices, n => n.Message.Contains("super-secret-token"));
        }

        // ----------------------------------------------------------------- join

        [Fact]
        public void ACodeHeardDuringDiscoveryIsJoinedWithNoTyping()
        {
            _session.Start(Identity());
            _channel.PeerAnnounces("PEERCODE", epoch: 1);
            Tick();

            var attempt = Assert.Single(_transport.Connects);
            Assert.Equal("PEERCODE", attempt.Code);
            Assert.Null(attempt.Token);
            Assert.Equal(SessionState.Joining, _session.State);
        }

        [Fact]
        public void AJoinerGetsNoTokenAndPersistsNothing()
        {
            _session.Start(Identity());
            _channel.PeerAnnounces("PEERCODE", 1);
            Tick();
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("PEERCODE");
            Tick();

            Assert.Equal(SessionState.Active, _session.State);
            Assert.False(_session.IsCreator);
            Assert.Null(_reclaim.Get(WorldUid));
        }

        [Fact]
        public void ANonElectedClientWaitsInDiscoveryInsteadOfCreating()
        {
            _peers.SelfPeerId = 50;
            _peers.Peers.Add(10);

            _session.Start(Identity());
            Tick(30);

            Assert.Empty(_transport.Connects);
            Assert.Equal(SessionState.Discovering, _session.State);
        }

        [Fact]
        public void AWaitingClientReAsksForTheCodeOnTheSlowTimer()
        {
            _peers.SelfPeerId = 50;
            _peers.Peers.Add(10);

            _session.Start(Identity());
            var initial = _channel.RequestCount;
            Tick(11);
            Tick(11);

            Assert.True(_channel.RequestCount > initial + 1);
        }

        [Fact]
        public void CreationIsStaggeredSoTwoClientsLoadingTogetherDoNotBothCreate()
        {
            // Empty peer list on both sides is the real double-create race, so
            // the stagger has to come from the peer id rather than from rank.
            _options.CreationStaggerSpread = TimeSpan.FromSeconds(3);
            _peers.SelfPeerId = 1002; // a non-zero stagger for this id

            _session.Start(Identity());
            Tick(5.1);
            Assert.Empty(_transport.Connects);

            Tick(3);
            Assert.Single(_transport.Connects);
        }

        [Fact]
        public void ACodeArrivingDuringTheStaggerIsJoinedInsteadOfCreating()
        {
            // The point of the stagger: the later client hears the earlier one
            // and never creates a competing room at all.
            _options.CreationStaggerSpread = TimeSpan.FromSeconds(3);
            _peers.SelfPeerId = 1002;

            _session.Start(Identity());
            Tick(5.1);
            _channel.PeerAnnounces("EARLYCOD", epoch: 1);
            Tick();

            var attempt = Assert.Single(_transport.Connects);
            Assert.Equal("EARLYCOD", attempt.Code);
        }

        // ------------------------------------------------------- code broadcast

        [Fact]
        public void TheCreatorAnswersAPeerAskingForTheCode()
        {
            StartAsCreator();
            _channel.Announced.Clear();

            _channel.PeerRequestsCode();
            Tick();

            Assert.Single(_channel.Announced);
        }

        [Fact]
        public void ManySimultaneousAsksProduceOneAnnouncement()
        {
            StartAsCreator();
            _channel.Announced.Clear();

            for (var i = 0; i < 8; i++) _channel.PeerRequestsCode();
            Tick();

            Assert.Single(_channel.Announced);
        }

        [Fact]
        public void AJoinerDoesNotAnswerCodeRequests()
        {
            _session.Start(Identity());
            _channel.PeerAnnounces("PEERCODE", 1);
            Tick();
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("PEERCODE");
            Tick();
            _channel.Announced.Clear();

            _channel.PeerRequestsCode();
            Tick();

            Assert.Empty(_channel.Announced);
        }

        [Fact]
        public void TheCreatorReAnnouncesOnTheHeartbeatForLateJoiners()
        {
            StartAsCreator();
            _channel.Announced.Clear();

            Tick(31);
            Assert.Single(_channel.Announced);
        }

        // ---------------------------------------------------------- the tiebreak

        [Fact]
        public void TheLosingCreatorMigratesToTheSmallerCode()
        {
            StartAsCreator("MMMMMMMM");
            _channel.PeerAnnounces("AAAAAAAA", epoch: 1);
            Tick();

            Assert.Equal(SessionState.Joining, _session.State);
            Assert.Equal("AAAAAAAA", _transport.Connects.Last().Code);
        }

        [Fact]
        public void TheLosingCreatorAlsoDiscardsItsTokenForTheAbandonedRoom()
        {
            // PLAN.md §5.1 says to disconnect and join the winner but not what to
            // do with the token. Keeping it means the next load of this world
            // reclaims the dead room and splits the group all over again.
            StartAsCreator("MMMMMMMM");
            Assert.NotNull(_reclaim.Get(WorldUid));

            _channel.PeerAnnounces("AAAAAAAA", epoch: 1);
            Tick();

            Assert.Null(_reclaim.Get(WorldUid));
            Assert.False(_session.IsCreator);
        }

        [Fact]
        public void TheWinningCreatorStaysPutAndReAnnounces()
        {
            StartAsCreator("AAAAAAAA");
            _channel.Announced.Clear();
            var connectsBefore = _transport.Connects.Count;

            _channel.PeerAnnounces("MMMMMMMM", epoch: 1);
            Tick();

            Assert.Equal(SessionState.Active, _session.State);
            Assert.Equal(connectsBefore, _transport.Connects.Count);
            Assert.Single(_channel.Announced);
        }

        [Fact]
        public void MigratingDoesNotLeaveAPhantomReconnectBehind()
        {
            // Closing the losing connection raises Closed, which must not be read
            // as connection loss and schedule a reconnect over the join.
            StartAsCreator("MMMMMMMM");
            _channel.PeerAnnounces("AAAAAAAA", 1);
            Tick();

            // Inside the connect timeout: the only thing that could move the
            // state here is the abandoned connection's close being mistaken for
            // connection loss, which is what this guards.
            Tick(10);

            Assert.Equal(SessionState.Joining, _session.State);
            Assert.Equal("AAAAAAAA", _transport.Connects.Last().Code);
        }

        [Fact]
        public void AStaleAnnouncementDoesNotDragTheGroupBackToADeadCode()
        {
            StartAsCreator("ZZZZZZZZ", token: "t");
            var connects = _transport.Connects.Count;

            // A lagging peer is still announcing the previous generation.
            _channel.PeerAnnounces("AAAAAAAA", epoch: 0);
            Tick();

            Assert.Equal(SessionState.Active, _session.State);
            Assert.Equal(connects, _transport.Connects.Count);
        }

        // ------------------------------------------------------------ close codes

        [Fact]
        public void AGenericDropReconnectsToTheSameRoomRatherThanCreatingANewOne()
        {
            // §1.5: the room outlives its last client by ROOM_TTL, so the code
            // alone resumes the session. Creating instead would strand every
            // browser and every peer on the old code.
            var code = StartAsCreator();
            _transport.DropWith(1006);
            Tick();

            Assert.Equal(SessionState.Reconnecting, _session.State);

            Tick(2);
            var attempt = _transport.Connects.Last();
            Assert.Equal(code, attempt.Code);
            Assert.NotNull(attempt.Token);
        }

        [Fact]
        public void ReconnectsClimbTheBackoffLadder()
        {
            StartAsCreator();

            var delays = new List<double>();
            for (var i = 0; i < 4; i++)
            {
                var before = _transport.Connects.Count;
                _transport.DropWith(1006);
                Tick();

                var waited = 0.0;
                while (_transport.Connects.Count == before && waited < 60)
                {
                    Tick(0.25);
                    waited += 0.25;
                }

                delays.Add(waited);
                _transport.CompleteConnect();
                Tick();
                _transport.DeliverWelcome("K7MQ2XR4", token: "tok-1");
                Tick();
            }

            Assert.True(delays[1] > delays[0], $"expected growth, got {string.Join(",", delays)}");
            Assert.True(delays[3] > delays[1], $"expected growth, got {string.Join(",", delays)}");
        }

        [Fact]
        public void ALongHealthyConnectionResetsTheBackoffLadder()
        {
            StartAsCreator();
            for (var i = 0; i < 3; i++)
            {
                _transport.DropWith(1006);
                Tick();
                Tick(31);
                _transport.CompleteConnect();
                Tick();
                _transport.DeliverWelcome("K7MQ2XR4", token: "tok-1");
                Tick();
            }

            // 60 s of healthy connection (§5.2), then a drop: back to ~1 s.
            Tick(61);
            var before = _transport.Connects.Count;
            _transport.DropWith(1006);
            Tick();

            var waited = 0.0;
            while (_transport.Connects.Count == before && waited < 40)
            {
                Tick(0.25);
                waited += 0.25;
            }

            Assert.True(waited < 3, $"expected a reset ladder, waited {waited}s");
        }

        [Fact]
        public void TokenMismatchDiscardsTheStoredCredentialsAndStartsOver()
        {
            StartAsCreator();
            Assert.NotNull(_reclaim.Get(WorldUid));

            _transport.DropWith(CloseCodes.TokenMismatch);
            Tick();

            Assert.Null(_reclaim.Get(WorldUid));
            Assert.Equal(SessionState.Discovering, _session.State);
        }

        [Fact]
        public void AnExpiredCodeMakesTheCreatorBuildAFreshRoom()
        {
            StartAsCreator();
            _transport.DropWith(CloseCodes.UnknownCode);
            Tick();

            Assert.Equal(SessionState.Creating, _session.State);
            Assert.Null(_transport.Connects.Last().Code);
            Assert.Null(_reclaim.Get(WorldUid));
        }

        [Fact]
        public void AnExpiredCodeSendsAJoinerBackIntoDiscoveryAndTellsThePlayer()
        {
            // §5.3 rotation: the creator left for good, ROOM_TTL elapsed, and any
            // browser open on the old code is now pointed at a dead room.
            _session.Start(Identity());
            _channel.PeerAnnounces("PEERCODE", 1);
            Tick();
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("PEERCODE");
            Tick();

            _transport.DropWith(CloseCodes.UnknownCode);
            Tick();

            Assert.Equal(SessionState.Discovering, _session.State);
            Assert.Contains(_notices, n => n.Kind == NoticeKind.CodeChanged);
        }

        [Fact]
        public void ADeadCodeIsNotRejoinedWhenAPeerAnnouncesItAgain()
        {
            _session.Start(Identity());
            _channel.PeerAnnounces("PEERCODE", 1);
            Tick();
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("PEERCODE");
            Tick();

            _transport.DropWith(CloseCodes.UnknownCode);
            Tick();
            var connects = _transport.Connects.Count;

            _channel.PeerAnnounces("PEERCODE", 1);
            Tick();

            Assert.Equal(connects, _transport.Connects.Count);
        }

        [Fact]
        public void RoomFullStopsRetryingAndOffersAManualRetry()
        {
            StartAsCreator();
            _transport.DropWith(CloseCodes.RoomFull);
            Tick();

            Assert.Equal(SessionState.Blocked, _session.State);
            Assert.Contains(_notices, n => n.Kind == NoticeKind.RoomFull);

            var connects = _transport.Connects.Count;
            Tick(300);
            Assert.Equal(connects, _transport.Connects.Count);

            _session.Retry();
            Assert.Equal(SessionState.Discovering, _session.State);
        }

        [Fact]
        public void RelayFullBacksOffHarderThanAnOrdinaryDrop()
        {
            StartAsCreator();
            _transport.DropWith(CloseCodes.RelayFull);
            Tick();

            Assert.Equal(SessionState.Reconnecting, _session.State);
            Assert.Contains(_notices, n => n.Kind == NoticeKind.RelayBusy);

            var connects = _transport.Connects.Count;
            Tick(3);
            Assert.Equal(connects, _transport.Connects.Count);

            Tick(10);
            Assert.True(_transport.Connects.Count > connects);
        }

        // --------------------------------------------------------------- reclaim

        [Fact]
        public void TheNextLoadOfTheWorldReclaimsWithTheStoredCodeAndToken()
        {
            StartAsCreator("K7MQ2XR4", "tok-1");
            _session.Stop("logout");

            // A fresh session over the same store, as on the next world load.
            var transport = new FakeTransport();
            var session = new RelaySession(
                _options, transport, new FakeGameChannel(), _peers, _clock, _log, _reclaim, () => 0.5);

            session.Start(Identity());
            session.Tick();
            _clock.Advance(6);
            session.Tick();

            var attempt = Assert.Single(transport.Connects);
            Assert.Equal("K7MQ2XR4", attempt.Code);
            Assert.Equal("tok-1", attempt.Token);
        }

        [Fact]
        public void ReclaimingTheSameCodeDoesNotTellThePlayerItChanged()
        {
            StartAsCreator("K7MQ2XR4", "tok-1");
            _notices.Clear();

            _transport.DropWith(1006);
            Tick(3);
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("K7MQ2XR4", token: "tok-1");
            Tick();

            Assert.DoesNotContain(_notices, n => n.Kind == NoticeKind.CodeChanged);
        }

        [Fact]
        public void ARotatedCodeIsAnnouncedToThePlayerAsAChange()
        {
            StartAsCreator("K7MQ2XR4", "tok-1");
            _notices.Clear();

            _transport.DropWith(CloseCodes.UnknownCode);
            Tick();
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("NEWCODE1", token: "tok-2");
            Tick();

            var changed = Assert.Single(_notices, n => n.Kind == NoticeKind.CodeChanged);
            Assert.Equal("NEWCODE1", changed.Code);
            Assert.Contains("NEWCODE1", changed.Message);
        }

        [Fact]
        public void ARotatedRoomClaimsALaterGenerationThanTheDeadOne()
        {
            StartAsCreator("K7MQ2XR4", "tok-1");
            _transport.DropWith(CloseCodes.UnknownCode);
            Tick();
            _transport.CompleteConnect();
            Tick();
            _transport.DeliverWelcome("ZZZZZZZZ", token: "tok-2");
            Tick();
            _channel.Announced.Clear();
            _channel.PeerRequestsCode();
            Tick();

            var announcement = Assert.Single(_channel.Announced);
            Assert.Equal("ZZZZZZZZ", announcement.Code);
            Assert.True(announcement.Epoch > 1, "a rotated room must outrank the dead one");
        }

        // --------------------------------------------------------------- telemetry

        [Fact]
        public void PositionIsRateLimitedToTheConfiguredInterval()
        {
            StartAsCreator();
            _transport.Sent.Clear();

            for (var i = 0; i < 20; i++)
            {
                _session.SubmitPosition(Moving(i));
                Tick(0.1);
            }

            // 2 s of ticks at a 1 s interval is 2-3 frames, not 20.
            Assert.InRange(_transport.SentOfType(FrameTypes.Position).Count, 1, 3);
        }

        [Fact]
        public void PositionIsNotSentWhenTheSessionIsNotActive()
        {
            _session.Start(Identity());
            _session.SubmitPosition(Moving(1));
            Tick();

            Assert.Empty(_transport.SentOfType(FrameTypes.Position));
        }

        [Fact]
        public void OptingOutOfSharingStopsPositionButKeepsTheSession()
        {
            _options.SharePosition = false;
            StartAsCreator();

            // §8: the map must be told this is deliberate, not a stale client.
            var hello = _transport.SentOfType(FrameTypes.Hello).First();
            Assert.False(hello["share"].AsBool(true));

            _transport.Sent.Clear();
            for (var i = 0; i < 10; i++)
            {
                _session.SubmitPosition(Moving(i));
                Tick(1.1);
            }

            Assert.Empty(_transport.SentOfType(FrameTypes.Position));
            Assert.Equal(SessionState.Active, _session.State);
        }

        [Fact]
        public void HelloIsRepeatedOnTheHeartbeat()
        {
            StartAsCreator();
            _transport.Sent.Clear();

            Tick(61);
            Assert.Single(_transport.SentOfType(FrameTypes.Hello));
        }

        // ------------------------------------------------------------ request_state

        [Fact]
        public void RequestStateReplaysHelloForAMapJoiningMidSession()
        {
            StartAsCreator();
            Tick(10);
            _transport.Sent.Clear();

            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();

            var hello = Assert.Single(_transport.SentOfType(FrameTypes.Hello));
            Assert.Equal("Midgard", hello["world"]["name"].AsString());
        }

        [Fact]
        public void RequestStateAlsoReplaysOurMarkersSoAReloadedMapDoesNotLoseThem()
        {
            // §3.4 calls markers session-persistent but nothing in PLAN.md stores
            // them, and §3.5 replays only hello and position.
            StartAsCreator();
            _session.AddMarker(10, 20, "silver here", "ore");
            _session.AddMarker(30, 40, "home", "home");
            Tick(10);
            _transport.Sent.Clear();

            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();

            var markers = _transport.SentOfType(FrameTypes.Marker);
            Assert.Equal(2, markers.Count);
            Assert.Contains(markers, m => m["label"].AsString() == "silver here");
        }

        [Fact]
        public void ARemovedMarkerIsNotReplayed()
        {
            StartAsCreator();
            var id = _session.AddMarker(10, 20, "gone", "ore");
            _session.RemoveMarker(id!);
            Tick(10);
            _transport.Sent.Clear();

            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();

            Assert.Empty(_transport.SentOfType(FrameTypes.Marker));
        }

        [Fact]
        public void EightBrowsersReloadingAtOnceProduceOneReplay()
        {
            StartAsCreator();
            Tick(10);
            _transport.Sent.Clear();

            for (var i = 0; i < 8; i++) _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();

            Assert.Single(_transport.SentOfType(FrameTypes.Hello));
        }

        [Fact]
        public void ARequestArrivingInsideTheCooldownIsAnsweredWhenItExpires()
        {
            // §3.5 caps the rate but does not say what happens to a request that
            // lands inside the window. Dropping it would leave a map that
            // reloaded a second later waiting a full hello interval for any
            // world data, so the reply is coalesced rather than discarded.
            StartAsCreator();
            Tick(10);
            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();
            _transport.Sent.Clear();

            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick(1);
            Assert.Empty(_transport.SentOfType(FrameTypes.Hello));

            Tick(5);
            Assert.Single(_transport.SentOfType(FrameTypes.Hello));
        }

        [Fact]
        public void AReplayForcesTheNextPositionPastTheDeadBand()
        {
            StartAsCreator();
            _session.SubmitPosition(Moving(0));
            Tick(10);
            _transport.Sent.Clear();

            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();
            _session.SubmitPosition(Moving(0));
            Tick();

            Assert.Single(_transport.SentOfType(FrameTypes.Position));
        }

        // --------------------------------------------------------- outbound pings

        [Fact]
        public void APingMadeInGameReachesTheRoom()
        {
            StartAsCreator();
            _transport.Sent.Clear();

            _session.SendPing(123.5, -456.25);
            Tick();

            var ping = Assert.Single(_transport.SentOfType(FrameTypes.Ping));
            Assert.Equal(123.5, ping["x"].AsDouble(), 3);
            Assert.Equal(-456.25, ping["z"].AsDouble(), 3);

            // The name rides along so a browser can say whose ping it is: the
            // relay stamps a playerId on a mod's frame but nothing else.
            Assert.Equal("Bob", ping["name"].AsString());
        }

        [Fact]
        public void APingIsNotSentWhenTheSharingSwitchIsOff()
        {
            // §7 ShareMyPings. Separate from ShareMyPosition: a player who has
            // turned off the position stream has not asked to stop pinging.
            StartAsCreator();
            _transport.Sent.Clear();
            _options.SharePings = false;

            _session.SendPing(1, 2);
            Tick();

            Assert.Empty(_transport.SentOfType(FrameTypes.Ping));
        }

        [Fact]
        public void PositionSharingAndPingSharingAreIndependent()
        {
            StartAsCreator();
            _transport.Sent.Clear();
            _options.SharePosition = false;

            _session.SendPing(1, 2);
            Tick();

            Assert.Single(_transport.SentOfType(FrameTypes.Ping));
        }

        [Fact]
        public void APingMadeBeforeTheSessionIsLiveIsDroppedRatherThanQueued()
        {
            // A ping is "look here, now". Replaying one after a reconnect points
            // at a place nobody is standing any more, so it is dropped on the
            // floor rather than held in the reliable queue with the markers.
            _session.Start(Identity());
            _session.SendPing(1, 2);
            Tick(6);
            _transport.CompleteConnect();
            Tick();

            Assert.Empty(_transport.SentOfType(FrameTypes.Ping));
        }

        // ---------------------------------------------------------------- inbound

        [Fact]
        public void PingsAndMarkersFromTheMapAreSurfacedToTheGame()
        {
            StartAsCreator();
            PingFrame? ping = null;
            MarkerFrame? marker = null;
            _session.PingReceived += p => ping = p;
            _session.MarkerReceived += m => marker = m;

            _transport.Deliver("{\"type\":\"ping\",\"v\":1,\"x\":10,\"z\":20,\"name\":\"Asa\"}");
            _transport.Deliver("{\"type\":\"marker\",\"v\":1,\"op\":\"add\",\"id\":\"web:1\",\"x\":5,\"z\":6,\"icon\":\"boss\"}");
            Tick();

            Assert.Equal(10, ping!.Value.X);
            Assert.Equal("web:1", marker!.Id);
            Assert.Equal(MarkerIcons.Boss, marker.Icon);
        }

        [Fact]
        public void UnknownFrameTypesAndGarbageAreIgnoredNotFatal()
        {
            // §3: ignoring unknown types is what lets the map ship on its own
            // schedule. Garbage must not take the session down either.
            StartAsCreator();

            _transport.Deliver("{\"type\":\"fog_share\",\"v\":2,\"chunks\":[1,2,3]}");
            _transport.Deliver("not json at all");
            _transport.Deliver("{\"type\":\"marker\",\"op\":\"add\"}");
            _transport.Deliver("");
            Tick();

            Assert.Equal(SessionState.Active, _session.State);
        }

        [Fact]
        public void AMalformedWelcomeDropsTheConnectionAndReconnects()
        {
            _session.Start(Identity());
            Tick(6);
            _transport.CompleteConnect();
            _transport.Deliver("{\"type\":\"welcome\"}");
            Tick();

            Assert.Equal(SessionState.Reconnecting, _session.State);
        }

        // --------------------------------------------------------------- markers

        [Fact]
        public void MarkerIdsAreNamespacedWithOurUidSoTwoClientsCannotCollide()
        {
            StartAsCreator();
            var id = _session.AddMarker(1, 2, "x", "ore");
            Assert.StartsWith("vh_7f3c9a21:", id);
        }

        [Fact]
        public void MarkerCreationIsCappedSoAReplayCannotBeUnbounded()
        {
            StartAsCreator();
            for (var i = 0; i < MarkerStore.MaxOwnedMarkers; i++)
            {
                Assert.NotNull(_session.AddMarker(i, i, "m" + i, "dot"));
            }

            Assert.Null(_session.AddMarker(999, 999, "one too many", "dot"));
            Assert.Equal(MarkerStore.MaxOwnedMarkers, _session.Markers.Count);
        }

        [Fact]
        public void EveryReplayedMarkerFitsInsideOneFrame()
        {
            StartAsCreator();
            for (var i = 0; i < MarkerStore.MaxOwnedMarkers; i++)
            {
                _session.AddMarker(i, i, new string('x', 40), "ore");
            }
            Tick(10);
            _transport.Sent.Clear();

            _transport.Deliver("{\"type\":\"request_state\",\"v\":1}");
            Tick();

            Assert.All(_transport.Sent, f => Assert.True(FrameCodec.FitsInFrame(f), "frame exceeded the 8192-byte cap"));
        }

        // --------------------------------------------------------------- shutdown

        [Fact]
        public void StopClosesTheConnectionAndNeverRetries()
        {
            StartAsCreator();
            _session.Stop();

            Assert.Equal(SessionState.Stopped, _session.State);
            var connects = _transport.Connects.Count;
            Tick(600);

            // §5.2: a mod retrying against the relay from the main menu is a bug.
            Assert.Equal(connects, _transport.Connects.Count);
        }

        [Fact]
        public void StopDiscardsMarkersBecauseTheyDoNotOutliveTheSession()
        {
            StartAsCreator();
            _session.AddMarker(1, 2, "x", "ore");
            _session.Stop();

            Assert.Equal(0, _session.Markers.Count);
        }

        [Fact]
        public void ADropAfterStopIsIgnored()
        {
            StartAsCreator();
            _session.Stop();
            _transport.DropWith(1006);
            Tick(60);

            Assert.Equal(SessionState.Stopped, _session.State);
        }

        // ---------------------------------------------------------- backpressure

        [Fact]
        public void FramesAreHeldRatherThanLostWhenTheTransportRefuses()
        {
            StartAsCreator();
            _transport.Sent.Clear();
            _transport.AcceptSends = false;

            _session.AddMarker(1, 2, "important", "ore");
            Tick();
            Assert.Empty(_transport.Sent);

            _transport.AcceptSends = true;
            Tick();

            Assert.Single(_transport.SentOfType(FrameTypes.Marker));
        }

        [Fact]
        public void QueuedFramesAreDiscardedAcrossAReconnect()
        {
            StartAsCreator();
            _transport.AcceptSends = false;
            _session.AddMarker(1, 2, "stale", "ore");
            Tick();

            _transport.DropWith(1006);
            Tick();
            _transport.AcceptSends = true;
            Tick(3);
            _transport.CompleteConnect();
            Tick();
            _transport.Sent.Clear();
            _transport.DeliverWelcome("K7MQ2XR4", token: "tok-1");
            Tick();

            // A fresh hello, not a replay of the previous connection's backlog.
            Assert.Empty(_transport.SentOfType(FrameTypes.Marker));
            Assert.Single(_transport.SentOfType(FrameTypes.Hello));
        }

        private static PositionSample Moving(int step)
            => new(100 + (step * 5), 200 + (step * 5), 30, step * 10, "Meadows", 100, 100, true, false, 0);
    }
}
