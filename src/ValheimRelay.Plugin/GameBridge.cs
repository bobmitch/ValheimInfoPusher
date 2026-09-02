using System;
using System.Collections.Generic;
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
            try
            {
                var minimap = Minimap.instance;
                if (minimap == null) return null;

                // y is irrelevant for a map pin; the game clamps it to terrain.
                var position = new Vector3((float)x, 0f, (float)z);
                return minimap.AddPin(position, ToPinType(icon), label ?? string.Empty, save: false, isChecked: false);
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

        /// <summary>Valheim's own transient ping, so a map ping feels identical in-game.</summary>
        public void ShowPing(double x, double z, string? who)
        {
            try
            {
                var minimap = Minimap.instance;
                if (minimap == null) return;
                minimap.ShowPointOnMap(new Vector3((float)x, 0f, (float)z));
            }
            catch (Exception ex)
            {
                _log.Warn("could not show a ping: " + ex.Message);
            }
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
