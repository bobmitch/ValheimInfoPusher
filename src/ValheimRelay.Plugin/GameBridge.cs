using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using ValheimRelay.Core.Protocol;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Plugin
{
    /// <summary>
    /// Everything that reads or writes game state. Core never touches these
    /// types, and the Harmony patches contain no logic beyond forwarding here
    /// (§4.1) — so when a game update renames something, this is the only file
    /// that changes.
    /// <para>
    /// Every lookup is defensive. §4.3 is written from the generally-known API
    /// surface rather than from a decompile of any particular build, and §11.4
    /// leaves "what happens when a patch stops applying" open; failing soft with
    /// a log line is the answer taken here, because a mod that refuses to load
    /// after a game update strands the player with no map and no explanation.
    /// </para>
    /// </summary>
    public sealed class GameBridge : IPeerView
    {
        private readonly ILog _log;

        public GameBridge(ILog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        // ------------------------------------------------------------ readiness

        /// <summary>True when there is a local player to report on.</summary>
        public static bool HasLocalPlayer => Player.m_localPlayer != null;

        public static bool IsWorldLoaded => ZNet.instance != null;

        // ------------------------------------------------------------- identity

        public string PlayerName
        {
            get
            {
                var player = Player.m_localPlayer;
                if (player == null) return "Viking";
                var name = player.GetPlayerName();
                return string.IsNullOrEmpty(name) ? "Viking" : name;
            }
        }

        /// <summary>
        /// The raw profile id, to be hashed with the install salt before it ever
        /// leaves the machine (§8 and Core's <c>StableUid</c>). It is returned
        /// raw only so the caller can hash it, and must never be sent or logged.
        /// </summary>
        public string ProfileId
        {
            get
            {
                try
                {
                    var profile = Game.instance?.GetPlayerProfile();
                    if (profile == null) return "unknown-profile";
                    return profile.GetPlayerID().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception ex)
                {
                    _log.Warn("could not read the player profile id: " + ex.Message);
                    return "unknown-profile";
                }
            }
        }

        public WorldInfo ReadWorld()
        {
            try
            {
                var net = ZNet.instance;
                if (net == null) return default;

                var name = net.GetWorldName();
                var uid = net.GetWorldUID().ToString(System.Globalization.CultureInfo.InvariantCulture);

                string? seedName = null;
                var seed = 0L;
                var generator = WorldGenerator.instance;
                if (generator?.m_world != null)
                {
                    seedName = generator.m_world.m_seedName;
                    seed = generator.m_world.m_seed;
                }

                return new WorldInfo(name, seedName, seed, uid);
            }
            catch (Exception ex)
            {
                _log.Warn("could not read world information: " + ex.Message);
                return default;
            }
        }

        // ------------------------------------------------------------ telemetry

        /// <summary>
        /// Reads the local player's position.
        /// <para>
        /// Returns false when there is no local player — loading, dead and not
        /// yet respawned, or sitting in a menu. This matters more than it looks:
        /// the world origin is a real place in Valheim, so a caller that
        /// substituted a default sample would put every such player on the spawn
        /// stone, and the map would show a crowd standing on it.
        /// </para>
        /// </summary>
        public bool TryReadPosition(bool includeHealth, long timestampMs, out PositionSample sample)
        {
            sample = default;

            var player = Player.m_localPlayer;
            if (player == null) return false;

            try
            {
                var transform = player.transform;
                var position = transform.position;
                var heading = transform.rotation.eulerAngles.y;

                var health = Mathf.RoundToInt(player.GetHealth());
                var maxHealth = Mathf.RoundToInt(player.GetMaxHealth());
                var dead = player.IsDead() || health <= 0;

                sample = new PositionSample(
                    position.x,
                    position.z,
                    position.y,
                    NormaliseDegrees(heading),
                    ReadBiome(position.x, position.z),
                    health,
                    maxHealth,
                    includeHealth,
                    dead,
                    timestampMs);

                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("could not read the local player: " + ex.Message);
                return false;
            }
        }

        private string? ReadBiome(float x, float z)
        {
            try
            {
                var generator = WorldGenerator.instance;
                if (generator == null) return null;
                return generator.GetBiome(x, z).ToString();
            }
            catch (Exception)
            {
                // A convenience for the map, not a requirement; never fail over it.
                return null;
            }
        }

        private static double NormaliseDegrees(double degrees)
        {
            var value = degrees % 360.0;
            return value < 0 ? value + 360.0 : value;
        }

        // ----------------------------------------------------------- IPeerView

        public bool IsHost
        {
            get
            {
                try
                {
                    return ZNet.instance != null && ZNet.instance.IsServer();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public long SelfPeerId
        {
            get
            {
                try
                {
                    // GetUID is static in this build, but it only means
                    // anything once ZNet exists, so the instance check
                    // stays as a guard rather than as the receiver.
                    return ZNet.instance == null ? 0 : ZNet.GetUID();
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public IReadOnlyList<long> PeerIds
        {
            get
            {
                var ids = new List<long>();
                try
                {
                    var peers = ZNet.instance?.GetPeers();
                    if (peers == null) return ids;
                    foreach (var peer in peers)
                    {
                        if (peer != null) ids.Add(peer.m_uid);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("could not read the peer list: " + ex.Message);
                }
                return ids;
            }
        }

        // ---------------------------------------------------------------- pins

        /// <summary>
        /// Maps the fixed §3.4 icon vocabulary onto Valheim's own pin types.
        /// Anything unrecognised has already been folded to <c>dot</c> by Core.
        /// </summary>
        public static Minimap.PinType ToPinType(string? icon)
        {
            switch (MarkerIcons.Normalise(icon))
            {
                case MarkerIcons.Ore: return Minimap.PinType.Icon3;
                case MarkerIcons.Boss: return Minimap.PinType.Boss;
                case MarkerIcons.Home: return Minimap.PinType.Icon1;
                case MarkerIcons.Death: return Minimap.PinType.Death;
                case MarkerIcons.Danger: return Minimap.PinType.Icon2;
                default: return Minimap.PinType.Icon0;
            }
        }

        public object? AddPin(double x, double z, string? label, string? icon)
        {
            // y is irrelevant for a map pin; the game clamps it to terrain.
            return AddPinAt(new Vector3((float)x, 0f, (float)z), ToPinType(icon), label ?? string.Empty);
        }

        private object? AddPinAt(Vector3 position, Minimap.PinType type, string label)
        {
            try
            {
                var minimap = Minimap.instance;
                if (minimap == null) return null;
                return minimap.AddPin(position, type, label, save: false, isChecked: false);
            }
            catch (Exception ex)
            {
                _log.Warn("could not add a map pin: " + ex.Message);
                return null;
            }
        }

        public void RemovePin(object? pin)
        {
            if (pin is not Minimap.PinData data) return;
            try
            {
                Minimap.instance?.RemovePin(data);
            }
            catch (Exception ex)
            {
                _log.Warn("could not remove a map pin: " + ex.Message);
            }
        }

        // --------------------------------------------------------------- pings

        /// <summary>How long a fallback ping pin stays on the map.</summary>
        private const float PingLifetimeSeconds = 6f;

        private readonly List<PingPin> _pingPins = new List<PingPin>();

        private MethodInfo? _addPing;
        private bool _addPingResolved;

        /// <summary>
        /// Valheim's own transient ping, so a map ping feels identical in-game.
        /// <para>
        /// Deliberately not <c>Minimap.ShowPointOnMap</c>. Despite the name that
        /// one is "reveal this place to the player": it forces the large map
        /// open and recentres it, and draws no ping at all — so an inbound ping
        /// used to yank the full-screen map over the game, possibly mid-fight,
        /// and still show nothing. The real ping is <c>Minimap.AddPing</c>,
        /// which is what the game's own chat path calls for a
        /// <c>Talker.Type.Ping</c> message.
        /// </para>
        /// <para>
        /// §4.3 says not to assume a signature that has not been checked
        /// against the build in front of us, so <c>AddPing</c> is resolved by
        /// shape rather than bound at compile time, and a build that does not
        /// have it falls back to a short-lived pin plus a chat line instead of
        /// silently doing nothing.
        /// </para>
        /// </summary>
        public void ShowPing(double x, double z, string? who)
        {
            try
            {
                if (Minimap.instance == null) return;

                var position = new Vector3((float)x, 0f, (float)z);
                var name = string.IsNullOrEmpty(who) ? "Ping" : who!;

                if (TryNativePing(position, name)) return;
                ShowFallbackPing(position, name);
            }
            catch (Exception ex)
            {
                _log.Warn("could not show a ping: " + ex.Message);
            }
        }

        /// <summary>
        /// Expires fallback ping pins. Cheap and safe to call every frame; does
        /// nothing at all unless a fallback ping is currently on the map.
        /// </summary>
        public void ExpirePings()
        {
            if (_pingPins.Count == 0) return;

            var now = Time.realtimeSinceStartup;
            for (var i = _pingPins.Count - 1; i >= 0; i--)
            {
                if (_pingPins[i].Expiry > now) continue;
                RemovePin(_pingPins[i].Pin);
                _pingPins.RemoveAt(i);
            }
        }

        /// <summary>Drops every fallback ping pin (session stop, world unload).</summary>
        public void ClearPings()
        {
            foreach (var ping in _pingPins) RemovePin(ping.Pin);
            _pingPins.Clear();
        }

        private bool TryNativePing(Vector3 position, string name)
        {
            var method = ResolveAddPing();
            if (method == null) return false;

            try
            {
                method.Invoke(Minimap.instance, BuildPingArgs(method, position, name));
                return true;
            }
            catch (Exception ex)
            {
                // Once it has thrown it will keep throwing, so stop calling it
                // and let every later ping take the fallback path.
                _addPing = null;
                _log.Warn("Minimap.AddPing failed, so pings will show as pins: " + Unwrap(ex).Message);
                return false;
            }
        }

        private void ShowFallbackPing(Vector3 position, string name)
        {
            var pin = AddPinAt(position, PingPinType, name);
            if (pin != null)
            {
                _pingPins.Add(new PingPin(pin, Time.realtimeSinceStartup + PingLifetimeSeconds));
            }

            // Without the game's own ping there is no sound and no on-screen
            // marker, so say where it was — a pin on a map you are not looking
            // at is easy to miss entirely.
            LocalMessage(name + " pinged "
                + Mathf.RoundToInt(position.x).ToString(CultureInfo.InvariantCulture) + ", "
                + Mathf.RoundToInt(position.z).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// <c>Minimap.AddPing(Vector3 pos, string name, ...)</c>. Matched on the
        /// two parameters that carry the meaning; anything the build adds after
        /// them has to be optional, so its own defaults can be supplied.
        /// </summary>
        private MethodInfo? ResolveAddPing()
        {
            if (_addPingResolved) return _addPing;
            _addPingResolved = true;

            try
            {
                foreach (var candidate in typeof(Minimap).GetMethods(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!string.Equals(candidate.Name, "AddPing", StringComparison.Ordinal)) continue;

                    var parameters = candidate.GetParameters();
                    if (parameters.Length < 2) continue;
                    if (parameters[0].ParameterType != typeof(Vector3)) continue;
                    if (parameters[1].ParameterType != typeof(string)) continue;

                    var usable = true;
                    for (var i = 2; i < parameters.Length; i++)
                    {
                        if (parameters[i].IsOptional) continue;
                        usable = false;
                        break;
                    }
                    if (!usable) continue;

                    _addPing = candidate;
                    return _addPing;
                }

                _log.Warn("Minimap.AddPing was not found, so inbound pings will show as short-lived pins. "
                    + "If the game has updated, this is the signature to check.");
            }
            catch (Exception ex)
            {
                _log.Warn("could not look up Minimap.AddPing: " + ex.Message);
            }

            return _addPing;
        }

        private static object[] BuildPingArgs(MethodInfo method, Vector3 position, string name)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];
            args[0] = position;
            args[1] = name;

            for (var i = 2; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                // An optional parameter whose default the metadata does not
                // carry comes back as DBNull or Missing rather than a value;
                // its type's own default is the closest honest answer.
                var value = parameter.DefaultValue;
                if (value == null || value == DBNull.Value || value is Missing)
                {
                    value = parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
                }

                args[i] = value!;
            }

            return args;
        }

        /// <summary>
        /// The pin the game uses for its own pings, when the build has one; a
        /// plain marker otherwise, which is only ever the fallback's fallback.
        /// </summary>
        private static readonly Minimap.PinType PingPinType = ResolvePingPinType();

        private static Minimap.PinType ResolvePingPinType()
        {
            foreach (var name in new[] { "Ping", "Shout" })
            {
                if (Enum.TryParse<Minimap.PinType>(name, out var type)
                    && Enum.IsDefined(typeof(Minimap.PinType), type))
                {
                    return type;
                }
            }
            return Minimap.PinType.Icon0;
        }

        private static Exception Unwrap(Exception ex) =>
            (ex as TargetInvocationException)?.InnerException ?? ex;

        private readonly struct PingPin
        {
            public PingPin(object pin, float expiry)
            {
                Pin = pin;
                Expiry = expiry;
            }

            public object Pin { get; }

            public float Expiry { get; }
        }

        /// <summary>A local-only chat line. Never used for anything secret (§5.3).</summary>
        public void LocalMessage(string message)
        {
            try
            {
                var chat = Chat.instance;
                if (chat == null) return;
                chat.AddString("<color=#7ec8e3>ValheimRelay</color>: " + message);
            }
            catch (Exception ex)
            {
                _log.Warn("could not write to chat: " + ex.Message);
            }
        }
    }
}
