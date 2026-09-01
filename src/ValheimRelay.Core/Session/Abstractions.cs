using System;
using System.Collections.Generic;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// Injected time. Everything in Core that waits, backs off or rate-limits
    /// goes through this, which is what makes the whole lifecycle testable in
    /// milliseconds (§4.1).
    /// </summary>
    public interface IClock
    {
        /// <summary>Monotonic time since some arbitrary origin. Use for durations.</summary>
        TimeSpan Elapsed { get; }

        /// <summary>Unix milliseconds. Use only for the advisory <c>t</c> field (§3).</summary>
        long UnixTimeMilliseconds { get; }
    }

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public interface ILog
    {
        void Log(LogLevel level, string message);
    }

    public static class LogExtensions
    {
        public static void Debug(this ILog log, string message) => log.Log(LogLevel.Debug, message);
        public static void Info(this ILog log, string message) => log.Log(LogLevel.Info, message);
        public static void Warn(this ILog log, string message) => log.Log(LogLevel.Warning, message);
        public static void Error(this ILog log, string message) => log.Log(LogLevel.Error, message);
    }

    public enum TransportState
    {
        Closed,
        Connecting,
        Open
    }

    /// <summary>
    /// The WebSocket, as Core sees it. Implementations connect off the main
    /// thread and report back through the events; Core never blocks on it.
    /// </summary>
    public interface IRelayTransport
    {
        TransportState State { get; }

        /// <summary>
        /// Begin connecting. <paramref name="code"/> null means "create a room"
        /// (§1.1). <paramref name="token"/> is only ever sent with a code.
        /// </summary>
        void Connect(string relayUrl, string? code, string? token);

        /// <summary>Queue a text frame. Returns false if the frame was dropped.</summary>
        bool Send(string frame);

        void Close(int code, string reason);

        /// <summary>Raised when the socket opens. Not necessarily the main thread.</summary>
        event Action? Opened;

        /// <summary>Raised per inbound text frame. Not necessarily the main thread.</summary>
        event Action<string>? Received;

        /// <summary>Raised once per connection when it ends, with the close code (§1.4).</summary>
        event Action<int, string>? Closed;
    }

    /// <summary>
    /// The in-game side channel that carries the session code between modded
    /// clients (§5.1). Implemented over a routed RPC, with chat as the fallback.
    /// </summary>
    public interface IGameChannel
    {
        /// <summary>True once the channel is usable (world loaded, RPC registered).</summary>
        bool IsReady { get; }

        /// <summary>Ask peers to announce the current code.</summary>
        void RequestCode();

        /// <summary>Announce a code to every peer.</summary>
        void AnnounceCode(string code, long epoch);

        /// <summary>A peer announced a code.</summary>
        event Action<CodeAnnouncement>? CodeAnnounced;

        /// <summary>A peer asked for the code.</summary>
        event Action? CodeRequested;
    }

    /// <summary>A code heard over the game channel, with the epoch that orders it (§12.3).</summary>
    public readonly struct CodeAnnouncement
    {
        public CodeAnnouncement(string code, long epoch, long senderPeerId)
        {
            Code = code;
            Epoch = epoch;
            SenderPeerId = senderPeerId;
        }

        public string Code { get; }

        /// <summary>
        /// Monotonic per-creator counter. Guards against a lagging peer's stale
        /// announcement dragging the group back onto a dead code.
        /// </summary>
        public long Epoch { get; }

        public long SenderPeerId { get; }
    }

    /// <summary>The set of peers the mod can see, as the election needs it (§5.1).</summary>
    public interface IPeerView
    {
        /// <summary>True when this client is the world's host.</summary>
        bool IsHost { get; }

        /// <summary>This client's own peer id.</summary>
        long SelfPeerId { get; }

        /// <summary>Connected peer ids, excluding self.</summary>
        IReadOnlyList<long> PeerIds { get; }
    }
}
