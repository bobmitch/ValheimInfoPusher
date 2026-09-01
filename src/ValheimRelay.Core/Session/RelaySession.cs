using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ValheimRelay.Core.Election;
using ValheimRelay.Core.Json;
using ValheimRelay.Core.Protocol;

namespace ValheimRelay.Core.Session
{
    /// <summary>Everything the session needs to identify this player and world.</summary>
    public sealed class SessionIdentity
    {
        public SessionIdentity(string playerName, string uid, string modVersion, WorldInfo world)
        {
            PlayerName = playerName;
            Uid = uid;
            ModVersion = modVersion;
            World = world;
        }

        public string PlayerName { get; }
        public string Uid { get; }
        public string ModVersion { get; }
        public WorldInfo World { get; }
    }

    /// <summary>
    /// The connection and session lifecycle of PLAN.md §5, with no game and no
    /// Unity anywhere in it.
    /// <para>
    /// Threading: transport and game-channel callbacks arrive on arbitrary
    /// threads and do nothing but enqueue. All state lives behind
    /// <see cref="Tick"/>, which the plugin calls from <c>Update</c>. That keeps
    /// the state machine single-threaded, which is both why it is testable and
    /// why it is safe to touch game objects from its events.
    /// </para>
    /// </summary>
    public sealed class RelaySession : IDisposable
    {
        private enum SocketEventKind { Opened, Received, Closed }

        private readonly struct SocketEvent
        {
            public SocketEvent(SocketEventKind kind, string text, int code)
            {
                Kind = kind;
                Text = text;
                Code = code;
            }

            public SocketEventKind Kind { get; }
            public string Text { get; }
            public int Code { get; }
        }

        private readonly SessionOptions _options;
        private readonly IRelayTransport _transport;
        private readonly IGameChannel _gameChannel;
        private readonly IPeerView _peers;
        private readonly IClock _clock;
        private readonly ILog _log;
        private readonly ReclaimStore _reclaim;
        private readonly CodeArbiter _arbiter;
        private readonly OutboundQueue _outbound;
        private readonly PositionThrottle _throttle;
        private readonly MarkerStore _markers = new MarkerStore();
        private readonly Backoff _backoff;
        private readonly Backoff _relayFullBackoff;

        private readonly ConcurrentQueue<SocketEvent> _socketEvents = new ConcurrentQueue<SocketEvent>();
        private readonly ConcurrentQueue<CodeAnnouncement> _announcements = new ConcurrentQueue<CodeAnnouncement>();
        private int _codeRequests;

        private SessionIdentity? _identity;
        private SessionState _state = SessionState.Idle;
        private bool _disposed;

        // Timers, all in monotonic Elapsed terms.
        private TimeSpan _stateEnteredAt;
        private TimeSpan _retryAt;
        private TimeSpan _lastDiscoveryAskAt;
        private TimeSpan _lastAnnounceAt;
        private TimeSpan _lastHelloAt;
        private TimeSpan _lastPositionAt;
        private TimeSpan _connectionOpenedAt;
        private TimeSpan _lastStateReplayAt;
        private bool _stateReplayPending;
        private bool _healthyResetDone;

        // A close we asked for (migrating to a winning code, shutting down) is
        // still delivered as a Closed event. Without this the abandoned
        // connection's close would be read as connection loss and would schedule
        // a reconnect over the top of the connect we just started.
        private int _deliberateCloses;

        // What the current connection attempt is trying to do.
        private string? _pendingCode;
        private string? _pendingToken;
        private long _pendingEpoch;

        private string? _activeCode;
        private long _activeEpoch;

        // The last code we actually showed the player. Unlike _activeCode this
        // survives a disconnect, so a rotation is reported as a change (§5.3)
        // rather than as a fresh session — the difference between a player
        // re-pasting the code and a player staring at a dead browser tab.
        private string? _codeShownToPlayer;
        private bool _isCreator;
        private int _markerSequence;

        public RelaySession(
            SessionOptions options,
            IRelayTransport transport,
            IGameChannel gameChannel,
            IPeerView peers,
            IClock clock,
            ILog log,
            ReclaimStore reclaim,
            Func<double>? random = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _gameChannel = gameChannel ?? throw new ArgumentNullException(nameof(gameChannel));
            _peers = peers ?? throw new ArgumentNullException(nameof(peers));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _reclaim = reclaim ?? throw new ArgumentNullException(nameof(reclaim));

            _options.Normalise();
            _arbiter = new CodeArbiter(clock);
            _outbound = new OutboundQueue(_options.OutboundReliableCapacity);
            _throttle = new PositionThrottle(_options);
            _backoff = new Backoff(random: random);
            _relayFullBackoff = Backoff.ForRelayFull(random);

            _transport.Opened += OnTransportOpened;
            _transport.Received += OnTransportReceived;
            _transport.Closed += OnTransportClosed;
            _gameChannel.CodeAnnounced += OnCodeAnnounced;
            _gameChannel.CodeRequested += OnCodeRequested;
        }

        // ------------------------------------------------------------- surface

        public SessionState State => _state;

        /// <summary>The canonical code to show the player, or null while there is none.</summary>
        public string? Code => _activeCode;

        public bool IsCreator => _isCreator;

        public int PeerCount { get; private set; }

        public OutboundQueue Outbound => _outbound;

        public MarkerStore Markers => _markers;

        public event Action<SessionState>? StateChanged;
        public event Action<SessionNotice>? Notice;

        /// <summary>A ping from a map or a peer mod. Raised on the tick thread.</summary>
        public event Action<PingFrame>? PingReceived;

        /// <summary>A marker add/remove from a map. Raised on the tick thread.</summary>
        public event Action<MarkerFrame>? MarkerReceived;

        // ------------------------------------------------------------ lifecycle

        public void Start(SessionIdentity identity)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _markers.Clear();
            _markerSequence = 0;
            _codeShownToPlayer = null;
            _deliberateCloses = 0;
            _arbiter.ClearCurrent();
            _healthyResetDone = false;
            _backoff.Reset();
            _relayFullBackoff.Reset();
            EnterDiscovering();
        }

        /// <summary>
        /// Leave the session. §5.2: a mod retrying against the relay from the main
        /// menu is a bug, so this is terminal until <see cref="Start"/> is called
        /// again.
        /// </summary>
        public void Stop(string reason = "left the world")
        {
            if (_state == SessionState.Stopped || _state == SessionState.Idle)
            {
                _state = SessionState.Stopped;
                return;
            }

            // Not counted as a deliberate close: HandleClosed already returns
            // early once the state is Stopped, so counting it would leave a
            // credit behind that swallows the next run's first genuine drop.
            CloseTransport(1000, reason, expectClose: false);
            _outbound.Clear();
            _markers.Clear();
            _activeCode = null;
            _codeShownToPlayer = null;
            _isCreator = false;
            SetState(SessionState.Stopped);
            Raise(new SessionNotice(NoticeKind.Stopped, "session ended: " + reason));
        }

        /// <summary>Manual retry after 4008 (§5.2).</summary>
        public void Retry()
        {
            if (_state != SessionState.Blocked) return;
            _backoff.Reset();
            EnterDiscovering();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport.Opened -= OnTransportOpened;
            _transport.Received -= OnTransportReceived;
            _transport.Closed -= OnTransportClosed;
            _gameChannel.CodeAnnounced -= OnCodeAnnounced;
            _gameChannel.CodeRequested -= OnCodeRequested;
        }

        // ----------------------------------------------------------------- pump

        /// <summary>Drives the whole machine. Call once per frame from the main thread.</summary>
        public void Tick()
        {
            DrainSocketEvents();
            DrainAnnouncements();
            DrainCodeRequests();

            switch (_state)
            {
                case SessionState.Discovering:
                    TickDiscovering();
                    break;
                case SessionState.Active:
                    TickActive();
                    break;
                case SessionState.Creating:
                case SessionState.Joining:
                    TickConnecting();
                    break;
                case SessionState.Reconnecting:
                    TickReconnecting();
                    break;
            }

            PumpOutbound();
        }

        private void TickDiscovering()
        {
            var now = _clock.Elapsed;

            if (now - _lastDiscoveryAskAt >= _options.DiscoveryRetryInterval)
            {
                _lastDiscoveryAskAt = now;
                if (_gameChannel.IsReady) _gameChannel.RequestCode();
            }

            if (now - _stateEnteredAt < _options.DiscoveryWindow) return;

            // The window closed with nobody offering a code.
            var reclaimEntry = _identity?.World.Uid is string worldUid ? _reclaim.Get(worldUid) : null;
            var elected = CreatorElection.IsElectedCreator(_peers);

            if (!elected)
            {
                // Someone else is creating; keep listening and re-ask on the slow
                // timer. Staying in Discovering is the whole mechanism here.
                return;
            }

            var stagger = CreatorElection.CreationStagger(_peers, _options.CreationStaggerSpread);
            if (now - _stateEnteredAt < _options.DiscoveryWindow + stagger) return;

            if (reclaimEntry != null)
            {
                // §5.3: try the stored code+token before making a new room, so a
                // browser left open on the old code keeps working.
                BeginConnect(reclaimEntry.Code, reclaimEntry.Token, reclaimEntry.Epoch, SessionState.Joining);
            }
            else
            {
                BeginConnect(null, null, _arbiter.NextEpoch(), SessionState.Creating);
            }
        }

        private void TickActive()
        {
            var now = _clock.Elapsed;

            if (!_healthyResetDone && now - _connectionOpenedAt >= _options.HealthyConnectionThreshold)
            {
                // §5.2: a connection that has stayed up this long counts as
                // healthy, so the next drop starts the ladder from the bottom.
                _backoff.Reset();
                _relayFullBackoff.Reset();
                _healthyResetDone = true;
            }

            if (now - _lastHelloAt >= _options.HelloInterval) SendHello();

            if (_isCreator && _activeCode != null && now - _lastAnnounceAt >= _options.CodeAnnounceInterval)
            {
                AnnounceCode();
            }

            if (_stateReplayPending && now - _lastStateReplayAt >= _options.RequestStateCooldown)
            {
                ReplayState();
            }
        }

        /// <summary>
        /// A socket that opens but never delivers <c>welcome</c> — a wedged
        /// proxy, a half-open connection, a relay mid-restart — would otherwise
        /// leave the session in Creating or Joining for ever, with no retry and
        /// nothing said to the player.
        /// </summary>
        private void TickConnecting()
        {
            if (_clock.Elapsed - _stateEnteredAt < _options.ConnectTimeout) return;

            _log.Warn("no welcome within " + _options.ConnectTimeout.TotalSeconds + "s; retrying");
            CloseTransport(1000, "connect timed out");
            ScheduleRetry(_backoff.Next());
            Raise(new SessionNotice(NoticeKind.Reconnecting, "reconnecting to the relay"));
        }

        private void TickReconnecting()
        {
            if (_clock.Elapsed < _retryAt) return;
            BeginConnect(_pendingCode, _pendingToken, _pendingEpoch,
                _pendingCode == null ? SessionState.Creating : SessionState.Joining);
        }

        private void PumpOutbound()
        {
            if (_transport.State != TransportState.Open) return;
            while (_outbound.TryPeek(out var frame))
            {
                // The frame is only removed once the transport has taken it. A
                // refusal leaves it exactly where it was, in order, and draining
                // resumes next tick.
                if (!_transport.Send(frame)) break;
                _outbound.CommitPeek();
            }
        }

        // ------------------------------------------------------------- outbound

        /// <summary>
        /// Offer the current player position. Cheap to call every frame — the
        /// interval and dead-band are applied here.
        /// </summary>
        public void SubmitPosition(in PositionSample sample)
        {
            if (_state != SessionState.Active) return;
            if (!_options.SharePosition) return;

            var now = _clock.Elapsed;
            if (now - _lastPositionAt < _options.PositionInterval) return;
            if (!_throttle.ShouldSend(sample, now)) return;

            _lastPositionAt = now;
            _throttle.MarkSent(sample, now);
            _outbound.SetPosition(FrameCodec.WritePosition(sample));
        }

        public void SendPing(double x, double z)
        {
            if (_state != SessionState.Active) return;
            var ping = new PingFrame(x, z, _identity?.PlayerName, _clock.UnixTimeMilliseconds);
            EnqueueReliable(FrameCodec.WritePing(ping));
        }

        /// <summary>Create a marker owned by this client. Returns its id, or null if the cap is reached.</summary>
        public string? AddMarker(double x, double z, string? label, string? icon)
        {
            if (_state != SessionState.Active || _identity == null) return null;

            var id = MarkerStore.NewId(_identity.Uid, ++_markerSequence);
            var marker = new MarkerFrame(
                MarkerOps.Add, id, x, z, label, MarkerIcons.Normalise(icon), _clock.UnixTimeMilliseconds);

            if (!_markers.Add(marker))
            {
                _markerSequence--;
                _log.Warn("marker limit reached (" + MarkerStore.MaxOwnedMarkers + "); not adding");
                return null;
            }

            EnqueueReliable(FrameCodec.WriteMarker(marker));
            return id;
        }

        public bool RemoveMarker(string id)
        {
            if (_state != SessionState.Active) return false;
            if (!_markers.Remove(id)) return false;

            var marker = new MarkerFrame(MarkerOps.Remove, id, 0, 0, null, null, _clock.UnixTimeMilliseconds);
            EnqueueReliable(FrameCodec.WriteMarker(marker));
            return true;
        }

        private void EnqueueReliable(string frame)
        {
            if (!FrameCodec.FitsInFrame(frame))
            {
                // Over MAX_MESSAGE_BYTES the relay drops the connection (§1.5).
                // Refusing here turns a mysterious disconnect into one log line.
                _log.Warn("refusing oversized frame (" + FrameCodec.MeasureBytes(frame) + " bytes)");
                return;
            }

            if (!_outbound.EnqueueReliable(frame))
            {
                _log.Warn("outbound queue full; dropped a frame");
            }
        }

        private void SendHello()
        {
            if (_identity == null) return;
            _lastHelloAt = _clock.Elapsed;
            var hello = new HelloFrame(
                _identity.PlayerName,
                _identity.Uid,
                _identity.ModVersion,
                _identity.World,
                _options.SharePosition);
            EnqueueReliable(FrameCodec.WriteHello(hello));
        }

        /// <summary>
        /// Answer <c>request_state</c> (§3.5): hello, then every marker we own.
        /// The caller's position follows on the next <see cref="SubmitPosition"/>,
        /// which the reset below forces past the dead-band.
        /// </summary>
        private void ReplayState()
        {
            _stateReplayPending = false;
            _lastStateReplayAt = _clock.Elapsed;

            SendHello();
            foreach (var marker in _markers.Snapshot())
            {
                EnqueueReliable(FrameCodec.WriteMarker(marker));
            }

            _throttle.Reset();
            _lastPositionAt = TimeSpan.Zero;
        }

        private void AnnounceCode()
        {
            if (_activeCode == null || !_gameChannel.IsReady) return;
            _lastAnnounceAt = _clock.Elapsed;
            _gameChannel.AnnounceCode(_activeCode, _activeEpoch);
        }

        // -------------------------------------------------------------- inbound

        private void DrainSocketEvents()
        {
            while (_socketEvents.TryDequeue(out var evt))
            {
                switch (evt.Kind)
                {
                    case SocketEventKind.Opened:
                        HandleOpened();
                        break;
                    case SocketEventKind.Received:
                        HandleFrame(evt.Text);
                        break;
                    case SocketEventKind.Closed:
                        HandleClosed(evt.Code, evt.Text);
                        break;
                }
            }
        }

        private void HandleOpened()
        {
            _connectionOpenedAt = _clock.Elapsed;
            _healthyResetDone = false;
            // Nothing is sent until `welcome` arrives: the code it carries is
            // authoritative and decides whether we are the creator (§1.2).
        }

        private void HandleFrame(string text)
        {
            var frame = FrameCodec.ParseFrame(text);
            if (frame == null)
            {
                _log.Debug("ignoring unparseable frame");
                return;
            }

            switch (FrameCodec.TypeOf(frame))
            {
                case FrameTypes.Welcome:
                    HandleWelcome(frame);
                    break;
                case FrameTypes.RequestState:
                    HandleRequestState();
                    break;
                case FrameTypes.Ping:
                    if (FrameCodec.ReadPing(frame) is PingFrame ping) PingReceived?.Invoke(ping);
                    break;
                case FrameTypes.Marker:
                    if (FrameCodec.ReadMarker(frame) is MarkerFrame marker) MarkerReceived?.Invoke(marker);
                    break;
                case FrameTypes.PlayerJoined:
                case FrameTypes.PlayerLeft:
                    // Maps-only per §1.3; harmless if a relay ever sends them here.
                    break;
                default:
                    // §3: ignore unknown types rather than erroring. This is what
                    // lets the map ship a new frame type on its own schedule.
                    break;
            }
        }

        private void HandleWelcome(JsonValue frame)
        {
            var welcome = FrameCodec.ReadWelcome(frame);
            if (welcome == null)
            {
                _log.Warn("malformed welcome; dropping the connection");
                CloseTransport(1002, "bad welcome", expectClose: false);
                return;
            }

            _activeCode = welcome.Code;
            _activeEpoch = _pendingEpoch;
            _isCreator = welcome.IsCreator;
            PeerCount = welcome.Players.Count;

            _arbiter.SetCurrent(welcome.Code, _activeEpoch);

            // From here on, a reconnect resumes *this* room. The relay keeps a
            // room alive for ROOM_TTL after its last client leaves (§1.5), so
            // reconnecting with the code is all a brief drop-out needs. Creators
            // also carry the token, which covers a drop longer than ROOM_TTL.
            _pendingCode = welcome.Code;
            _pendingToken = welcome.Token;

            if (welcome.IsCreator && _identity?.World.Uid is string worldUid)
            {
                // Never log the token (§5.3, §8).
                _reclaim.Put(worldUid, new ReclaimEntry(
                    welcome.Code, welcome.Token!, _activeEpoch, _clock.UnixTimeMilliseconds));
            }

            SetState(SessionState.Active);
            _throttle.Reset();
            _lastPositionAt = TimeSpan.Zero;
            SendHello();

            if (_isCreator) AnnounceCode();

            if (_codeShownToPlayer == null)
            {
                _codeShownToPlayer = welcome.Code;
                Raise(new SessionNotice(NoticeKind.SessionStarted, "map code " + welcome.Code, welcome.Code));
            }
            else if (!string.Equals(_codeShownToPlayer, welcome.Code, StringComparison.Ordinal))
            {
                _codeShownToPlayer = welcome.Code;
                // §5.3: any browser on the old code is now pointed at a dead room.
                // Say so explicitly — this is the one flow where zero-typing fails.
                Raise(new SessionNotice(
                    NoticeKind.CodeChanged,
                    "the map code changed to " + welcome.Code + " — re-enter it in the web map",
                    welcome.Code));
            }
        }

        private void HandleRequestState()
        {
            var now = _clock.Elapsed;
            if (now - _lastStateReplayAt >= _options.RequestStateCooldown)
            {
                ReplayState();
                return;
            }

            // §3.5 caps the reply rate but does not say what happens to a request
            // that lands inside the cooldown. Dropping it would leave a map that
            // reloaded a second after another map waiting up to a full hello
            // interval for any world data, so coalesce instead: remember that a
            // reply is owed and send one when the cooldown expires. Eight
            // browsers reloading at once still produce exactly one replay.
            _stateReplayPending = true;
        }

        private void HandleClosed(int closeCode, string reason)
        {
            _outbound.Clear();

            if (_state == SessionState.Stopped) return;

            if (_deliberateCloses > 0)
            {
                _deliberateCloses--;
                return;
            }

            _log.Info("relay connection closed: " + CloseCodes.Describe(closeCode));

            switch (closeCode)
            {
                case CloseCodes.TokenMismatch:
                    ForgetReclaim("reclaim token rejected");
                    _activeCode = null;
                    _isCreator = false;
                    EnterDiscovering();
                    return;

                case CloseCodes.UnknownCode:
                    HandleUnknownCode();
                    return;

                case CloseCodes.RoomFull:
                    // Not transient: the 17th player will never fit (§5.2).
                    _activeCode = null;
                    _isCreator = false;
                    SetState(SessionState.Blocked);
                    Raise(new SessionNotice(
                        NoticeKind.RoomFull,
                        "the session is full (16 players). Retry from the relay panel."));
                    return;

                case CloseCodes.RelayFull:
                    ScheduleRetry(_relayFullBackoff.Next());
                    Raise(new SessionNotice(NoticeKind.RelayBusy, "the relay is busy; retrying shortly"));
                    return;

                default:
                    ScheduleRetry(_backoff.Next());
                    Raise(new SessionNotice(NoticeKind.Reconnecting, "reconnecting to the relay"));
                    return;
            }
        }

        private void HandleUnknownCode()
        {
            var dead = _pendingCode ?? _activeCode;
            if (dead != null) _arbiter.MarkDead(dead, _pendingEpoch);

            _activeCode = null;

            if (_isCreator || _pendingToken != null)
            {
                // We owned this room, or tried to reclaim it, and it is gone.
                // Make a new one, a generation above anything still circulating.
                ForgetReclaim("code expired");
                _isCreator = false;
                BeginConnect(null, null, _arbiter.NextEpoch(), SessionState.Creating);
                return;
            }

            // We were only a guest: the creator left and the room was swept
            // (§5.3). Fall back into discovery so a new creator is elected.
            _isCreator = false;
            EnterDiscovering();
            Raise(new SessionNotice(
                NoticeKind.CodeChanged,
                "the session ended; finding or creating a new one"));
        }

        private void ForgetReclaim(string why)
        {
            if (_identity?.World.Uid is string worldUid)
            {
                _log.Info("discarding stored session for this world: " + why);
                _reclaim.Forget(worldUid);
            }
        }

        // ------------------------------------------------------- code arbitration

        private void DrainAnnouncements()
        {
            while (_announcements.TryDequeue(out var announcement))
            {
                if (_state == SessionState.Stopped || _state == SessionState.Blocked) continue;

                switch (_arbiter.Consider(announcement))
                {
                    case CodeDecision.Adopt:
                        AdoptCode(announcement);
                        break;
                    case CodeDecision.Defend:
                        // Ours wins; re-announce so the sender migrates to us.
                        AnnounceCode();
                        break;
                }
            }
        }

        private void AdoptCode(in CodeAnnouncement announcement)
        {
            if (string.Equals(_activeCode, announcement.Code, StringComparison.OrdinalIgnoreCase)) return;

            // Also ignore a repeat of the code we are already connecting to. The
            // creator announces on a heartbeat, so a second announcement during
            // the join is the normal case, not an exception — acting on it would
            // tear down the in-flight connect and start again, indefinitely.
            if (string.Equals(_pendingCode, announcement.Code, StringComparison.OrdinalIgnoreCase) &&
                (_state == SessionState.Joining || _state == SessionState.Reconnecting))
            {
                return;
            }

            if (_isCreator)
            {
                // We created a room and just lost the tiebreak. PLAN.md §5.1 says
                // to disconnect and join the winner but not what to do with the
                // token we are still holding for the room we are abandoning.
                // Keeping it means the next load of this world reclaims the dead
                // room and splits the group all over again, so drop it here.
                ForgetReclaim("lost the code tiebreak to " + announcement.Code);
                _isCreator = false;
            }

            _log.Info("adopting session code " + announcement.Code);

            // Claim it now rather than at welcome, so further announcements of
            // the same code are Ignored by the arbiter instead of re-adopted. If
            // the join fails with 4004 the code is marked dead, which clears it.
            _arbiter.SetCurrent(announcement.Code, announcement.Epoch);

            CloseTransport(1000, "migrating to " + announcement.Code);
            _outbound.Clear();
            BeginConnect(announcement.Code, null, announcement.Epoch, SessionState.Joining);
        }

        private void DrainCodeRequests()
        {
            var requests = System.Threading.Interlocked.Exchange(ref _codeRequests, 0);
            if (requests == 0) return;
            if (_state != SessionState.Active || !_isCreator || _activeCode == null) return;

            // Coalesced: many askers, one announcement.
            AnnounceCode();
        }

        // ------------------------------------------------------------ transitions

        private void EnterDiscovering()
        {
            _activeCode = null;
            _isCreator = false;
            _pendingCode = null;
            _pendingToken = null;
            _lastDiscoveryAskAt = TimeSpan.Zero;

            // Discovering means we hold no session. Leaving the arbiter pointed
            // at the code we just lost would have it Defend that code against a
            // peer announcing the live one, and this client would never rejoin.
            _arbiter.ClearCurrent();

            SetState(SessionState.Discovering);
            if (_gameChannel.IsReady) _gameChannel.RequestCode();
            _lastDiscoveryAskAt = _clock.Elapsed;
        }

        private void BeginConnect(string? code, string? token, long epoch, SessionState state)
        {
            _pendingCode = code;
            _pendingToken = token;
            _pendingEpoch = epoch;
            SetState(state);
            _transport.Connect(_options.RelayUrl, code, token);
        }

        private void ScheduleRetry(TimeSpan delay)
        {
            _retryAt = _clock.Elapsed + delay;
            // Keep _pendingCode/_pendingToken: the room outlives its last client
            // by ROOM_TTL (§1.5), so reconnecting with the same code resumes the
            // same session and needs no special handling at all.
            SetState(SessionState.Reconnecting);
        }

        /// <param name="expectClose">
        /// True when this close is part of a transition we are already driving —
        /// the resulting Closed event must not be treated as connection loss.
        /// </param>
        private void CloseTransport(int code, string reason, bool expectClose = true)
        {
            if (_transport.State == TransportState.Closed) return;
            if (expectClose) _deliberateCloses++;
            _transport.Close(code, reason);
        }

        private void SetState(SessionState state)
        {
            if (_state == state) return;
            _state = state;
            _stateEnteredAt = _clock.Elapsed;
            StateChanged?.Invoke(state);
        }

        private void Raise(SessionNotice notice) => Notice?.Invoke(notice);

        // ------------------------------------------------- callbacks (any thread)

        private void OnTransportOpened()
            => _socketEvents.Enqueue(new SocketEvent(SocketEventKind.Opened, string.Empty, 0));

        private void OnTransportReceived(string text)
            => _socketEvents.Enqueue(new SocketEvent(SocketEventKind.Received, text, 0));

        private void OnTransportClosed(int code, string reason)
            => _socketEvents.Enqueue(new SocketEvent(SocketEventKind.Closed, reason ?? string.Empty, code));

        private void OnCodeAnnounced(CodeAnnouncement announcement)
            => _announcements.Enqueue(announcement);

        private void OnCodeRequested()
            => System.Threading.Interlocked.Increment(ref _codeRequests);
    }
}
