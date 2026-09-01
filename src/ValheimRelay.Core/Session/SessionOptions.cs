using System;

namespace ValheimRelay.Core.Session
{
    public enum SessionState
    {
        Idle,
        Discovering,
        Creating,
        Joining,
        Active,
        Reconnecting,

        /// <summary>Terminal until the player asks to retry — room full (4008).</summary>
        Blocked,

        Stopped
    }

    /// <summary>Every tunable in one place, all defaulted from PLAN.md §1.5, §5 and §7.</summary>
    public sealed class SessionOptions
    {
        public string RelayUrl { get; set; } = Session.RelayUrl.Default;

        /// <summary>§5.1 step 1: how long to listen for a peer's code before acting.</summary>
        public TimeSpan DiscoveryWindow { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// A deterministic per-client delay, spread over this window, before
        /// creating a room — so two clients that loaded together and cannot yet
        /// see each other do not both create (§12.2).
        /// </summary>
        public TimeSpan CreationStaggerSpread { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>§5.1 step 3: a non-creator re-asks on this timer.</summary>
        public TimeSpan DiscoveryRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>§5.1 step 4: the creator re-announces on this heartbeat.</summary>
        public TimeSpan CodeAnnounceInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>§3.5 belt-and-braces: re-send <c>hello</c> on this timer.</summary>
        public TimeSpan HelloInterval { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>§7 <c>PositionInterval</c>, clamped to at least 0.5 s.</summary>
        public TimeSpan PositionInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>§3.5: at most one state replay per this window, across all maps.</summary>
        public TimeSpan RequestStateCooldown { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>§5.2: reset the backoff ladder after this much healthy connection.</summary>
        public TimeSpan HealthyConnectionThreshold { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Dead-band for position sends (§12.5). A player standing still emits a
        /// keepalive at <see cref="PositionKeepalive"/> instead of 1 Hz of
        /// identical frames.
        /// </summary>
        public double PositionMinMetres { get; set; } = 1.0;

        public double PositionMinRotationDegrees { get; set; } = 5.0;

        public TimeSpan PositionKeepalive { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Whether this client streams its own position (§7 <c>ShareMyPosition</c>).</summary>
        public bool SharePosition { get; set; } = true;

        /// <summary>
        /// How long to wait for <c>welcome</c> after the socket opens before
        /// giving up and retrying.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Must comfortably exceed a full state replay — one <c>hello</c> plus up
        /// to <see cref="MarkerStore.MaxOwnedMarkers"/> markers (§12.4) — or the
        /// replay drops its own tail and a reloaded map silently loses markers.
        /// </summary>
        public int OutboundReliableCapacity { get; set; } = MarkerStore.MaxOwnedMarkers + 32;

        public SessionOptions Clone() => (SessionOptions)MemberwiseClone();

        /// <summary>Applies the clamps the config UI promises, so a hand-edited file cannot break the relay contract.</summary>
        public void Normalise()
        {
            if (PositionInterval < TimeSpan.FromSeconds(0.5)) PositionInterval = TimeSpan.FromSeconds(0.5);
            if (DiscoveryWindow < TimeSpan.FromSeconds(1)) DiscoveryWindow = TimeSpan.FromSeconds(1);
            if (RequestStateCooldown < TimeSpan.FromSeconds(1)) RequestStateCooldown = TimeSpan.FromSeconds(1);
            if (HelloInterval < TimeSpan.FromSeconds(10)) HelloInterval = TimeSpan.FromSeconds(10);
            if (PositionKeepalive < PositionInterval) PositionKeepalive = PositionInterval;
            if (ConnectTimeout < TimeSpan.FromSeconds(5)) ConnectTimeout = TimeSpan.FromSeconds(5);
            // A replay is hello + every owned marker in one go.
            var replayFloor = MarkerStore.MaxOwnedMarkers + 8;
            if (OutboundReliableCapacity < replayFloor) OutboundReliableCapacity = replayFloor;
        }
    }

    /// <summary>Things the player should be told about (§7).</summary>
    public enum NoticeKind
    {
        SessionStarted,
        CodeChanged,
        Disconnected,
        Reconnecting,
        RoomFull,
        RelayBusy,
        Stopped
    }

    public sealed class SessionNotice
    {
        public SessionNotice(NoticeKind kind, string message, string? code = null)
        {
            Kind = kind;
            Message = message;
            Code = code;
        }

        public NoticeKind Kind { get; }
        public string Message { get; }
        public string? Code { get; }
    }
}
