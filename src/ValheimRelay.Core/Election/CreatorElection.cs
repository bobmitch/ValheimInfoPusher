using System;
using System.Collections.Generic;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Core.Election
{
    /// <summary>
    /// Decides which modded client creates the room (§5.1). Deterministic and
    /// negotiation-free: every client computes the same answer from state it
    /// already has.
    /// </summary>
    public static class CreatorElection
    {
        /// <summary>
        /// The host creates. Otherwise the numerically lowest connected peer id
        /// creates, counting this client among the candidates.
        /// </summary>
        public static bool IsElectedCreator(IPeerView peers)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (peers.IsHost) return true;

            var self = peers.SelfPeerId;
            foreach (var peer in peers.PeerIds)
            {
                if (peer == self) continue;
                if (peer < self) return false;
            }

            return true;
        }

        /// <summary>
        /// This client's rank in the creator ordering: 0 for the elected creator,
        /// 1 for the next in line, and so on. Diagnostic — the delay below does
        /// not use it, for the reason given there.
        /// </summary>
        public static int CreatorRank(IPeerView peers)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (peers.IsHost) return 0;

            var self = peers.SelfPeerId;
            var rank = 0;
            foreach (var peer in peers.PeerIds)
            {
                if (peer == self) continue;
                if (peer < self) rank++;
            }

            return rank;
        }

        /// <summary>
        /// A deterministic per-client delay before creating, spread over
        /// <paramref name="spread"/>.
        /// <para>
        /// The race this exists for is two clients loading simultaneously, each
        /// with an empty peer list, each therefore believing it is the lowest id
        /// and creating (§5.1). Note what that means: both are rank 0, so
        /// staggering by <see cref="CreatorRank"/> would do nothing at all — a
        /// client with rank above 0 is by definition not the elected creator and
        /// never reaches this code. The stagger has to come from something that
        /// differs between two clients that cannot see each other, and the peer
        /// id is the only such thing to hand.
        /// </para>
        /// <para>
        /// The later client spends its extra wait listening, hears the earlier
        /// one's announcement, and joins — so the tiebreak never has to run. The
        /// tiebreak still exists for when it does.
        /// </para>
        /// </summary>
        public static TimeSpan CreationStagger(IPeerView peers, TimeSpan spread)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));

            // The host is unambiguously the creator, so it should never wait.
            if (peers.IsHost || spread <= TimeSpan.Zero) return TimeSpan.Zero;

            var fraction = (Mix(peers.SelfPeerId) % 10_000UL) / 10_000.0;
            return TimeSpan.FromTicks((long)(spread.Ticks * fraction));
        }

        /// <summary>
        /// SplitMix64 finaliser. Peer ids are often sequential or share high bits,
        /// so using them raw would cluster the delays exactly where they need to
        /// be spread.
        /// </summary>
        private static ulong Mix(long value)
        {
            var x = unchecked((ulong)value + 0x9E3779B97F4A7C15UL);
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
}
