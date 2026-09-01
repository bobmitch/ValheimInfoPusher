using System;
using System.Collections.Generic;

namespace ValheimRelay.Core.Protocol
{
    /// <summary>World identity, sent once in <c>hello</c> (§3.1) so the map can draw the right terrain.</summary>
    public readonly struct WorldInfo
    {
        public WorldInfo(string? name, string? seed, long seedInt, string? uid)
        {
            Name = name;
            Seed = seed;
            SeedInt = seedInt;
            Uid = uid;
        }

        public string? Name { get; }
        public string? Seed { get; }
        public long SeedInt { get; }

        /// <summary>Valheim's world UID. Also the key of the reclaim store (§5.3).</summary>
        public string? Uid { get; }

        public bool IsEmpty => string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(Uid);
    }

    /// <summary>The mod's identity frame (§3.1).</summary>
    public sealed class HelloFrame
    {
        public HelloFrame(string name, string uid, string modVersion, WorldInfo world, bool sharingPosition = true)
        {
            Name = name ?? string.Empty;
            Uid = uid ?? string.Empty;
            ModVersion = modVersion ?? string.Empty;
            World = world;
            SharingPosition = sharingPosition;
        }

        public string Name { get; }

        /// <summary>Stable per character save, salted (§8 and the addendum in §12.1).</summary>
        public string Uid { get; }

        public string ModVersion { get; }
        public WorldInfo World { get; }

        /// <summary>
        /// False when the player has opted out of position sharing but stayed in
        /// the session (§7 <c>ShareMyPosition</c>). Without this the map cannot
        /// tell "present, deliberately hidden" from "stale/disconnected", and
        /// would show the opted-out player as a frozen ghost.
        /// </summary>
        public bool SharingPosition { get; }
    }

    /// <summary>A single position sample (§3.2). Struct: this is built at 1 Hz and should not allocate.</summary>
    public readonly struct PositionSample
    {
        public PositionSample(
            double x,
            double z,
            double y,
            double rotationDegrees,
            string? biome,
            int health,
            int maxHealth,
            bool includeHealth,
            bool dead,
            long timestampMs)
        {
            X = x;
            Z = z;
            Y = y;
            RotationDegrees = rotationDegrees;
            Biome = biome;
            Health = health;
            MaxHealth = maxHealth;
            IncludeHealth = includeHealth;
            Dead = dead;
            TimestampMs = timestampMs;
        }

        public double X { get; }
        public double Z { get; }
        public double Y { get; }
        public double RotationDegrees { get; }
        public string? Biome { get; }
        public int Health { get; }
        public int MaxHealth { get; }

        /// <summary>Gated by the <c>ShareHealth</c> config toggle (§7).</summary>
        public bool IncludeHealth { get; }

        /// <summary>
        /// The player is dead and this is their corpse location. Without it a map
        /// shows a dead player as a live one standing still for ever, which reads
        /// as a bug in the mod.
        /// </summary>
        public bool Dead { get; }

        public long TimestampMs { get; }

        /// <summary>
        /// Squared horizontal distance to another sample. Used by the send
        /// dead-band (§12.5) without paying for a square root at 1 Hz.
        /// </summary>
        public double HorizontalDistanceSquaredTo(in PositionSample other)
        {
            var dx = X - other.X;
            var dz = Z - other.Z;
            return (dx * dx) + (dz * dz);
        }
    }

    /// <summary>A transient "look here" (§3.3).</summary>
    public readonly struct PingFrame
    {
        public PingFrame(double x, double z, string? name, long timestampMs)
        {
            X = x;
            Z = z;
            Name = name;
            TimestampMs = timestampMs;
        }

        public double X { get; }
        public double Z { get; }
        public string? Name { get; }
        public long TimestampMs { get; }
    }

    /// <summary>A session-lived map marker (§3.4).</summary>
    public sealed class MarkerFrame
    {
        public MarkerFrame(string op, string id, double x, double z, string? label, string? icon, long timestampMs)
        {
            Op = op;
            Id = id;
            X = x;
            Z = z;
            Label = label;
            Icon = icon;
            TimestampMs = timestampMs;
        }

        public string Op { get; }

        /// <summary>Globally unique, namespaced with the sender's <c>uid</c>.</summary>
        public string Id { get; }

        public double X { get; }
        public double Z { get; }
        public string? Label { get; }
        public string? Icon { get; }
        public long TimestampMs { get; }

        public bool IsAdd => string.Equals(Op, MarkerOps.Add, StringComparison.Ordinal);
        public bool IsRemove => string.Equals(Op, MarkerOps.Remove, StringComparison.Ordinal);
    }

    /// <summary>One entry of <c>welcome.players</c> (§1.2).</summary>
    public readonly struct RosterEntry
    {
        public RosterEntry(string playerId, string? name, string? uid)
        {
            PlayerId = playerId;
            Name = name;
            Uid = uid;
        }

        public string PlayerId { get; }
        public string? Name { get; }
        public string? Uid { get; }
    }

    /// <summary>The relay's one-per-connection <c>welcome</c> (§1.2).</summary>
    public sealed class WelcomeFrame
    {
        public WelcomeFrame(string code, string playerId, string? token, IReadOnlyList<RosterEntry> players)
        {
            Code = code;
            PlayerId = playerId;
            Token = token;
            Players = players;
        }

        /// <summary>Authoritative, canonical uppercase. Display this, not what the player typed.</summary>
        public string Code { get; }

        /// <summary>Server-assigned, new on every connection. Never persist it (§1.2).</summary>
        public string PlayerId { get; }

        /// <summary>Present only for the mod that created or reclaimed the room. A secret (§5.3).</summary>
        public string? Token { get; }

        public IReadOnlyList<RosterEntry> Players { get; }

        public bool IsCreator => !string.IsNullOrEmpty(Token);
    }
}
