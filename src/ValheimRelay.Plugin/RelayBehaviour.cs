using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimRelay.Core.Identity;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Plugin
{
    /// <summary>
    /// The main-thread pump (§4.2). Every game object touched by this mod is
    /// touched here or in <see cref="GameBridge"/>, and Core's events are raised
    /// from inside <see cref="RelaySession.Tick"/>, which this calls from
    /// <c>Update</c> — so handlers are already on the main thread and are free
    /// to call into Unity.
    /// </summary>
    public sealed class RelayBehaviour : MonoBehaviour
    {
        private readonly Dictionary<string, object> _pins = new Dictionary<string, object>(StringComparer.Ordinal);

        private ValheimRelayPlugin _plugin = null!;
        private GameBridge _bridge = null!;
        private GameCodeChannel _channel = null!;
        private ClientWebSocketTransport _transport = null!;
        private ReclaimStore _reclaim = null!;
        private RelaySession? _session;
        private RelayPanel _panel = null!;

        private bool _sessionRunning;
        private float _discoveryDeadline;
        private bool _fallbackConsidered;
        private bool _pingSenderWarned;
        private bool _pingSilenceWarned;
        private int _pingsForwarded;
        private int _pingsRejected;

        /// <summary>
        /// How many pings may be read as somebody else's before that is treated
        /// as suspicious. Three, because one or two really can be another
        /// player's — the point is a run of them with none of our own.
        /// </summary>
        private const int PingRejectionsBeforeWarning = 3;

        public RelaySession? Session => _session;

        public string? Code => _session?.Code;

        public SessionState State => _session?.State ?? SessionState.Idle;

        private void Awake()
        {
            _plugin = ValheimRelayPlugin.Instance
                ?? throw new InvalidOperationException("RelayBehaviour created without a plugin");

            _bridge = new GameBridge(_plugin.Log, () => _plugin.Settings.PingStyle.Value);
            _channel = new GameCodeChannel(_plugin.Log, () => _plugin.Settings.AnnounceInChat.Value);
            _transport = new ClientWebSocketTransport(_plugin.Log);
            _reclaim = new ReclaimStore(new FileReclaimStorage(_plugin.SessionStorePath, _plugin.Log), _plugin.Log);
            _panel = new RelayPanel(this, _plugin.Settings);
        }

        /// <summary>The code channel's RPCs register on <c>Game.Start</c> (§4.3).</summary>
        public void OnGameStart() => _channel.Register();

        /// <summary>A peer's chat line, forwarded from the chat patch.</summary>
        public bool TryConsumeChat(long sender, string? text) => _channel.TryConsumeChat(sender, text);

        /// <summary>
        /// A ping the GAME has just delivered, forwarded from the ping patch —
        /// the outbound half of §3.3.
        /// <para>
        /// Two duplicate problems meet here and they need different answers.
        /// The GAME already showed this ping to every player in the world, so
        /// the copy §3.3 fans out to peer mods would be a second marker and a
        /// second sound: <see cref="GameBridge.NoteGamePing"/> is what swallows
        /// that, and it is recorded for EVERY ping observed, whether or not this
        /// client is the one forwarding it. The WIRE has the other problem: with
        /// every modded client forwarding, one ping would reach the web map once
        /// per mod, so only the local player's own ping goes out.
        /// </para>
        /// </summary>
        public void OnGamePing(Vector3 position, long senderId)
        {
            // Before every gate below it. A client with sharing off still sees
            // the relayed copies of everyone else's pings, so it still needs to
            // know which of them it has already been shown.
            _bridge.NoteGamePing(position.x, position.z);

            if (!_plugin.Settings.ShareMyPings.Value) return;
            if (!_sessionRunning) return;

            if (!IsLocalPing(senderId))
            {
                WarnIfNothingIsEverForwarded(senderId);
                return;
            }

            _pingsForwarded++;

            // The session refuses this unless it is Active, so a ping made
            // while reconnecting is dropped rather than queued: it is a "look
            // here, now", and arriving a reconnect late points at nothing.
            _session?.SendPing(position.x, position.z);
        }

        /// <summary>
        /// The one failure this feature could have that nobody would notice.
        /// <para>
        /// The local filter assumes the sender id on a ping message is the same
        /// id <c>ZNet.GetUID</c> returns, and that the game routes the local
        /// player's own ping back through the chat handler rather than drawing
        /// it directly. If either is untrue on some build, every ping is read as
        /// somebody else's, nothing is ever forwarded, and the mod looks like it
        /// is working — the pings still appear in game, because this patch never
        /// touched them. A player would have no way to tell that from an empty
        /// room. So say it once, with both ids in the line, rather than leaving
        /// it to be guessed at.
        /// </para>
        /// </summary>
        private void WarnIfNothingIsEverForwarded(long senderId)
        {
            if (_pingSilenceWarned || _pingsForwarded > 0) return;
            if (++_pingsRejected < PingRejectionsBeforeWarning) return;

            _pingSilenceWarned = true;
            _plugin.Log.Warn(
                "seen " + _pingsRejected + " pings in game and forwarded none of them: every one read as "
                + "another player's (self=" + _bridge.SelfPeerId + ", last sender=" + senderId + "). If you "
                + "are alone in this world that is a bug — pings made here are not reaching the web map, "
                + "while pings FROM the map still work.");
        }

        /// <summary>
        /// Whether this ping is the local player's, which is the whole of the
        /// wire-side duplicate filter.
        /// <para>
        /// WHEN IT CANNOT TELL, IT SAYS YES. An unresolvable id means every
        /// modded client forwards, so the map draws one ring per mod — which,
        /// being the same ring at the same place at the same moment, is
        /// indistinguishable from one, and the in-game duplicate is handled by
        /// the echo window regardless. Answering no would instead make the
        /// feature quietly do nothing, which is far harder to notice and far
        /// worse. It is logged once so it is at least diagnosable.
        /// </para>
        /// </summary>
        private bool IsLocalPing(long senderId)
        {
            var self = _bridge.SelfPeerId;
            if (self != 0 && senderId != 0) return senderId == self;

            if (!_pingSenderWarned)
            {
                _pingSenderWarned = true;
                _plugin.Log.Warn(
                    "could not tell whose ping this is (self=" + self + ", sender=" + senderId + "), so pings "
                    + "made in game are being forwarded without that check. If several players here run the "
                    + "mod, the web map may draw one ring per player for a single ping.");
            }

            return true;
        }

        /// <summary>Called once the world is loaded and there is a local player.</summary>
        public void StartSession()
        {
            if (_sessionRunning) return;
            if (!_plugin.Settings.Enabled.Value) return;
            if (!GameBridge.IsWorldLoaded || !GameBridge.HasLocalPlayer) return;

            _channel.Register();
            _channel.Reset();

            var options = _plugin.Settings.ToSessionOptions();
            _session?.Dispose();
            _session = new RelaySession(options, _transport, _channel, _bridge, new UnityClock(), _plugin.Log, _reclaim);
            _session.Notice += OnNotice;
            _session.PingReceived += OnPingReceived;
            _session.MarkerReceived += OnMarkerReceived;

            // §8: never a raw platform id. The salt lives beside the config and
            // makes the digest unlinkable to the account. ReclaimStore.Salt
            // regenerates anything unusable, so this decode cannot fail — but it
            // is checked rather than assumed, because the failure mode is an
            // exception on every world load with no way for a player to recover.
            if (!StableUid.TryDecodeSalt(_reclaim.Salt, out var salt))
            {
                _plugin.Log.Error("could not establish an identity salt; not starting a session");
                _sessionRunning = false;
                return;
            }

            var uid = StableUid.Derive(_bridge.ProfileId, salt);

            _session.Start(new SessionIdentity(
                _bridge.PlayerName, uid, ValheimRelayPlugin.PluginVersion, _bridge.ReadWorld()));

            _sessionRunning = true;
            _fallbackConsidered = false;
            _discoveryDeadline = Time.realtimeSinceStartup + (float)options.DiscoveryWindow.TotalSeconds;
        }

        /// <summary>Called on logout or world unload (§4.3, §5.2).</summary>
        public void StopSession(string reason)
        {
            if (!_sessionRunning) return;
            _sessionRunning = false;

            ClearPins();
            _session?.Stop(reason);
        }

        private void Update()
        {
            // Ahead of the session check: a ping pin that outlived its session
            // would otherwise sit on the map until the world unloaded.
            _bridge.ExpirePings();

            if (_session == null) return;

            if (Input.GetKeyDown(_plugin.Settings.ToggleKey.Value) && ToggleModifierHeld())
            {
                _panel.Toggle();
            }

            if (!_sessionRunning)
            {
                _session.Tick();
                return;
            }

            // §6: if no peer has answered over RPC by the time discovery closes,
            // switch to the chat channel rather than silently never converging.
            if (!_fallbackConsidered && Time.realtimeSinceStartup >= _discoveryDeadline)
            {
                _fallbackConsidered = true;
                if (!_channel.RpcWorks) _channel.EnableChatFallback();
            }

            SubmitPosition();
            _session.Tick();
        }

        /// <summary>
        /// True unless the config asks for Shift and neither Shift key is down.
        /// The default bind is Shift+F8: F9 is a stock game bind, F8 is not, and
        /// the modifier keeps the panel clear of whatever else is bound to F8.
        /// </summary>
        private bool ToggleModifierHeld()
        {
            if (!_plugin.Settings.ToggleRequiresShift.Value) return true;
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        }

        private void SubmitPosition()
        {
            if (!_plugin.Settings.ShareMyPosition.Value) return;

            // No local player means loading, dead-and-not-respawned, or a menu.
            // Sending nothing is correct: the world origin is a real place, and
            // a default sample would park everyone on the spawn stone.
            if (!_bridge.TryReadPosition(
                    _plugin.Settings.ShareHealth.Value,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    out var sample))
            {
                return;
            }

            _session!.SubmitPosition(sample);
        }

        private void OnGUI() => _panel.Draw();

        // -------------------------------------------------------------- events

        private void OnNotice(SessionNotice notice)
        {
            switch (notice.Kind)
            {
                case NoticeKind.SessionStarted:
                    if (_plugin.Settings.AnnounceInChat.Value && notice.Code != null)
                    {
                        // The link is the useful thing to copy, but the code is
                        // the thing you read aloud over voice chat — which is
                        // what the Crockford alphabet exists for (§1.1) — so the
                        // line carries both.
                        _bridge.LocalMessage(_plugin.Settings.HasMapLink
                            ? notice.Code + "  ·  " + _plugin.Settings.BuildShareText(notice.Code, WorldSeed)
                                + "  (Shift+F8 copies it and shows a QR)"
                            : "map code " + notice.Code + "  (Shift+F8 for the panel)");
                    }
                    break;

                case NoticeKind.CodeChanged:
                    // §5.3: the one flow where zero typing cannot be kept. Any
                    // browser on the old code is now pointed at a dead room, so
                    // say so rather than failing quietly.
                    _bridge.LocalMessage(notice.Message);
                    break;

                case NoticeKind.RoomFull:
                    _bridge.LocalMessage(notice.Message);
                    break;
            }

            _plugin.Log.Info(notice.Message);
        }

        private void OnPingReceived(PingFrame ping)
        {
            _bridge.ShowPing(ping.X, ping.Z, ping.Name);
        }

        private void OnMarkerReceived(MarkerFrame marker)
        {
            if (!_plugin.Settings.AcceptMapMarkers.Value) return;

            if (marker.IsRemove)
            {
                if (_pins.TryGetValue(marker.Id, out var existing))
                {
                    _bridge.RemovePin(existing);
                    _pins.Remove(marker.Id);
                }
                return;
            }

            // An add for an id we already hold is a move, not a duplicate.
            if (_pins.TryGetValue(marker.Id, out var previous))
            {
                _bridge.RemovePin(previous);
                _pins.Remove(marker.Id);
            }

            var pin = _bridge.AddPin(marker.X, marker.Z, marker.Label, marker.Icon);
            if (pin != null) _pins[marker.Id] = pin;
        }

        private void ClearPins()
        {
            foreach (var pin in _pins.Values) _bridge.RemovePin(pin);
            _pins.Clear();
            _bridge.ClearPings();
        }

        // ------------------------------------------------------------ panel API

        /// <summary>
        /// The one string the mod hands a player: the map link when a map is
        /// configured, the bare code when one is not. The panel copies this and
        /// draws it as a QR, so the clipboard and the square can never end up
        /// pointing at different things.
        /// </summary>
        public string? ShareText
        {
            get
            {
                var code = Code;
                return code == null ? null : _plugin.Settings.BuildShareText(code, WorldSeed);
            }
        }

        public void RetryAfterRoomFull() => _session?.Retry();

        /// <summary>
        /// The current world's seed for the share link, read at share time
        /// rather than cached: a player can leave one world and load another
        /// without this component restarting, and a stale seed would hand them a
        /// link that renders the wrong terrain.
        /// </summary>
        private string? WorldSeed => _bridge.ReadWorld().Seed;

        private void OnDestroy()
        {
            _session?.Dispose();
            _transport.Dispose();
            _panel.Release();
        }
    }

    /// <summary>Unity's clock, in the shape Core wants.</summary>
    public sealed class UnityClock : IClock
    {
        // realtimeSinceStartup rather than Time.time: it does not stop when the
        // game is paused, and a paused game still has an open socket with a 60 s
        // read deadline behind it.
        public TimeSpan Elapsed => TimeSpan.FromSeconds(Time.realtimeSinceStartup);

        public long UnixTimeMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
