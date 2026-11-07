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

        /// <summary>
        /// How long a client that was NOT elected creator waits for a code before
        /// creating a room anyway (plus its stagger), measured from entering
        /// discovery.
        /// <para>
        /// The election can only see game peers, not which of them run the mod.
        /// On a dedicated server a client's only peer is the server itself, which
        /// never creates — so without this, any client whose id happened to sort
        /// above the server's waited in discovery for ever. The same happens when
        /// the elected player is a host without the mod. The election still
        /// decides who goes first; this only stops it being a gate. Duplicate
        /// rooms this produces are merged by the code tiebreak (§5.1).
        /// </para>
        /// <para>
        /// Must exceed the elected creator's worst case — the window, its
        /// stagger, connecting and announcing — or non-elected clients race it.
        /// </para>
        /// </summary>
        public TimeSpan UnelectedCreateAfter { get; set; } = TimeSpan.FromSeconds(12);

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
        /// Whether this client forwards its own in-game pings to the room (§7
        /// <c>ShareMyPings</c>).
        /// <para>
        /// Separate from <see cref="SharePosition"/> on purpose: a ping is a
        /// deliberate act aimed at the people you are playing with, whereas
        /// position is a continuous stream you may simply not want running. The
        /// player who turns one off has not asked for the other.
        /// </para>
        /// </summary>
        public bool SharePings { get; set; } = true;

        /// <summary>
        /// Whether this client shares its fog of war (§3.7 <c>ShareMyFog</c>).
        /// Off by default, unlike position: fog is history, not just "now".
        /// Has no effect while <see cref="SharePosition"/> is off, because a
        /// live tile shows the edge of the fog moving, and that edge is where
        /// the player is.
        /// </summary>
        public bool ShareFog { get; set; }

        /// <summary>§3.7 live updates: changed tiles go out on this cadence. Clamped to at least 5 s.</summary>
        public TimeSpan FogInterval { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// §3.7: at most one snapshot per this window, coalesced as §12.5 does
        /// for <c>request_state</c>. Six times that cooldown because a snapshot
        /// can be 100 KB, and the relay delivers it to every map in the room.
        /// </summary>
        public TimeSpan FogSnapshotCooldown { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>§3.7 pacing: the minimum gap between two fog frames.</summary>
        public TimeSpan FogFrameGap { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// How often every tile the map already holds is re-checked, so that
        /// exploration not centred on the player — a console command, or
        /// anything a later game build adds — still reaches the map. Tiles that
        /// come out unchanged cost an encode and nothing on the wire.
        /// </summary>
        public TimeSpan FogSweepInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Bounds the main-thread work of one tick: tiles that encode to
        /// nothing worth sending (empty, or unchanged) are skipped without
        /// waiting for <see cref="FogFrameGap"/>, but no more than this many.
        /// </summary>
        public int FogTilesExaminedPerTick { get; set; } = 32;

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
            var electedWorstCase = DiscoveryWindow + CreationStaggerSpread;
            if (UnelectedCreateAfter < electedWorstCase) UnelectedCreateAfter = electedWorstCase;
            if (RequestStateCooldown < TimeSpan.FromSeconds(1)) RequestStateCooldown = TimeSpan.FromSeconds(1);
            if (HelloInterval < TimeSpan.FromSeconds(10)) HelloInterval = TimeSpan.FromSeconds(10);
            if (PositionKeepalive < PositionInterval) PositionKeepalive = PositionInterval;
            if (ConnectTimeout < TimeSpan.FromSeconds(5)) ConnectTimeout = TimeSpan.FromSeconds(5);
            if (FogInterval < TimeSpan.FromSeconds(5)) FogInterval = TimeSpan.FromSeconds(5);
            if (FogSnapshotCooldown < TimeSpan.FromSeconds(5)) FogSnapshotCooldown = TimeSpan.FromSeconds(5);
            if (FogFrameGap < TimeSpan.FromMilliseconds(20)) FogFrameGap = TimeSpan.FromMilliseconds(20);
            if (FogSweepInterval < FogInterval) FogSweepInterval = FogInterval;
            if (FogTilesExaminedPerTick < 1) FogTilesExaminedPerTick = 1;
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
