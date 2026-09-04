using System;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    /// <summary>
    /// The game-side half of ping duplication. A ping is already broadcast to
    /// every player by Valheim itself, so once a mod forwards one, §3.3's peer
    /// fan-out hands the same ping back to clients that have just drawn it.
    /// </summary>
    public class PingEchoTests
    {
        private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

        [Fact]
        public void TheRelayedCopyOfAPingAlreadySeenIsSuppressed()
        {
            var echo = new PingEcho();
            echo.Observe(100, -200, At(1));

            Assert.True(echo.ShouldSuppress(100, -200, At(1.4)));
        }

        [Fact]
        public void APingNobodySawInGameIsDrawn()
        {
            // The whole point of the feature: a browser pinging a place no
            // player pinged must reach the game.
            var echo = new PingEcho();
            echo.Observe(100, -200, At(1));

            Assert.False(echo.ShouldSuppress(400, -200, At(1.1)));
        }

        [Fact]
        public void AMatchIsConsumedSoASecondPingAtTheSameSpotStillDraws()
        {
            // NOT a mute window. Somebody pings a rock; a browser then pings the
            // same rock deliberately. The second one is a real message and
            // swallowing it would be indistinguishable, to the player, from the
            // web map being broken.
            var echo = new PingEcho();
            echo.Observe(10, 10, At(1));

            Assert.True(echo.ShouldSuppress(10, 10, At(1.2)));
            Assert.False(echo.ShouldSuppress(10, 10, At(1.3)));
        }

        [Fact]
        public void TwoPingsObservedSwallowTwoCopies()
        {
            var echo = new PingEcho();
            echo.Observe(10, 10, At(1));
            echo.Observe(10, 10, At(2));

            Assert.True(echo.ShouldSuppress(10, 10, At(2.1)));
            Assert.True(echo.ShouldSuppress(10, 10, At(2.2)));
            Assert.False(echo.ShouldSuppress(10, 10, At(2.3)));
        }

        [Fact]
        public void ACopyThatArrivesAfterTheWindowIsDrawnRatherThanLost()
        {
            // A late arrival is far more likely to be a fresh ping than a copy
            // of one from eight seconds ago, and the failure directions are not
            // symmetric: a spurious double ping is noise, a swallowed one is a
            // message that never arrived.
            var echo = new PingEcho(window: TimeSpan.FromSeconds(8));
            echo.Observe(10, 10, At(1));

            Assert.False(echo.ShouldSuppress(10, 10, At(9.5)));
        }

        [Fact]
        public void SlackOnThePositionIsBoundedByTheMatchRadius()
        {
            var echo = new PingEcho(matchRadius: 2.0);
            echo.Observe(0, 0, At(1));
            Assert.False(echo.ShouldSuppress(0, 3, At(1.1)));

            echo.Observe(0, 0, At(1));
            Assert.True(echo.ShouldSuppress(1.0, 1.0, At(1.1)));
        }

        [Fact]
        public void WhatIsTrackedIsBounded()
        {
            // A player leaning on the ping key with the relay unreachable must
            // not grow this without limit.
            var echo = new PingEcho();
            for (var i = 0; i < 500; i++) echo.Observe(i, i, At(1));

            Assert.True(echo.Tracked <= 32);
        }

        [Fact]
        public void AClockThatGoesBackwardsExpiresRatherThanTraps()
        {
            // Time.realtimeSinceStartup resets on a scene load. An entry from
            // "the future" would otherwise sit there swallowing real pings.
            var echo = new PingEcho();
            echo.Observe(10, 10, At(100));

            Assert.False(echo.ShouldSuppress(10, 10, At(1)));
        }

        [Fact]
        public void ClearDropsEverything()
        {
            var echo = new PingEcho();
            echo.Observe(10, 10, At(1));
            echo.Clear();

            Assert.Equal(0, echo.Tracked);
            Assert.False(echo.ShouldSuppress(10, 10, At(1.1)));
        }
    }
}
