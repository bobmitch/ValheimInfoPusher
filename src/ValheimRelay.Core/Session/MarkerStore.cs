using System;
using System.Collections.Generic;
using ValheimRelay.Core.Protocol;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// The markers this client owns, held for the life of the session.
    /// <para>
    /// §3.4 says markers are persistent for the session, but nothing in PLAN.md
    /// stores them and <c>request_state</c> (§3.5) replays only <c>hello</c> and
    /// a <c>position</c>. So a browser that reloads — the single most likely
    /// thing a player does with a web map — comes back to a session with every
    /// marker gone. Each mod therefore keeps its own markers and replays them
    /// alongside its <c>hello</c>.
    /// </para>
    /// <para>
    /// A mod only ever replays markers it created. Map-originated markers are the
    /// map's to remember; a mod re-announcing them would let two maps ping-pong
    /// a marker a third client deleted.
    /// </para>
    /// </summary>
    public sealed class MarkerStore
    {
        /// <summary>
        /// Bounds the replay burst. 64 markers at ~110 bytes is ~7 KB, which is
        /// several frames at the 8192-byte cap (§3.6) — the replay is emitted one
        /// marker per frame, so the cap is never the constraint, but the count
        /// still needs a ceiling or a griefer's marker spam becomes every new
        /// map's join cost.
        /// </summary>
        public const int MaxOwnedMarkers = 64;

        private readonly object _gate = new object();
        private readonly Dictionary<string, MarkerFrame> _owned = new Dictionary<string, MarkerFrame>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public int Count
        {
            get { lock (_gate) return _owned.Count; }
        }

        /// <summary>Records a marker this client created. False if the cap is reached.</summary>
        public bool Add(MarkerFrame marker)
        {
            if (marker == null) throw new ArgumentNullException(nameof(marker));
            if (!marker.IsAdd) throw new ArgumentException("expected an add", nameof(marker));

            lock (_gate)
            {
                if (_owned.ContainsKey(marker.Id))
                {
                    _owned[marker.Id] = marker;
                    return true;
                }

                if (_owned.Count >= MaxOwnedMarkers) return false;

                _owned[marker.Id] = marker;
                _order.Add(marker.Id);
                return true;
            }
        }

        public bool Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_gate)
            {
                if (!_owned.Remove(id)) return false;
                _order.Remove(id);
                return true;
            }
        }

        /// <summary>Owned markers in creation order, for replay.</summary>
        public IReadOnlyList<MarkerFrame> Snapshot()
        {
            lock (_gate)
            {
                var list = new List<MarkerFrame>(_order.Count);
                foreach (var id in _order)
                {
                    if (_owned.TryGetValue(id, out var marker)) list.Add(marker);
                }
                return list;
            }
        }

        /// <summary>Markers do not outlive the session (§3.4).</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _owned.Clear();
                _order.Clear();
            }
        }

        /// <summary>
        /// Builds an id namespaced with the owner's <c>uid</c> so two clients
        /// cannot collide (§3.4).
        /// </summary>
        public static string NewId(string ownerUid, int sequence)
            => ownerUid + ":m" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
