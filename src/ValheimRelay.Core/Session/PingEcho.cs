using System;
using System.Collections.Generic;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// Remembers pings this client has already seen through the GAME, so the
    /// relayed copy of the same ping is not drawn a second time.
    /// <para>
    /// A ping is already broadcast to every player by Valheim's own routed RPC.
    /// Once a mod also forwards it to the relay, §3.3's peer fan-out delivers it
    /// to every OTHER mod in the room — which have all just rendered it
    /// natively. Without this, one player's ping is one marker and one sound for
    /// them and two of each for everybody else, and the second arrives a network
    /// round trip late so it reads as a stutter rather than as a duplicate.
    /// </para>
    /// <para>
    /// It is deliberately NOT a mute window. A match is CONSUMED, so exactly one
    /// relayed copy is swallowed per ping observed in game and a second ping at
    /// the same place — a browser deliberately pinging where somebody just
    /// pinged — still draws. Suppressing by elapsed time alone would silently
    /// eat that, and a map ping that does nothing is the one outcome this
    /// feature cannot afford.
    /// </para>
    /// <para>
    /// This is the half of the duplicate problem that is about the GAME. The
    /// other half is about the WIRE, and the two need different fixes: without a
    /// sender filter at the capture site, N modded clients would each forward
    /// the same ping and the web map would draw N rings. See
    /// <c>RelayBehaviour.OnGamePing</c>.
    /// </para>
    /// </summary>
    public sealed class PingEcho
    {
        /// <summary>
        /// How long a relayed copy is allowed to arrive late and still be
        /// recognised as the ping we already drew. It has to cover a round trip
        /// to the relay and back on a poor connection; overshooting costs
        /// nothing, because a match is consumed rather than timed out.
        /// </summary>
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Metres. The coordinates make a float -> double -> JSON -> double ->
        /// float round trip, which is exact, so an exact match would in
        /// principle do. This is slack for a relay or a mod that rounds on the
        /// way past, and it is far below the distance at which two pings mean
        /// two different places.
        /// </summary>
        public const double DefaultMatchRadius = 2.0;

        /// <summary>
        /// A hard ceiling, so a player spamming pings while disconnected from
        /// the relay cannot grow this without bound. The oldest goes first.
        /// </summary>
        private const int MaxTracked = 32;

        private readonly List<Seen> _seen = new List<Seen>();
        private readonly TimeSpan _window;
        private readonly double _radiusSquared;

        public PingEcho(TimeSpan? window = null, double matchRadius = DefaultMatchRadius)
        {
            _window = window ?? DefaultWindow;
            _radiusSquared = matchRadius * matchRadius;
        }

        /// <summary>Note a ping the game itself has just shown.</summary>
        public void Observe(double x, double z, TimeSpan now)
        {
            Prune(now);
            if (_seen.Count >= MaxTracked) _seen.RemoveAt(0);
            _seen.Add(new Seen(x, z, now));
        }

        /// <summary>
        /// True when this inbound ping is the relayed copy of one already drawn,
        /// in which case the match is consumed and will not swallow another.
        /// </summary>
        public bool ShouldSuppress(double x, double z, TimeSpan now)
        {
            Prune(now);

            // Newest first: when a player pings twice in the same spot, the
            // copies arrive in order, and consuming the newest keeps the
            // remaining entry's deadline the more generous of the two.
            for (var i = _seen.Count - 1; i >= 0; i--)
            {
                var dx = _seen[i].X - x;
                var dz = _seen[i].Z - z;
                if ((dx * dx) + (dz * dz) > _radiusSquared) continue;

                _seen.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>Session stop or world unload: nothing seen before is relevant after.</summary>
        public void Clear() => _seen.Clear();

        public int Tracked => _seen.Count;

        private void Prune(TimeSpan now)
        {
            for (var i = _seen.Count - 1; i >= 0; i--)
            {
                // `now` going backwards is a clock reset rather than an
                // expiry; dropping the entry is the safe way to read it,
                // because a stale one would swallow a real ping.
                var age = now - _seen[i].At;
                if (age >= _window || age < TimeSpan.Zero) _seen.RemoveAt(i);
            }
        }

        private readonly struct Seen
        {
            public Seen(double x, double z, TimeSpan at)
            {
                X = x;
                Z = z;
                At = at;
            }

            public double X { get; }
            public double Z { get; }
            public TimeSpan At { get; }
        }
    }
}
