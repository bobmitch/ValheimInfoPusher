using System;
using System.Collections.Generic;
using System.Text;
using ValheimRelay.Core.Json;

namespace ValheimRelay.Core.Protocol
{
    /// <summary>
    /// Serialises outbound frames and parses inbound ones. The single place that
    /// knows the wire format in §3, so map/mod drift has exactly one file to
    /// review.
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>
        /// The relay's <c>MAX_MESSAGE_BYTES</c> (§1.5). A frame at or over this is
        /// refused by the relay, so we refuse it first and log rather than
        /// silently losing the connection.
        /// </summary>
        public const int MaxFrameBytes = 8192;

        public static int MeasureBytes(string frame) => Encoding.UTF8.GetByteCount(frame);

        public static bool FitsInFrame(string frame) => MeasureBytes(frame) <= MaxFrameBytes;

        // ---------------------------------------------------------------- write

        public static string WriteHello(HelloFrame hello)
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", FrameTypes.Hello)
                .Prop("v", ProtocolVersion.Current)
                .Prop("name", hello.Name)
                .Prop("uid", hello.Uid)
                .Prop("mod", hello.ModVersion);

            // Only sent when false: the default is "sharing", and the highest
            // value of an optional field is that old maps ignore it correctly.
            if (!hello.SharingPosition) w.Prop("share", false);

            // §3.7: omitted, not false, when off — the same rule as `share`.
            if (hello.FogRevealRadius is double radius)
            {
                w.Name("fog").BeginObject().Prop("r", radius, 1).EndObject();
            }

            if (!hello.World.IsEmpty)
            {
                w.Name("world").BeginObject()
                    .Prop("name", hello.World.Name)
                    .Prop("seed", hello.World.Seed)
                    .Prop("seedInt", hello.World.SeedInt)
                    .Prop("uid", hello.World.Uid)
                    .EndObject();
            }

            return w.EndObject().ToString();
        }

        public static string WritePosition(in PositionSample p)
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", FrameTypes.Position)
                .Prop("v", ProtocolVersion.Current)
                .Prop("x", p.X)
                .Prop("z", p.Z)
                .Prop("y", p.Y, 1)
                .Prop("rot", p.RotationDegrees, 1);

            if (!string.IsNullOrEmpty(p.Biome)) w.Prop("biome", p.Biome);
            if (p.IncludeHealth)
            {
                w.Prop("hp", p.Health).Prop("maxHp", p.MaxHealth);
            }
            if (p.Dead) w.Prop("dead", true);

            return w.Prop("t", p.TimestampMs).EndObject().ToString();
        }

        public static string WritePing(in PingFrame ping)
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", FrameTypes.Ping)
                .Prop("v", ProtocolVersion.Current)
                .Prop("x", ping.X)
                .Prop("z", ping.Z);
            if (!string.IsNullOrEmpty(ping.Name)) w.Prop("name", ping.Name);
            return w.Prop("t", ping.TimestampMs).EndObject().ToString();
        }

        public static string WriteMarker(MarkerFrame marker)
        {
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", FrameTypes.Marker)
                .Prop("v", ProtocolVersion.Current)
                .Prop("op", marker.Op)
                .Prop("id", marker.Id);

            // On a remove only `id` is required (§3.4) and coordinates would be
            // noise; the receiver looks the marker up by id.
            if (!marker.IsRemove)
            {
                w.Prop("x", marker.X).Prop("z", marker.Z);
                if (!string.IsNullOrEmpty(marker.Label)) w.Prop("label", marker.Label);
                w.Prop("icon", MarkerIcons.Normalise(marker.Icon));
            }

            return w.Prop("t", marker.TimestampMs).EndObject().ToString();
        }

        /// <summary>
        /// One fog tile (§3.7). <c>w</c> and <c>px</c> go on every frame so a
        /// map never has to assume the unmodded world, and so the frame decodes
        /// without having seen a <c>hello</c> first. No <c>layer</c>: v1 sends
        /// only the player's own exploration, and §3.7 reserves the field.
        /// </summary>
        public static string WriteFog(int mapSize, double metresPerPixel, int tx, int tz, string encoding, string data)
        {
            var w = new JsonWriter();
            return w.BeginObject()
                .Prop("type", FrameTypes.Fog)
                .Prop("v", ProtocolVersion.Current)
                .Prop("w", mapSize)
                .Prop("px", metresPerPixel, 4)
                .Prop("n", FogTiles.TileSize)
                .Prop("tx", tx)
                .Prop("tz", tz)
                .Prop("enc", encoding)
                .Prop("d", data)
                .EndObject()
                .ToString();
        }

        // ----------------------------------------------------------------- read

        /// <summary>
        /// Parses one inbound text frame. Returns null for anything unparseable
        /// or without a string <c>type</c> — never throws, because the caller is
        /// a socket pump and a bad frame from one peer must not stop the others.
        /// </summary>
        public static JsonValue? ParseFrame(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (!JsonParser.TryParse(text, out var value)) return null;
            if (value.Kind != JsonKind.Object) return null;
            if (value["type"].AsString() == null) return null;
            return value;
        }

        public static string? TypeOf(JsonValue frame) => frame["type"].AsString();

        public static WelcomeFrame? ReadWelcome(JsonValue frame)
        {
            var code = frame["code"].AsString();
            var playerId = frame["playerId"].AsString();
            if (code == null || playerId == null) return null;

            var roster = new List<RosterEntry>();
            foreach (var entry in frame["players"].AsArray())
            {
                var id = entry["playerId"].AsString();
                if (id == null) continue;
                roster.Add(new RosterEntry(id, entry["name"].AsString(), entry["uid"].AsString()));
            }

            // An empty-string token is not a token; the relay omits the field for
            // joiners, but be defensive about a "" that would make us think we
            // own the room.
            var token = frame["token"].AsString();
            if (string.IsNullOrEmpty(token)) token = null;

            return new WelcomeFrame(code, playerId, token, roster);
        }

        public static PingFrame? ReadPing(JsonValue frame)
        {
            if (frame["x"].Kind != JsonKind.Number || frame["z"].Kind != JsonKind.Number) return null;
            return new PingFrame(
                frame["x"].AsDouble(),
                frame["z"].AsDouble(),
                frame["name"].AsString(),
                frame["t"].AsLong());
        }

        public static MarkerFrame? ReadMarker(JsonValue frame)
        {
            var id = frame["id"].AsString();
            if (string.IsNullOrEmpty(id)) return null;

            var op = frame["op"].AsString(MarkerOps.Add)!;
            if (!string.Equals(op, MarkerOps.Add, StringComparison.Ordinal) &&
                !string.Equals(op, MarkerOps.Remove, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(op, MarkerOps.Add, StringComparison.Ordinal) &&
                (frame["x"].Kind != JsonKind.Number || frame["z"].Kind != JsonKind.Number))
            {
                return null;
            }

            return new MarkerFrame(
                op,
                id!,
                frame["x"].AsDouble(),
                frame["z"].AsDouble(),
                frame["label"].AsString(),
                MarkerIcons.Normalise(frame["icon"].AsString()),
                frame["t"].AsLong());
        }

        /// <summary>
        /// Whether a <c>request_fog</c> (§3.7) is for this player. Its <c>uid</c>
        /// is optional: absent addresses every mod, present addresses one. The
        /// relay broadcasts map frames to every mod (§1.3), so this is the only
        /// place the addressing is applied.
        /// </summary>
        public static bool IsRequestFogFor(JsonValue frame, string? uid)
        {
            var target = frame["uid"].AsString();
            if (target == null) return true;
            return uid != null && string.Equals(target, uid, StringComparison.Ordinal);
        }

        /// <summary>The <c>playerId</c> the relay stamped on a relayed frame, if any.</summary>
        public static string? ReadPlayerId(JsonValue frame) => frame["playerId"].AsString();
    }
}
