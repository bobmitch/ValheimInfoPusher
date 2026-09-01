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

        public RelaySession? Session => _session;

        public string? Code => _session?.Code;

        public SessionState State => _session?.State ?? SessionState.Idle;

        private void Awake()
        {
            _plugin = ValheimRelayPlugin.Instance
                ?? throw new InvalidOperationException("RelayBehaviour created without a plugin");

            _bridge = new GameBridge(_plugin.Log);
            _channel = new GameCodeChannel(_plugin.Log, () => _plugin.Settings.AnnounceInChat.Value);
            _transport = new ClientWebSocketTransport(_plugin.Log);
            _reclaim = new ReclaimStore(new FileReclaimStorage(_plugin.SessionStorePath, _plugin.Log), _plugin.Log);
            _panel = new RelayPanel(this, _plugin.Settings);
        }

        /// <summary>The code channel's RPCs register on <c>Game.Start</c> (§4.3).</summary>
        public void OnGameStart() => _channel.Register();

        /// <summary>A peer's chat line, forwarded from the chat patch.</summary>
        public bool TryConsumeChat(long sender, string? text) => _channel.TryConsumeChat(sender, text);

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
            if (_session == null) return;

            if (Input.GetKeyDown(_plugin.Settings.ToggleKey.Value))
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
                        _bridge.LocalMessage("map code " + notice.Code + "  (F9 for the panel)");
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
        }

        // ------------------------------------------------------------ panel API

        public void CopyShareTextToClipboard()
        {
            var code = Code;
            if (code == null) return;
            GUIUtility.systemCopyBuffer = _plugin.Settings.BuildShareText(code);
        }

        public void RetryAfterRoomFull() => _session?.Retry();

        private void OnDestroy()
        {
            _session?.Dispose();
            _transport.Dispose();
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
