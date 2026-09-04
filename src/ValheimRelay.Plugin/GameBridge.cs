using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
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
        private readonly Func<PingStyle>? _pingStyle;

        /// <param name="pingStyle">
        /// Read per ping rather than captured, so editing the config file takes
        /// effect without a restart — which is the point of having the setting
        /// at all (§7).
        /// </param>
        public GameBridge(ILog log, Func<PingStyle>? pingStyle = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _pingStyle = pingStyle;
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

        private const BindingFlags AnyInstanceMethod =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<PingPin> _pingPins = new List<PingPin>();

        private MethodInfo? _chatPing;
        private bool _chatPingResolved;
        private MethodInfo? _groundHeight;
        private MethodInfo? _groundHeightDirect;
        private MethodInfo? _generatedHeight;
        private bool _heightResolved;
        private MethodInfo? _addPing;
        private bool _addPingResolved;
        private bool _pingApiDescribed;
        private string? _pingPathLogged;

        /// <summary>
        /// Valheim's own transient ping, so a map ping feels identical in-game.
        /// <para>
        /// Deliberately not <c>Minimap.ShowPointOnMap</c>. Despite the name that
        /// one is "reveal this place to the player": it forces the large map
        /// open and recentres it, and draws no ping at all.
        /// </para>
        /// <para>
        /// A ping is not one call but a whole path. Everything a player
        /// recognises as a ping — the pulsing map marker, the sound, the
        /// "<c>Name: Ping</c>" text floating in the world — is what
        /// <c>Chat.OnNewChatMessage</c> does with a <c>Talker.Type.Ping</c>
        /// message. <c>Minimap.AddPing</c> is only the marker, which is why
        /// going straight to it produced a silent, static one. So the first
        /// choice is to hand the game a ping message locally and let its own
        /// code render it, exactly as it renders another player's ping. It is
        /// local only: nothing is sent over the game's network, because §3.3
        /// already delivers the ping to every mod in the room and a rebroadcast
        /// would multiply it by the number of modded clients.
        /// </para>
        /// <para>
        /// §4.3 says not to assume a signature that has not been checked
        /// against the build in front of us — and this is the signature §4.3
        /// singles out as having changed across versions — so the method and
        /// its arguments are matched by shape rather than bound at compile
        /// time, with two narrower fallbacks behind it: <c>Minimap.AddPing</c>
        /// for the marker alone, then a short-lived pin plus a chat line.
        /// Whichever path runs says so in the log, once, next to a dump of what
        /// this build actually offers.
        /// </para>
        /// </summary>
        public void ShowPing(double x, double z, string? who)
        {
            try
            {
                if (Minimap.instance == null) return;

                DescribePingApi();

                // A ping is a place in the world, not just a spot on the map:
                // the world text and the sound both sit at this point, so a y of
                // zero puts them at sea level, under the ground the ping is on.
                var position = new Vector3((float)x, GroundHeight((float)x, (float)z), (float)z);
                var name = string.IsNullOrEmpty(who) ? "Ping" : who!;
                var style = ReadPingStyle();

                if (style == PingStyle.Auto && TryVanillaPing(position, name)) return;
                if (style != PingStyle.Pin && TryMinimapPing(position, name)) return;
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

        /// <summary>
        /// Feeds the game a ping message the way the network would, so its own
        /// chat path draws the marker, plays the sound and floats the text.
        /// </summary>
        private bool TryVanillaPing(Vector3 position, string name)
        {
            var chat = Chat.instance;
            if (chat == null) return false;

            var method = ResolveChatPing();
            if (method == null) return false;

            var args = BuildChatPingArgs(method, position, name);
            if (args == null) return false;

            try
            {
                method.Invoke(chat, args);
                LogPingPath("the game's own chat path — " + Describe(method));
                return true;
            }
            catch (Exception ex)
            {
                // Once it has thrown it will keep throwing, and it throws from
                // inside the game's code: stop calling it rather than risk
                // leaving chat half-updated on every later ping.
                _chatPing = null;
                _log.Warn("the game's ping path failed, falling back to the minimap: " + Unwrap(ex).Message);
                return false;
            }
        }

        private bool TryMinimapPing(Vector3 position, string name)
        {
            var method = ResolveAddPing();
            if (method == null) return false;

            try
            {
                method.Invoke(Minimap.instance, BuildPingArgs(method, position, name));
                LogPingPath("Minimap.AddPing — the marker only, with no sound or world text");
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

        private PingStyle ReadPingStyle()
        {
            try
            {
                return _pingStyle?.Invoke() ?? PingStyle.Auto;
            }
            catch (Exception)
            {
                return PingStyle.Auto;
            }
        }

        /// <summary>
        /// Says how pings are being drawn, and says it again if that ever
        /// changes — which is the one line worth having in the log when a ping
        /// looks wrong in-game.
        /// </summary>
        private void LogPingPath(string path)
        {
            if (string.Equals(_pingPathLogged, path, StringComparison.Ordinal)) return;
            _pingPathLogged = path;
            _log.Info("pings are being shown through " + path);
        }

        private void ShowFallbackPing(Vector3 position, string name)
        {
            var pin = AddPinAt(position, PingPinType, name);
            if (pin != null)
            {
                _pingPins.Add(new PingPin(pin, Time.realtimeSinceStartup + PingLifetimeSeconds));
            }

            LogPingPath("a short-lived pin and a chat line");

            // Without the game's own ping there is no sound and no on-screen
            // marker, so say where it was — a pin on a map you are not looking
            // at is easy to miss entirely.
            LocalMessage(name + " pinged "
                + Mathf.RoundToInt(position.x).ToString(CultureInfo.InvariantCulture) + ", "
                + Mathf.RoundToInt(position.z).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The height of the land at (x, z).
        /// <para>
        /// Two sources, because neither covers the whole map. Where the terrain
        /// is loaded the game can measure it, which is the only way to see
        /// ground a player has raised or dug since the world was generated.
        /// Everywhere else — and that is most of the map, most of the time —
        /// the generator knows what the terrain would be without it having to
        /// exist yet. Failing both, zero: a ping at sea level is where this
        /// started, and is still better than no ping.
        /// </para>
        /// <para>
        /// <b>A MISS IS NOT ZERO.</b> The two <c>ZoneSystem.GetGroundHeight</c>
        /// overloads report "nothing there" differently. The <c>out</c> one
        /// writes <c>0f</c> and returns <c>false</c>. The <c>float</c> one takes
        /// its position by value, raises its own copy to 6000 for the raycast,
        /// and on a miss returns <em>the y of the position it was handed</em> —
        /// so testing its answer for zero accepts the probe altitude as a
        /// height. That is what put every ping outside the loaded zones 5 km
        /// into the sky, and it never reached the generator fallback that would
        /// have answered correctly. The in-game map looked right throughout,
        /// because <c>Minimap.WorldToMapPoint</c> reads x and z and never y.
        /// </para>
        /// </summary>
        private float GroundHeight(float x, float z)
        {
            ResolveHeight();

            try
            {
                // This y is NOT the raycast origin: both overloads overwrite
                // it with 6000 before casting down. It survives only as what the
                // float overload hands back when it hits nothing, which is what
                // the check below tests for — so it has to stay an altitude no
                // terrain in this game reaches.
                var probe = new Vector3(x, 5000f, z);
                var zones = ZoneSystem.instance;

                if (zones != null && _groundHeight != null)
                {
                    var args = Fill(_groundHeight, probe);
                    if (_groundHeight.Invoke(zones, args) is bool found && found && args[1] is float measured)
                    {
                        return measured;
                    }
                }

                if (zones != null && _groundHeightDirect != null
                    && _groundHeightDirect.Invoke(zones, Fill(_groundHeightDirect, probe)) is float direct
                    && !Mathf.Approximately(direct, probe.y)
                    && Mathf.Abs(direct) > 0.001f)
                {
                    // Neither of the two ways a build can say it found nothing:
                    // handing the probe's own altitude straight back, which is
                    // what this overload does, or answering exactly zero.
                    return direct;
                }

                var generator = WorldGenerator.instance;
                if (generator != null && _generatedHeight != null
                    && _generatedHeight.Invoke(generator, Fill(_generatedHeight, x, z)) is float generated
                    && !float.IsNaN(generated))
                {
                    return generated;
                }
            }
            catch (Exception ex)
            {
                _log.Debug("could not read the ground height: " + ex.Message);
            }

            return 0f;
        }

        private void ResolveHeight()
        {
            if (_heightResolved) return;
            _heightResolved = true;

            try
            {
                // Ground before solid: the ping is meant to land on the terrain,
                // not on the roof of whatever has been built over it.
                foreach (var name in new[] { "GetGroundHeight", "GetSolidHeight" })
                {
                    foreach (var candidate in typeof(ZoneSystem).GetMethods(AnyInstanceMethod))
                    {
                        if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;

                        var parameters = candidate.GetParameters();
                        if (parameters.Length < 1 || parameters[0].ParameterType != typeof(Vector3)) continue;

                        if (candidate.ReturnType == typeof(bool)
                            && parameters.Length == 2
                            && parameters[1].IsOut
                            && parameters[1].ParameterType.GetElementType() == typeof(float))
                        {
                            if (_groundHeight == null) _groundHeight = candidate;
                        }
                        else if (candidate.ReturnType == typeof(float) && parameters.Length == 1)
                        {
                            if (_groundHeightDirect == null) _groundHeightDirect = candidate;
                        }
                    }

                    if (_groundHeight != null || _groundHeightDirect != null) break;
                }

                foreach (var candidate in typeof(WorldGenerator).GetMethods(AnyInstanceMethod))
                {
                    if (!string.Equals(candidate.Name, "GetHeight", StringComparison.Ordinal)) continue;
                    if (candidate.ReturnType != typeof(float)) continue;

                    var parameters = candidate.GetParameters();
                    if (parameters.Length < 2) continue;
                    if (parameters[0].ParameterType != typeof(float) || parameters[1].ParameterType != typeof(float)) continue;

                    // Some builds hand back a biome mask alongside the height.
                    var usable = true;
                    for (var i = 2; i < parameters.Length; i++)
                    {
                        if (parameters[i].IsOut || parameters[i].IsOptional) continue;
                        usable = false;
                        break;
                    }
                    if (!usable) continue;

                    _generatedHeight = candidate;
                    break;
                }

                if (_groundHeight == null && _groundHeightDirect == null && _generatedHeight == null)
                {
                    _log.Info("no ground-height lookup was found on this build, so pings will sit at sea level.");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("could not look up the ground-height API: " + ex.Message);
            }
        }

        /// <summary>
        /// An argument array with the values that matter at the front and each
        /// remaining parameter — <c>out</c> and optional alike — left at its
        /// type's default, which is what <see cref="MethodBase.Invoke"/> wants.
        /// </summary>
        private static object[] Fill(MethodInfo method, params object[] leading)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                if (i < leading.Length)
                {
                    args[i] = leading[i];
                    continue;
                }

                var type = parameters[i].ParameterType;
                if (type.IsByRef) type = type.GetElementType()!;
                args[i] = (type.IsValueType ? Activator.CreateInstance(type) : null)!;
            }

            return args;
        }

        /// <summary>
        /// <c>Chat.OnNewChatMessage(GameObject go, long senderID, Vector3 pos,
        /// Talker.Type type, UserInfo user, string text, string
        /// senderNetworkUserId)</c> on the current build — but the parameter
        /// list is the one §4.3 warns has changed, so it is matched on the two
        /// things a ping cannot do without: somewhere to put it, and a talker
        /// type that knows what a ping is.
        /// </summary>
        private MethodInfo? ResolveChatPing()
        {
            if (_chatPingResolved) return _chatPing;
            _chatPingResolved = true;

            try
            {
                foreach (var candidate in typeof(Chat).GetMethods(AnyInstanceMethod))
                {
                    if (!string.Equals(candidate.Name, "OnNewChatMessage", StringComparison.Ordinal)) continue;

                    var position = false;
                    var talkerType = false;
                    foreach (var parameter in candidate.GetParameters())
                    {
                        if (parameter.ParameterType == typeof(Vector3)) position = true;
                        else if (parameter.ParameterType.IsEnum && HasEnumName(parameter.ParameterType, "Ping")) talkerType = true;
                    }

                    if (!position || !talkerType) continue;

                    _chatPing = candidate;
                    return _chatPing;
                }

                _log.Info("Chat.OnNewChatMessage is not usable for pings on this build, so pings will be drawn on the map only.");
            }
            catch (Exception ex)
            {
                _log.Warn("could not look up the chat ping path: " + ex.Message);
            }

            return _chatPing;
        }

        /// <summary>
        /// Fills the chat handler's parameters by type rather than by position,
        /// which is what makes this survive the reshuffles §4.3 warns about.
        /// Returns null when something needed cannot be built, so the caller
        /// can fall back rather than call the game with a wrong argument.
        /// </summary>
        private object[]? BuildChatPingArgs(MethodInfo method, Vector3 position, string name)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];

            // Older builds pass the sender's name as a bare string where newer
            // ones pass a UserInfo; which it is decides what the strings mean.
            var carriesUser = false;
            foreach (var parameter in parameters)
            {
                if (!IsUserInfoLike(parameter.ParameterType)) continue;
                carriesUser = true;
                break;
            }

            var nameSlot = carriesUser ? 0 : 1;
            var textSlot = carriesUser ? 1 : 2;
            var strings = 0;

            for (var i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;

                if (type == typeof(Vector3))
                {
                    args[i] = position;
                }
                else if (type == typeof(long))
                {
                    args[i] = PingSenderId(name);
                }
                else if (type.IsEnum)
                {
                    if (!TryParseEnum(type, "Ping", out var ping)) return null;
                    args[i] = ping!;
                }
                else if (type == typeof(string))
                {
                    strings++;
                    args[i] = strings == nameSlot ? name
                        : strings == textSlot ? "Ping"
                        : string.Empty;
                }
                else if (IsUserInfoLike(type))
                {
                    var user = BuildUser(type, name);
                    if (user == null) return null;
                    args[i] = user;
                }
                else
                {
                    // Notably the GameObject: the game passes null there for a
                    // ping, because a ping belongs to a place and not to a
                    // talker who might walk away from it.
                    args[i] = (type.IsValueType ? Activator.CreateInstance(type) : null)!;
                }
            }

            return args;
        }

        /// <summary>
        /// A <c>UserInfo</c> carrying the web user's name. Built from the
        /// build's own local-user factory where there is one, so any field this
        /// mod has never heard of is still filled in the way the game expects.
        /// </summary>
        private static object? BuildUser(Type type, string name)
        {
            object? user = null;

            try
            {
                var factory = type.GetMethod(
                    "GetLocalUser", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);

                if (factory != null && type.IsAssignableFrom(factory.ReturnType))
                {
                    user = factory.Invoke(null, null);
                }
            }
            catch (Exception)
            {
                user = null;
            }

            if (user == null)
            {
                try
                {
                    user = Activator.CreateInstance(type);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            if (user == null) return null;

            // Whichever of these the build has: the display name is read from
            // the gamertag first on the platforms that have one.
            SetStringMember(user, "Name", name);
            SetStringMember(user, "m_name", name);
            SetStringMember(user, "Gamertag", name);
            SetStringMember(user, "m_gamertag", name);
            return user;
        }

        private static void SetStringMember(object target, string member, string value)
        {
            try
            {
                var type = target.GetType();

                var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(string) && !field.IsInitOnly)
                {
                    field.SetValue(target, value);
                    return;
                }

                var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.PropertyType == typeof(string) && property.CanWrite)
                {
                    property.SetValue(target, value, null);
                }
            }
            catch (Exception)
            {
                // Best effort: a name that does not stick is a cosmetic loss,
                // and never a reason to drop the ping.
            }
        }

        /// <summary>
        /// The world text the game keeps per talker is keyed by sender, so
        /// pings from the same web user replace each other the way one
        /// player's pings do, while two web users get a line each.
        /// </summary>
        private static long PingSenderId(string name) => name.GetHashCode();

        private static bool IsUserInfoLike(Type type) =>
            !type.IsPrimitive
            && !type.IsEnum
            && type != typeof(string)
            && type != typeof(Vector3)
            && !typeof(UnityEngine.Object).IsAssignableFrom(type);

        private static bool HasEnumName(Type type, string name)
        {
            foreach (var candidate in Enum.GetNames(type))
            {
                if (string.Equals(candidate, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool TryParseEnum(Type type, string name, out object? value)
        {
            value = null;
            if (!HasEnumName(type, name)) return false;
            value = Enum.Parse(type, name);
            return true;
        }

        /// <summary>
        /// Logs what this build offers, once, the first time a ping arrives.
        /// The mod cannot be built against the game's assemblies on a machine
        /// that does not have the game, so when a ping renders differently than
        /// it should, this is the log line that says which call to reach for
        /// next — cheaper than another round of guessing.
        /// </summary>
        private void DescribePingApi()
        {
            if (_pingApiDescribed) return;
            _pingApiDescribed = true;

            try
            {
                var report = new StringBuilder("ping API on this game build:");
                AppendMethods(report, typeof(Chat), "OnNewChatMessage", "AddInworldText", "SendPing", "RPC_ChatMessage");
                AppendMethods(report, typeof(Minimap), "AddPing", "ShowPointOnMap");
                AppendMethods(report, typeof(ZoneSystem), "GetGroundHeight", "GetSolidHeight");
                AppendMethods(report, typeof(WorldGenerator), "GetHeight");
                AppendEffectFields(report, typeof(Chat));
                AppendEffectFields(report, typeof(Minimap));
                _log.Info(report.ToString());
            }
            catch (Exception ex)
            {
                _log.Debug("could not describe the ping API: " + ex.Message);
            }
        }

        private static void AppendMethods(StringBuilder report, Type type, params string[] names)
        {
            foreach (var method in type.GetMethods(AnyInstanceMethod))
            {
                foreach (var name in names)
                {
                    if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                    report.Append("\n  ").Append(type.Name).Append('.').Append(Describe(method));
                    break;
                }
            }
        }

        /// <summary>
        /// Effect lists are where the game keeps its sounds, so if a ping is
        /// silent this is the list of places the sound could be hiding.
        /// </summary>
        private static void AppendEffectFields(StringBuilder report, Type type)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType.Name.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) < 0) continue;
                report.Append("\n  ").Append(type.Name).Append('.').Append(field.Name)
                      .Append(" : ").Append(field.FieldType.Name);
            }
        }

        private static string Describe(MethodInfo method)
        {
            var text = new StringBuilder(method.Name).Append('(');
            var parameters = method.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(parameters[i].ParameterType.Name);
                if (parameters[i].IsOptional) text.Append('?');
            }
            return text.Append(')').ToString();
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
