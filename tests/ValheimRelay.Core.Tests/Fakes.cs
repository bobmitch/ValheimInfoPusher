using System;
using System.Collections.Generic;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Core.Tests
{
    /// <summary>Time the test moves by hand. Nothing in Core reads a real clock.</summary>
    public sealed class FakeClock : IClock
    {
        public TimeSpan Elapsed { get; private set; }

        public long UnixTimeMilliseconds { get; set; } = 1_725_148_800_000;

        public void Advance(TimeSpan by)
        {
            Elapsed += by;
            UnixTimeMilliseconds += (long)by.TotalMilliseconds;
        }

        public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
    }

    public sealed class FakeLog : ILog
    {
        public List<string> Lines { get; } = new List<string>();

        public void Log(LogLevel level, string message) => Lines.Add(level + ": " + message);

        public bool Contains(string fragment)
            => Lines.Exists(l => l.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A transport the test drives directly: it records what the session asked
    /// for and lets the test play the relay's side back.
    /// </summary>
    public sealed class FakeTransport : IRelayTransport
    {
        public sealed record ConnectAttempt(string Url, string? Code, string? Token);

        public List<ConnectAttempt> Connects { get; } = new List<ConnectAttempt>();
        public List<string> Sent { get; } = new List<string>();
        public List<int> Closes { get; } = new List<int>();

        /// <summary>When false, <see cref="Send"/> refuses — the backpressure path.</summary>
        public bool AcceptSends { get; set; } = true;

        public TransportState State { get; private set; } = TransportState.Closed;

        public event Action? Opened;
        public event Action<string>? Received;
        public event Action<int, string>? Closed;

        public void Connect(string relayUrl, string? code, string? token)
        {
            Connects.Add(new ConnectAttempt(relayUrl, code, token));
            State = TransportState.Connecting;
        }

        public bool Send(string frame)
        {
            if (!AcceptSends) return false;
            Sent.Add(frame);
            return true;
        }

        public void Close(int code, string reason)
        {
            Closes.Add(code);
            if (State == TransportState.Closed) return;
            State = TransportState.Closed;
            Closed?.Invoke(code, reason);
        }

        // --- the relay's side, driven by the test ---

        public void CompleteConnect()
        {
            State = TransportState.Open;
            Opened?.Invoke();
        }

        public void Deliver(string frame) => Received?.Invoke(frame);

        public void DeliverWelcome(string code, string playerId = "p1", string? token = null, params RosterEntry[] roster)
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", FrameTypes.Welcome)
                .Prop("code", code)
                .Prop("playerId", playerId);
            if (token != null) w.Prop("token", token);
            w.Name("players").BeginArray();
            foreach (var entry in roster)
            {
                w.BeginObject()
                    .Prop("playerId", entry.PlayerId)
                    .Prop("name", entry.Name)
                    .Prop("uid", entry.Uid)
                    .EndObject();
            }
            w.EndArray().EndObject();
            Deliver(w.ToString());
        }

        /// <summary>The relay dropping us, as opposed to us closing.</summary>
        public void DropWith(int closeCode, string reason = "")
        {
            State = TransportState.Closed;
            Closed?.Invoke(closeCode, reason);
        }

        public IEnumerable<JsonValue> SentFrames()
        {
            foreach (var text in Sent)
            {
                if (JsonParser.TryParse(text, out var value)) yield return value;
            }
        }

        public List<JsonValue> SentOfType(string type)
        {
            var result = new List<JsonValue>();
            foreach (var frame in SentFrames())
            {
                if (frame["type"].AsString() == type) result.Add(frame);
            }
            return result;
        }
    }

    public sealed class FakeGameChannel : IGameChannel
    {
        public bool IsReady { get; set; } = true;
        public int RequestCount { get; private set; }
        public List<(string Code, long Epoch)> Announced { get; } = new List<(string, long)>();

        public event Action<CodeAnnouncement>? CodeAnnounced;
        public event Action? CodeRequested;

        public void RequestCode() => RequestCount++;

        public void AnnounceCode(string code, long epoch) => Announced.Add((code, epoch));

        // --- peers' side, driven by the test ---

        public void PeerAnnounces(string code, long epoch, long peerId = 2)
            => CodeAnnounced?.Invoke(new CodeAnnouncement(code, epoch, peerId));

        public void PeerRequestsCode() => CodeRequested?.Invoke();
    }

    public sealed class FakePeerView : IPeerView
    {
        public bool IsHost { get; set; }
        public long SelfPeerId { get; set; } = 100;
        public List<long> Peers { get; } = new List<long>();

        public IReadOnlyList<long> PeerIds => Peers;
    }

    public sealed class InMemoryReclaimStorage : IReclaimStorage
    {
        public string? Contents { get; set; }

        public int Writes { get; private set; }

        public string? Read() => Contents;

        public void Write(string contents)
        {
            Contents = contents;
            Writes++;
        }
    }
}
