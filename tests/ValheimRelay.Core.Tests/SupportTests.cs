using System;
using System.Collections.Generic;
using ValheimRelay.Core.Election;
using ValheimRelay.Core.Identity;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    public class BackoffTests
    {
        [Fact]
        public void ClimbsAndCapsAtThirtySeconds()
        {
            // Jitter off, so the ladder itself is what is under test.
            var backoff = new Backoff(jitterFraction: 0, random: () => 0.5);
            var delays = new List<double>();
            for (var i = 0; i < 8; i++) delays.Add(backoff.Next().TotalSeconds);

            Assert.Equal(new double[] { 1, 2, 4, 8, 16, 30, 30, 30 }, delays);
        }

        [Fact]
        public void ResetReturnsToTheBottomOfTheLadder()
        {
            var backoff = new Backoff(jitterFraction: 0, random: () => 0.5);
            for (var i = 0; i < 5; i++) backoff.Next();
            backoff.Reset();
            Assert.Equal(1, backoff.Next().TotalSeconds);
        }

        [Fact]
        public void JitterSpreadsSymmetricallyAroundTheNominalDelay()
        {
            var low = new Backoff(jitterFraction: 0.25, random: () => 0.0).Next().TotalSeconds;
            var high = new Backoff(jitterFraction: 0.25, random: () => 1.0).Next().TotalSeconds;

            Assert.Equal(0.75, low, 6);
            Assert.Equal(1.25, high, 6);
        }

        [Fact]
        public void RelayFullLadderStartsWellPastTheNormalOne()
        {
            // 4013 is transient but shed load: a herd retrying on the normal
            // ladder just re-arrives together (§5.2).
            var normal = new Backoff(jitterFraction: 0, random: () => 0.5);
            var relayFull = Backoff.ForRelayFull(() => 0.5);

            Assert.True(relayFull.Next() > normal.Next());
        }

        [Fact]
        public void NeverOverflowsOnALongOutage()
        {
            var backoff = new Backoff(jitterFraction: 0, random: () => 0.5);
            for (var i = 0; i < 1000; i++)
            {
                var delay = backoff.Next();
                Assert.InRange(delay.TotalSeconds, 0, 30);
            }
        }
    }

    public class CreatorElectionTests
    {
        [Fact]
        public void TheHostAlwaysCreates()
        {
            var peers = new FakePeerView { IsHost = true, SelfPeerId = 999 };
            peers.Peers.AddRange(new long[] { 1, 2, 3 });

            Assert.True(CreatorElection.IsElectedCreator(peers));
            Assert.Equal(0, CreatorElection.CreatorRank(peers));
        }

        [Fact]
        public void OtherwiseTheLowestPeerIdCreates()
        {
            var low = new FakePeerView { SelfPeerId = 10 };
            low.Peers.AddRange(new long[] { 20, 30 });
            Assert.True(CreatorElection.IsElectedCreator(low));

            var high = new FakePeerView { SelfPeerId = 30 };
            high.Peers.AddRange(new long[] { 10, 20 });
            Assert.False(CreatorElection.IsElectedCreator(high));
        }

        [Fact]
        public void ASoloClientCreates()
        {
            Assert.True(CreatorElection.IsElectedCreator(new FakePeerView { SelfPeerId = 42 }));
        }

        [Fact]
        public void EveryClientComputesTheSameWinner()
        {
            var ids = new long[] { 55, 12, 88, 3 };
            var winners = 0;
            foreach (var self in ids)
            {
                var view = new FakePeerView { SelfPeerId = self };
                foreach (var other in ids) view.Peers.Add(other);
                if (CreatorElection.IsElectedCreator(view)) winners++;
            }

            Assert.Equal(1, winners);
        }

        [Fact]
        public void TheHostNeverWaitsBeforeCreating()
        {
            var host = new FakePeerView { IsHost = true, SelfPeerId = 7 };
            Assert.Equal(TimeSpan.Zero, CreatorElection.CreationStagger(host, TimeSpan.FromSeconds(3)));
        }

        [Fact]
        public void TwoClientsThatCannotSeeEachOtherGetDifferentCreationDelays()
        {
            // The actual double-create race: both peer lists are empty, so both
            // clients are rank 0 and a rank-based stagger would not separate them.
            var spread = TimeSpan.FromSeconds(3);
            var a = CreatorElection.CreationStagger(new FakePeerView { SelfPeerId = 1001 }, spread);
            var b = CreatorElection.CreationStagger(new FakePeerView { SelfPeerId = 1002 }, spread);

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void TheCreationDelayIsStableForAGivenClient()
        {
            var spread = TimeSpan.FromSeconds(3);
            var peers = new FakePeerView { SelfPeerId = 4242 };
            Assert.Equal(
                CreatorElection.CreationStagger(peers, spread),
                CreatorElection.CreationStagger(peers, spread));
        }

        [Fact]
        public void CreationDelaysStayInsideTheSpreadAndDoNotClusterOnSequentialIds()
        {
            // Peer ids are frequently sequential; the mix has to spread them.
            var spread = TimeSpan.FromSeconds(3);
            var buckets = new HashSet<long>();
            for (long id = 5000; id < 5040; id++)
            {
                var delay = CreatorElection.CreationStagger(new FakePeerView { SelfPeerId = id }, spread);
                Assert.InRange(delay, TimeSpan.Zero, spread);
                buckets.Add((long)(delay.TotalSeconds * 4));
            }

            Assert.True(buckets.Count >= 8, $"delays clustered into only {buckets.Count} buckets");
        }
    }

    public class CodeArbiterTests
    {
        private readonly FakeClock _clock = new FakeClock();

        [Fact]
        public void AdoptsAnyCodeWhenItHasNone()
        {
            var arbiter = new CodeArbiter(_clock);
            Assert.Equal(CodeDecision.Adopt, arbiter.Consider(new CodeAnnouncement("ZZZZZZZZ", 1, 2)));
        }

        [Fact]
        public void WithinAGenerationTheSmallerCodeWins()
        {
            var arbiter = new CodeArbiter(_clock);
            arbiter.SetCurrent("MMMMMMMM", 1);

            Assert.Equal(CodeDecision.Adopt, arbiter.Consider(new CodeAnnouncement("AAAAAAAA", 1, 2)));

            arbiter.SetCurrent("MMMMMMMM", 1);
            Assert.Equal(CodeDecision.Defend, arbiter.Consider(new CodeAnnouncement("ZZZZZZZZ", 1, 2)));
        }

        [Fact]
        public void OurOwnCodeIsNeitherAdoptedNorDefended()
        {
            var arbiter = new CodeArbiter(_clock);
            arbiter.SetCurrent("K7MQ2XR4", 1);
            Assert.Equal(CodeDecision.Ignore, arbiter.Consider(new CodeAnnouncement("k7mq2xr4", 1, 2)));
        }

        [Fact]
        public void ALaterGenerationBeatsASmallerCode()
        {
            // The rotation case PLAN.md's tiebreak alone gets wrong: the group
            // has deliberately moved to a larger code and a lagging peer is still
            // announcing the dead smaller one.
            var arbiter = new CodeArbiter(_clock);
            arbiter.SetCurrent("ZZZZZZZZ", 2);

            Assert.Equal(CodeDecision.Defend, arbiter.Consider(new CodeAnnouncement("AAAAAAAA", 1, 2)));
        }

        [Fact]
        public void AnEarlierGenerationYieldsToALaterOne()
        {
            var arbiter = new CodeArbiter(_clock);
            arbiter.SetCurrent("AAAAAAAA", 1);
            Assert.Equal(CodeDecision.Adopt, arbiter.Consider(new CodeAnnouncement("ZZZZZZZZ", 2, 2)));
        }

        [Fact]
        public void ADeadCodeIsNotAdoptedAgain()
        {
            var arbiter = new CodeArbiter(_clock);
            arbiter.MarkDead("AAAAAAAA", 1);
            Assert.Equal(CodeDecision.Ignore, arbiter.Consider(new CodeAnnouncement("AAAAAAAA", 1, 2)));
        }

        [Fact]
        public void ADeadCodeReclaimedInALaterGenerationIsHeardAgain()
        {
            // Reclaim (§5.3) genuinely brings a code back to life, so the dead
            // list is keyed by generation rather than by code alone.
            var arbiter = new CodeArbiter(_clock);
            arbiter.MarkDead("AAAAAAAA", 1);
            Assert.Equal(CodeDecision.Adopt, arbiter.Consider(new CodeAnnouncement("AAAAAAAA", 2, 2)));
        }

        [Fact]
        public void MarkingTheCurrentCodeDeadClearsIt()
        {
            var arbiter = new CodeArbiter(_clock);
            arbiter.SetCurrent("AAAAAAAA", 1);
            arbiter.MarkDead("AAAAAAAA", 1);
            Assert.Null(arbiter.CurrentCode);
        }

        [Fact]
        public void DeadCodesAreForgottenAfterTheirTtl()
        {
            var arbiter = new CodeArbiter(_clock, TimeSpan.FromMinutes(10));
            arbiter.MarkDead("AAAAAAAA", 5);
            _clock.Advance(TimeSpan.FromMinutes(11));
            Assert.False(arbiter.IsKnownDead("AAAAAAAA", 5));
        }

        [Fact]
        public void ANewRoomClaimsAGenerationAboveAnythingHeard()
        {
            var arbiter = new CodeArbiter(_clock);
            arbiter.Consider(new CodeAnnouncement("AAAAAAAA", 7, 2));
            Assert.Equal(8, arbiter.NextEpoch());
        }

        [Fact]
        public void TwoClientsConvergeOnTheSameWinner()
        {
            // Both created at once; each hears the other. Exactly one migrates.
            var a = new CodeArbiter(_clock);
            var b = new CodeArbiter(_clock);
            a.SetCurrent("AAAAAAAA", 1);
            b.SetCurrent("BBBBBBBB", 1);

            var aDecision = a.Consider(new CodeAnnouncement("BBBBBBBB", 1, 2));
            var bDecision = b.Consider(new CodeAnnouncement("AAAAAAAA", 1, 1));

            Assert.Equal(CodeDecision.Defend, aDecision);
            Assert.Equal(CodeDecision.Adopt, bDecision);
        }
    }

    public class StableUidTests
    {
        [Fact]
        public void IsStableForTheSameProfileAndSalt()
        {
            var salt = StableUid.NewSalt();
            Assert.Equal(StableUid.Derive("76561198000000000", salt), StableUid.Derive("76561198000000000", salt));
        }

        [Fact]
        public void DiffersPerInstallSoItCannotBeCorrelatedAcrossPlayers()
        {
            var one = StableUid.Derive("76561198000000000", StableUid.NewSalt());
            var two = StableUid.Derive("76561198000000000", StableUid.NewSalt());
            Assert.NotEqual(one, two);
        }

        [Fact]
        public void IsNotAnUnsaltedHashOfTheProfileId()
        {
            // The point of the salt: the space of real platform ids is small
            // enough that a bare SHA-256 is invertible by brute force (§12.1).
            var salt = StableUid.NewSalt();
            var derived = StableUid.Derive("76561198000000000", salt);

            using var sha = System.Security.Cryptography.SHA256.Create();
            var bare = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("76561198000000000"));
            var bareHex = StableUid.Prefix + BitConverter.ToString(bare, 0, 8).Replace("-", "").ToLowerInvariant();

            Assert.NotEqual(bareHex, derived);
        }

        [Fact]
        public void IsPrefixedAndCarriesNoPlatformIdentifier()
        {
            var uid = StableUid.Derive("76561198000000000", StableUid.NewSalt());
            Assert.StartsWith(StableUid.Prefix, uid);
            Assert.DoesNotContain("76561198", uid);
            Assert.Equal(StableUid.Prefix.Length + StableUid.DigestChars, uid.Length);
        }

        [Fact]
        public void SaltSurvivesEncodingRoundTrip()
        {
            var salt = StableUid.NewSalt();
            Assert.True(StableUid.TryDecodeSalt(StableUid.EncodeSalt(salt), out var decoded));
            Assert.Equal(salt, decoded);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not base64!!")]
        [InlineData("dG9vc2hvcnQ=")]
        public void RejectsUnusableSalts(string? encoded)
        {
            Assert.False(StableUid.TryDecodeSalt(encoded, out _));
        }
    }

    public class OutboundQueueTests
    {
        [Fact]
        public void ReliableFramesGoAheadOfTelemetry()
        {
            // A marker must never wait behind a position frame (§4.2).
            var queue = new OutboundQueue();
            queue.SetPosition("pos");
            queue.EnqueueReliable("marker");

            Assert.True(queue.TryDequeue(out var first));
            Assert.Equal("marker", first);
            Assert.True(queue.TryDequeue(out var second));
            Assert.Equal("pos", second);
        }

        [Fact]
        public void OnlyTheLatestPositionSurvivesBackpressure()
        {
            var queue = new OutboundQueue();
            queue.SetPosition("a");
            queue.SetPosition("b");
            queue.SetPosition("c");

            Assert.True(queue.TryDequeue(out var frame));
            Assert.Equal("c", frame);
            Assert.False(queue.TryDequeue(out _));
            Assert.Equal(2, queue.SupersededPositions);
        }

        [Fact]
        public void ReliableFramesAreNeverDroppedToMakeRoomForTelemetry()
        {
            var queue = new OutboundQueue(reliableCapacity: 4);
            for (var i = 0; i < 4; i++) Assert.True(queue.EnqueueReliable("m" + i));
            for (var i = 0; i < 100; i++) queue.SetPosition("pos" + i);

            Assert.Equal(0, queue.DroppedReliable);
            Assert.Equal(5, queue.Count);
        }

        [Fact]
        public void AFullReliableQueueRefusesRatherThanReorderingMarkerOps()
        {
            var queue = new OutboundQueue(reliableCapacity: 2);
            Assert.True(queue.EnqueueReliable("add"));
            Assert.True(queue.EnqueueReliable("remove"));
            Assert.False(queue.EnqueueReliable("overflow"));

            // The add/remove pair kept its order; the newest was refused.
            Assert.True(queue.TryDequeue(out var first));
            Assert.Equal("add", first);
            Assert.Equal(1, queue.DroppedReliable);
        }

        [Fact]
        public void ClearDropsEverythingSoAReconnectDoesNotReplayAStalePosition()
        {
            var queue = new OutboundQueue();
            queue.EnqueueReliable("hello");
            queue.SetPosition("pos");
            queue.Clear();

            Assert.Equal(0, queue.Count);
            Assert.False(queue.TryDequeue(out _));
        }

        [Fact]
        public void IsSafeUnderConcurrentProducers()
        {
            var queue = new OutboundQueue(reliableCapacity: 100_000);
            System.Threading.Tasks.Parallel.For(0, 2000, i =>
            {
                if (i % 2 == 0) queue.EnqueueReliable("r" + i);
                else queue.SetPosition("p" + i);
            });

            var drained = 0;
            while (queue.TryDequeue(out _)) drained++;
            Assert.Equal(1001, drained); // 1000 reliable + one surviving position
        }
    }

    public class PositionThrottleTests
    {
        private static SessionOptions Options() => new SessionOptions
        {
            PositionMinMetres = 1.0,
            PositionMinRotationDegrees = 5.0,
            PositionKeepalive = TimeSpan.FromSeconds(10)
        };

        private static PositionSample At(double x, double z, double rot = 0, int hp = 100, bool dead = false, string biome = "Meadows")
            => new PositionSample(x, z, 0, rot, biome, hp, 100, true, dead, 0);

        [Fact]
        public void SendsTheFirstSampleUnconditionally()
        {
            Assert.True(new PositionThrottle(Options()).ShouldSend(At(0, 0), TimeSpan.Zero));
        }

        [Fact]
        public void SuppressesAPlayerStandingStill()
        {
            var throttle = new PositionThrottle(Options());
            var sample = At(10, 10);
            throttle.MarkSent(sample, TimeSpan.Zero);

            Assert.False(throttle.ShouldSend(At(10.2, 10.2), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void SendsOnceMovementPassesTheDeadBand()
        {
            var throttle = new PositionThrottle(Options());
            throttle.MarkSent(At(10, 10), TimeSpan.Zero);
            Assert.True(throttle.ShouldSend(At(11.5, 10), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void SendsAKeepaliveSoTheMapCanTellStillFromGone()
        {
            var throttle = new PositionThrottle(Options());
            throttle.MarkSent(At(10, 10), TimeSpan.Zero);

            Assert.False(throttle.ShouldSend(At(10, 10), TimeSpan.FromSeconds(9)));
            Assert.True(throttle.ShouldSend(At(10, 10), TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void DeathAndHealthChangesBypassTheDeadBand()
        {
            var throttle = new PositionThrottle(Options());
            throttle.MarkSent(At(10, 10, hp: 100), TimeSpan.Zero);

            Assert.True(throttle.ShouldSend(At(10, 10, hp: 40), TimeSpan.FromSeconds(1)));
            Assert.True(throttle.ShouldSend(At(10, 10, dead: true), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void CrossingABiomeBoundaryIsAlwaysWorthSending()
        {
            var throttle = new PositionThrottle(Options());
            throttle.MarkSent(At(10, 10, biome: "Meadows"), TimeSpan.Zero);
            Assert.True(throttle.ShouldSend(At(10, 10, biome: "BlackForest"), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void TurningOnTheSpotIsSentBecauseTheMapDrawsHeading()
        {
            var throttle = new PositionThrottle(Options());
            throttle.MarkSent(At(10, 10, rot: 0), TimeSpan.Zero);
            Assert.True(throttle.ShouldSend(At(10, 10, rot: 30), TimeSpan.FromSeconds(1)));
        }

        [Theory]
        [InlineData(359, 1, -2)]
        [InlineData(1, 359, 2)]
        [InlineData(180, 0, 180)]
        public void AngleDeltaTakesTheShortWayRound(double a, double b, double expected)
        {
            Assert.Equal(expected, PositionThrottle.AngleDelta(a, b), 6);
        }
    }

    public class RelayUrlTests
    {
        [Theory]
        [InlineData("wss://relay.example/ws", "wss://relay.example/ws")]
        [InlineData("ws://localhost:8080/ws", "ws://localhost:8080/ws")]
        // The mistakes a player handed an address by a friend will actually make.
        [InlineData("https://relay.example/ws", "wss://relay.example/ws")]
        [InlineData("http://localhost:8080/ws", "ws://localhost:8080/ws")]
        [InlineData("relay.example", "wss://relay.example/ws")]
        [InlineData("wss://relay.example", "wss://relay.example/ws")]
        [InlineData("wss://relay.example/", "wss://relay.example/ws")]
        [InlineData("  wss://relay.example/ws  ", "wss://relay.example/ws")]
        // Scheme and host normalise to lower case; the path does not, because paths are case-sensitive.
        [InlineData("WSS://RELAY.EXAMPLE/WS", "wss://relay.example/WS")]
        public void AbsorbsThePasteMistakesPlayersMake(string input, string expected)
        {
            Assert.Equal(expected, RelayUrl.Normalise(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("///")]
        public void FallsBackRatherThanProducingAnUnusableUrl(string? input)
        {
            Assert.Equal("ws://fallback/ws", RelayUrl.Normalise(input, "ws://fallback/ws"));
        }

        [Fact]
        public void AnEmptyConfigLandsOnTheShippedRelay()
        {
            // §2: a fresh install must need no edits, so a blank RelayUrl has to
            // resolve to something that works rather than to nothing.
            Assert.Equal(RelayUrl.Default, RelayUrl.Normalise(""));
            Assert.Equal(RelayUrl.Default, RelayUrl.Normalise(null));
        }

        [Fact]
        public void TheShippedDefaultIsAlreadyCanonicalAndEncrypted()
        {
            // This constant is in every install. A typo in it is a silent
            // failure for every player at once, and a ws:// slip would put every
            // player's live position on the wire in the clear (§8).
            Assert.Equal(RelayUrl.Default, RelayUrl.Normalise(RelayUrl.Default));
            Assert.StartsWith("wss://", RelayUrl.Default);
            Assert.EndsWith(RelayUrl.PathSuffix, RelayUrl.Default);
            Assert.False(RelayUrl.IsInsecure(RelayUrl.Default));
            Assert.True(Uri.TryCreate(RelayUrl.Default, UriKind.Absolute, out _));
        }

        [Fact]
        public void TheDefaultSessionOptionsPointAtTheShippedRelay()
        {
            var options = new SessionOptions();
            options.Normalise();
            Assert.Equal(RelayUrl.Default, options.RelayUrl);
        }

        [Fact]
        public void TheLocalDevelopmentUrlIsNotFlaggedAsInsecure()
        {
            // ws:// against the dev fixture is the legitimate plaintext case (§9, M2).
            Assert.False(RelayUrl.IsInsecure(RelayUrl.LocalDevelopment));
            Assert.Equal(RelayUrl.LocalDevelopment, RelayUrl.Normalise(RelayUrl.LocalDevelopment));
        }

        [Fact]
        public void TheShippedDefaultSurvivesTheTransportsQueryBuilder()
        {
            var uri = ClientWebSocketTransport.BuildUri(RelayUrl.Default, "K7MQ2XR4", "tok");

            Assert.Equal("wss", uri.Scheme);
            Assert.Equal("/ws", uri.AbsolutePath);
            Assert.Contains("role=mod", uri.Query);
            Assert.Contains("code=K7MQ2XR4", uri.Query);
            Assert.Contains("token=tok", uri.Query);
        }

        [Fact]
        public void ASchemelessHostDefaultsToTheSecureScheme()
        {
            // A player pasting a hostname must not silently end up unencrypted.
            Assert.StartsWith("wss://", RelayUrl.Normalise("relay.example"));
        }

        [Theory]
        [InlineData("ws://relay.example/ws", true)]
        [InlineData("ws://localhost:8080/ws", false)]
        [InlineData("ws://127.0.0.1:8080/ws", false)]
        [InlineData("wss://relay.example/ws", false)]
        public void InsecureUrlsAreFlaggedExceptForLocalDevelopment(string url, bool insecure)
        {
            Assert.Equal(insecure, RelayUrl.IsInsecure(url));
        }
    }

    public class MapLinkTests
    {
        private const string Code = "K7MQ2XR4";

        [Fact]
        public void APathHostedMapDoesNotGetAnExtraSlashBeforeTheFragment()
        {
            // §11.3 wrote the form as "<base>/#<code>", which for a map at a
            // path produces "/valheim/#CODE" and depends on the server
            // redirecting the trailing slash. Plenty do not.
            Assert.Equal("https://bobmitch.com/valheim#" + Code, MapLink.Build("https://bobmitch.com/valheim", Code));
        }

        [Fact]
        public void ARootHostedMapKeepsTheSlashSoTheUrlIsWellFormed()
        {
            Assert.Equal("https://map.example/#" + Code, MapLink.Build("https://map.example", Code));
        }

        [Theory]
        [InlineData("bobmitch.com/valheim")]
        [InlineData("https://bobmitch.com/valheim")]
        [InlineData("https://bobmitch.com/valheim/")]
        [InlineData("  https://bobmitch.com/valheim  ")]
        [InlineData("https://bobmitch.com/valheim#STALECODE")]
        public void AbsorbsThePasteMistakesPlayersMake(string configured)
        {
            Assert.Equal("https://bobmitch.com/valheim#" + Code, MapLink.Build(configured, Code));
        }

        [Fact]
        public void ASchemelessAddressBecomesHttps()
        {
            Assert.StartsWith("https://", MapLink.Build("bobmitch.com/valheim", Code));
        }

        [Fact]
        public void AWebsocketSchemeIsCorrectedBecauseBrowsersDoNotSpeakIt()
        {
            // Pasting the relay address into MapUrl is the obvious mix-up.
            Assert.Equal("https://bobmitch.com/valheim#" + Code, MapLink.Build("wss://bobmitch.com/valheim", Code));
            Assert.Equal("http://localhost:3000/#" + Code, MapLink.Build("ws://localhost:3000", Code));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a url at all")]
        public void WithoutAUsableMapThePlayerGetsTheBareCodeNotABrokenLink(string? configured)
        {
            // Handing someone a link that does not resolve is worse than
            // handing them the code and letting them find the map.
            Assert.Equal(Code, MapLink.Build(configured, Code));
        }

        [Fact]
        public void AQueryStringOnTheMapUrlIsPreserved()
        {
            Assert.Equal("https://map.example/v2?theme=dark#" + Code,
                MapLink.Build("https://map.example/v2?theme=dark", Code));
        }

        [Fact]
        public void TheCodeGoesInTheFragmentSoItNeverReachesTheMapsServer()
        {
            // §8: the code is the credential. A fragment is the only part of a
            // URL the browser does not send, so it stays out of access logs,
            // referrer headers and page analytics.
            var link = MapLink.Build(MapLink.Default, Code);
            var uri = new Uri(link);

            Assert.Equal("#" + Code, uri.Fragment);
            Assert.DoesNotContain(Code, uri.Query);
            Assert.DoesNotContain(Code, uri.AbsolutePath);
        }

        [Fact]
        public void TheShippedMapDefaultIsAlreadyCanonical()
        {
            // Compiled into every install: a typo fails for every player at once.
            Assert.Equal(MapLink.Default, MapLink.Normalise(MapLink.Default));
            Assert.StartsWith("https://", MapLink.Default);
            Assert.True(Uri.TryCreate(MapLink.Default, UriKind.Absolute, out _));
            Assert.EndsWith("#" + Code, MapLink.Build(MapLink.Default, Code));
        }

        [Fact]
        public void AnEmptyCodeNeverProducesADanglingLink()
        {
            Assert.Equal(string.Empty, MapLink.Build(MapLink.Default, ""));
        }
    }
}
