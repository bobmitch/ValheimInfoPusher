using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    /// <summary>
    /// Drives the real session over a real WebSocket against the dev relay in
    /// <c>tools/devrelay</c> — PLAN.md §10's "integration against the real
    /// relay", as close as this repository can get to it.
    /// <para>
    /// Skipped automatically when the Go toolchain is not present, so the unit
    /// suite still runs anywhere.
    /// </para>
    /// </summary>
    [Collection("devrelay")]
    public class IntegrationTests : IDisposable
    {
        private readonly DevRelay? _relay;

        public IntegrationTests()
        {
            _relay = DevRelay.TryStart();
        }

        public void Dispose() => _relay?.Dispose();

        private bool NoToolchain => _relay == null;

        [SkippableFact]
        public async Task AModCreatesARoomStreamsTelemetryAndAMapSeesIt()
        {
            Xunit.Skip.If(NoToolchain, "the Go toolchain is not available");

            using var host = new SessionHost(_relay!.Url);
            var code = await host.StartAndWaitForCodeAsync();

            Assert.Equal(8, code.Length);
            Assert.True(host.Session.IsCreator);

            using var map = await StubMap.ConnectAsync(_relay.Url, code);

            var hello = await map.WaitForAsync(FrameTypes.Hello);
            Assert.Equal("Bob", hello["name"].AsString());
            Assert.Equal("Midgard", hello["world"]["name"].AsString());
            Assert.False(string.IsNullOrEmpty(hello["playerId"].AsString()));

            host.Move(120.5, -340.25);
            var position = await map.WaitForAsync(FrameTypes.Position);
            Assert.Equal(120.5, position["x"].AsDouble(), 2);
            Assert.Equal(-340.25, position["z"].AsDouble(), 2);
        }

        [SkippableFact]
        public async Task ASecondModJoinsTheCodeAndBothReachTheMap()
        {
            Xunit.Skip.If(NoToolchain, "the Go toolchain is not available");

            using var creator = new SessionHost(_relay!.Url, playerName: "Bob", uid: "vh_bob");
            var code = await creator.StartAndWaitForCodeAsync();

            using var joiner = new SessionHost(_relay.Url, playerName: "Asa", uid: "vh_asa");
            await joiner.JoinAsync(code);

            Assert.False(joiner.Session.IsCreator);
            Assert.Equal(code, joiner.Session.Code);

            using var map = await StubMap.ConnectAsync(_relay.Url, code);
            creator.Move(10, 10);
            joiner.Move(-10, -10);

            var names = new HashSet<string>();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (names.Count < 2 && DateTime.UtcNow < deadline)
            {
                var hello = await map.WaitForAsync(FrameTypes.Hello);
                var name = hello["name"].AsString();
                if (name != null) names.Add(name);
            }

            Assert.Equal(new[] { "Asa", "Bob" }, names.OrderBy(n => n).ToArray());
        }

        [SkippableFact]
        public async Task RequestStateFromAMapJoiningMidSessionReplaysHelloAndMarkers()
        {
            Xunit.Skip.If(NoToolchain, "the Go toolchain is not available");

            using var host = new SessionHost(_relay!.Url);
            var code = await host.StartAndWaitForCodeAsync();

            host.Invoke(s => s.AddMarker(500, 600, "silver here", "ore"));
            await Task.Delay(200);

            // A browser opening (or reloading) well after the session began: it
            // sends request_state and the mod replays what it missed.
            using var map = await StubMap.ConnectAsync(_relay.Url, code);

            var hello = await map.WaitForAsync(FrameTypes.Hello);
            Assert.Equal("Midgard", hello["world"]["name"].AsString());

            var marker = await map.WaitForAsync(FrameTypes.Marker);
            Assert.Equal("silver here", marker["label"].AsString());
            Assert.Equal(MarkerIcons.Ore, marker["icon"].AsString());
        }

        [SkippableFact]
        public async Task AMapPingReachesTheMod()
        {
            Xunit.Skip.If(NoToolchain, "the Go toolchain is not available");

            using var host = new SessionHost(_relay!.Url);
            var code = await host.StartAndWaitForCodeAsync();

            var pings = new ConcurrentQueue<PingFrame>();
            host.Session.PingReceived += p => pings.Enqueue(p);

            using var map = await StubMap.ConnectAsync(_relay.Url, code);
            await map.SendAsync("{\"type\":\"ping\",\"v\":1,\"x\":42,\"z\":-42,\"name\":\"web\"}");

            var ping = await host.WaitAsync(() => pings.TryDequeue(out var p) ? p : (PingFrame?)null);
            Assert.Equal(42, ping.X);
            Assert.Equal(-42, ping.Z);
        }

        [SkippableFact]
        public async Task TheConnectionSurvivesPastTheServerPingInterval()
        {
            // §4.2: if control pings go unanswered the 60 s read deadline drops
            // the connection every minute and it looks like a network problem.
            // The relay is run here with a 1 s ping and a 2 s deadline so the
            // failure would show up in seconds rather than in someone's game.
            Xunit.Skip.If(NoToolchain, "the Go toolchain is not available");

            using var strict = DevRelay.Start(extraArgs: "-ping-interval 500ms -read-deadline 1500ms");
            using var host = new SessionHost(strict.Url);
            await host.StartAndWaitForCodeAsync();

            await host.PumpForAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SessionState.Active, host.Session.State);
        }

        [SkippableFact]
        public async Task AFullRoomStopsRetryingInsteadOfHammeringTheRelay()
        {
            Xunit.Skip.If(NoToolchain, "the Go toolchain is not available");

            using var tiny = DevRelay.Start(extraArgs: "-max-mods 1");
            using var creator = new SessionHost(tiny.Url);
            var code = await creator.StartAndWaitForCodeAsync();

            using var refused = new SessionHost(tiny.Url, playerName: "Third");
            refused.Start();
            refused.Announce(code);

            await refused.WaitForStateAsync(SessionState.Blocked);
            Assert.Equal(SessionState.Blocked, refused.Session.State);
        }

        [SkippableFact]
        public async Task ADroppedConnectionResumesTheSameRoom()
        {
            Xunit.Skip.If(NoToolchain, "the Go toolchain is not available");

            using var host = new SessionHost(_relay!.Url);
            var code = await host.StartAndWaitForCodeAsync();

            host.ForceDrop();
            await host.WaitForStateAsync(SessionState.Active, TimeSpan.FromSeconds(20));

            // §1.5: the room outlives its last client, so the code still works.
            Assert.Equal(code, host.Session.Code);
        }
    }
}
