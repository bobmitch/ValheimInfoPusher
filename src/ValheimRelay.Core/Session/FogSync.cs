using System;
using ValheimRelay.Core.Protocol;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// The live explored map, as the game holds it (§3.7). A view, not a copy:
    /// <see cref="Explored"/> is the game's own array, read on the main thread.
    /// </summary>
    public readonly struct FogMap
    {
        public FogMap(bool[] explored, int size, double metresPerPixel, double revealRadius)
        {
            Explored = explored;
            Size = size;
            MetresPerPixel = metresPerPixel;
            RevealRadius = revealRadius;
        }

        /// <summary>One cell per map pixel, index <c>cy · size + cx</c>.</summary>
        public bool[] Explored { get; }

        /// <summary>Map texture size in cells. 2048 unmodded — read it off the live instance, never assume it.</summary>
        public int Size { get; }

        /// <summary>World metres per cell. 12 unmodded.</summary>
        public double MetresPerPixel { get; }

        /// <summary>The game's reveal radius around the player, in metres.</summary>
        public double RevealRadius { get; }

        /// <summary>
        /// False for anything a broken or half-initialised source could hand
        /// over. Checked before every use, because the array is indexed with
        /// <see cref="Size"/> and a mismatch would throw on the main thread.
        /// </summary>
        public bool IsValid =>
            Explored != null
            && Size > 0
            && Size <= FogTiles.MaxMapSize
            && Explored.Length == Size * Size
            && MetresPerPixel > 0 && !double.IsNaN(MetresPerPixel) && !double.IsInfinity(MetresPerPixel)
            && RevealRadius >= 0 && !double.IsNaN(RevealRadius) && !double.IsInfinity(RevealRadius);
    }

    /// <summary>Where the fog comes from. Implemented by the plugin over <c>Minimap</c>.</summary>
    public interface IFogSource
    {
        /// <summary>
        /// The live explored map, or false when there is none: loading, the main
        /// menu, a game build whose fields have moved.
        /// </summary>
        bool TryGetFogMap(out FogMap map);
    }

    /// <summary>
    /// Decides which fog tiles go out, and when (§3.7). Owns no socket and no
    /// clock: the session hands it time and the map, and asks it for the next
    /// frame whenever the lane is free.
    /// <para>
    /// Tiles waiting to go are a <b>set of tile keys</b>, not a queue of
    /// frames. A tile is encoded when it is taken, so it carries the content
    /// current at send time; a tile marked twice is sent once; and the set is
    /// bounded by the tile count, so unlike the reliable lane it has nothing to
    /// drop.
    /// </para>
    /// <para>
    /// Which tiles changed is learned from the player's position rather than
    /// from a patch on the game's explore step: the game reveals a circle of
    /// <see cref="FogMap.RevealRadius"/> around the player, so every position
    /// marks the tiles that circle overlaps. That needs no Harmony patch on a
    /// method whose signature nobody has confirmed, and over-marking is free,
    /// because a tile that encodes the same as what this connection last sent
    /// is skipped. A slow sweep covers anything that explores away from the
    /// player.
    /// </para>
    /// </summary>
    public sealed class FogSync
    {
        // How a waiting tile is to be treated. Ordered: marking a tile again
        // keeps the strongest reason it has.
        private const byte NotPending = 0;

        // Sweep: send only if the map was sent this tile before and it has
        // changed since. A tile the map has never had from this connection is
        // left for its snapshot, or the sweep would become a snapshot nobody
        // asked for.
        private const byte IfKnownAndChanged = 1;

        // The player was near it: send if it differs from what was last sent,
        // including never having been sent.
        private const byte IfChanged = 2;

        // A snapshot: send whatever it holds.
        private const byte Always = 3;

        private readonly SessionOptions _options;
        private readonly FogTileEncoder _encoder = new FogTileEncoder();

        // Geometry the arrays below are sized for.
        private int _size;
        private double _metresPerPixel;
        private double _revealRadius;
        private int _tilesPerSide;
        private bool _hasGeometry;

        private byte[] _dirty = Array.Empty<byte>();
        private byte[] _pending = Array.Empty<byte>();
        private int _pendingCount;

        // What this connection last sent each tile as. Null: never sent. Empty:
        // known to be empty, which a map already assumes of any tile it has
        // not received.
        private string?[] _sentData = Array.Empty<string?>();
        private string?[] _sentEncoding = Array.Empty<string?>();

        private bool _hasPosition;
        private double _lastX;
        private double _lastZ;

        private bool _enabled;
        private bool _timersStarted;
        private TimeSpan _lastFlushAt;
        private TimeSpan _lastSweepAt;
        private TimeSpan _lastFrameAt;
        private bool _anyFrameSent;
        private bool _snapshotSent;
        private TimeSpan _lastSnapshotAt;
        private bool _snapshotOwed;

        // The tile handed out by the last NextFrame, so a refusal can put it back.
        private int _takenIndex = -1;
        private byte _takenMode;
        private string? _takenPreviousData;
        private string? _takenPreviousEncoding;

        public FogSync(SessionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Whether fog goes out at all. Turning it off stops sending at once —
        /// pending tiles and owed snapshots are discarded, because a player who
        /// switches sharing off means now, not after the queue drains.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value)
                {
                    ClearPending();
                    _snapshotOwed = false;
                }
                _timersStarted = false;
            }
        }

        /// <summary>Tiles waiting to be examined. For tests and the panel.</summary>
        public int PendingCount => _pendingCount;

        /// <summary>Fog frames handed to the transport since the session started.</summary>
        public int FramesSent { get; private set; }

        public bool SnapshotOwed => _snapshotOwed;

        /// <summary>A new session: forget everything, including where the player was.</summary>
        public void Reset()
        {
            _hasGeometry = false;
            _size = 0;
            _tilesPerSide = 0;
            _dirty = Array.Empty<byte>();
            _pending = Array.Empty<byte>();
            _pendingCount = 0;
            _sentData = Array.Empty<string?>();
            _sentEncoding = Array.Empty<string?>();
            _hasPosition = false;
            _takenIndex = -1;
            FramesSent = 0;
            OnConnectionChanged();
        }

        /// <summary>
        /// A connection began or ended. What was sent on the old one says
        /// nothing about what a map on the new one holds, and the map asks for a
        /// snapshot when it sees the new <c>playerId</c> (§3.7), so the record
        /// and the queue both go. Dirty marks stay: those are about the game,
        /// not the connection.
        /// </summary>
        public void OnConnectionChanged()
        {
            ClearPending();
            for (var i = 0; i < _sentData.Length; i++)
            {
                _sentData[i] = null;
                _sentEncoding[i] = null;
            }
            _snapshotSent = false;
            _snapshotOwed = false;
            _anyFrameSent = false;
            _timersStarted = false;
            _takenIndex = -1;
        }

        /// <summary>
        /// The player is here. Marks every tile the reveal circle overlaps, and
        /// is remembered so a snapshot can start nearest the player — the part
        /// of the map a stream is looking at.
        /// </summary>
        public void NotePosition(double x, double z)
        {
            _hasPosition = true;
            _lastX = x;
            _lastZ = z;
            if (_enabled && _hasGeometry) MarkAround(x, z);
        }

        /// <summary>Marks every tile for a re-check against what the map already holds.</summary>
        public void MarkAllDirty()
        {
            for (var i = 0; i < _dirty.Length; i++)
            {
                if (_dirty[i] < IfKnownAndChanged) _dirty[i] = IfKnownAndChanged;
            }
        }

        /// <summary>
        /// A map asked for a snapshot. One per <see cref="SessionOptions.FogSnapshotCooldown"/>;
        /// a request inside the window is owed, not dropped (§12.5's rule), and
        /// so is one that arrives before the minimap exists.
        /// </summary>
        public void RequestSnapshot(TimeSpan now)
        {
            if (!_enabled) return;
            if (_hasGeometry && CooldownOver(now))
            {
                StartSnapshot(now);
                return;
            }
            _snapshotOwed = true;
        }

        /// <summary>Timers: the live-update flush, the sweep, and an owed snapshot.</summary>
        public void Tick(TimeSpan now, in FogMap map)
        {
            if (!_enabled || !map.IsValid) return;

            AdoptGeometry(map);

            if (!_timersStarted)
            {
                _timersStarted = true;
                _lastFlushAt = now;
                _lastSweepAt = now;
            }

            if (now - _lastSweepAt >= _options.FogSweepInterval)
            {
                _lastSweepAt = now;
                MarkAllDirty();
            }

            if (now - _lastFlushAt >= _options.FogInterval)
            {
                _lastFlushAt = now;
                Flush();
            }

            if (_snapshotOwed && CooldownOver(now)) StartSnapshot(now);
        }

        /// <summary>
        /// The next fog frame to send, or null when there is none or the pacing
        /// gap has not passed. Call only when the reliable lane and the position
        /// slot are both empty: fog is the lowest-priority traffic there is.
        /// </summary>
        public string? NextFrame(in FogMap map, TimeSpan now)
        {
            _takenIndex = -1;
            if (!_enabled || _pendingCount == 0 || !map.IsValid) return null;
            if (_anyFrameSent && now - _lastFrameAt < _options.FogFrameGap) return null;

            AdoptGeometry(map);

            for (var budget = _options.FogTilesExaminedPerTick; budget > 0; budget--)
            {
                var index = NearestPending();
                if (index < 0) return null;

                var mode = _pending[index];
                _pending[index] = NotPending;
                _pendingCount--;

                var previousData = _sentData[index];
                var previousEncoding = _sentEncoding[index];

                // Before encoding: the sweep marks every tile, and most of them
                // were never sent on this connection.
                if (mode == IfKnownAndChanged && previousData == null) continue;

                var tx = index % _tilesPerSide;
                var tz = index / _tilesPerSide;

                if (!_encoder.TryEncode(map.Explored, map.Size, tx, tz, out var encoding, out var data))
                {
                    // Empty, which is what a map assumes of a tile it never got.
                    _sentData[index] = string.Empty;
                    _sentEncoding[index] = null;
                    continue;
                }

                if (mode != Always
                    && previousData != null
                    && string.Equals(previousData, data, StringComparison.Ordinal)
                    && string.Equals(previousEncoding, encoding, StringComparison.Ordinal))
                {
                    continue;
                }

                var frame = FrameCodec.WriteFog(map.Size, map.MetresPerPixel, tx, tz, encoding, data);

                _sentData[index] = data;
                _sentEncoding[index] = encoding;
                _takenIndex = index;
                _takenMode = mode;
                _takenPreviousData = previousData;
                _takenPreviousEncoding = previousEncoding;
                _lastFrameAt = now;
                _anyFrameSent = true;
                FramesSent++;
                return frame;
            }

            return null;
        }

        /// <summary>
        /// The transport refused the frame <see cref="NextFrame"/> just returned.
        /// The tile goes back as it was, to be re-encoded on the next attempt.
        /// </summary>
        public void Refused()
        {
            var index = _takenIndex;
            if (index < 0 || index >= _pending.Length) return;
            _takenIndex = -1;

            _sentData[index] = _takenPreviousData;
            _sentEncoding[index] = _takenPreviousEncoding;
            FramesSent--;
            Mark(_pending, index, _takenMode, countPending: true);
        }

        // ------------------------------------------------------------ internals

        private bool CooldownOver(TimeSpan now)
            => !_snapshotSent || now - _lastSnapshotAt >= _options.FogSnapshotCooldown;

        private void StartSnapshot(TimeSpan now)
        {
            _snapshotOwed = false;
            _snapshotSent = true;
            _lastSnapshotAt = now;
            for (var i = 0; i < _pending.Length; i++) Mark(_pending, i, Always, countPending: true);
        }

        /// <summary>Dirty tiles join the pending set, each keeping its strongest reason.</summary>
        private void Flush()
        {
            for (var i = 0; i < _dirty.Length; i++)
            {
                if (_dirty[i] == NotPending) continue;
                Mark(_pending, i, _dirty[i], countPending: true);
                _dirty[i] = NotPending;
            }
        }

        private void Mark(byte[] set, int index, byte mode, bool countPending)
        {
            var existing = set[index];
            if (existing >= mode) return;
            if (countPending && existing == NotPending) _pendingCount++;
            set[index] = mode;
        }

        private void ClearPending()
        {
            if (_pendingCount > 0) Array.Clear(_pending, 0, _pending.Length);
            _pendingCount = 0;
        }

        /// <summary>
        /// Sizes everything for this map. A different size or scale means a
        /// different world (or a world mod's settings), so nothing recorded
        /// against the old grid carries over.
        /// </summary>
        private void AdoptGeometry(in FogMap map)
        {
            _revealRadius = map.RevealRadius;
            if (_hasGeometry && map.Size == _size && map.MetresPerPixel.Equals(_metresPerPixel)) return;

            _hasGeometry = true;
            _size = map.Size;
            _metresPerPixel = map.MetresPerPixel;
            _tilesPerSide = FogTiles.TilesPerSide(_size);

            var count = _tilesPerSide * _tilesPerSide;
            _dirty = new byte[count];
            _pending = new byte[count];
            _pendingCount = 0;
            _sentData = new string?[count];
            _sentEncoding = new string?[count];
            _takenIndex = -1;

            if (_hasPosition) MarkAround(_lastX, _lastZ);
        }

        private void MarkAround(double x, double z)
        {
            // Cell coordinates of the circle's bounding box. Rounding, not
            // flooring, is how the game maps world to cell; one cell of slack on
            // each side makes the difference irrelevant.
            var half = _size / 2.0;
            var reach = (_revealRadius / _metresPerPixel) + 1;
            var cx = (x / _metresPerPixel) + half;
            var cy = (z / _metresPerPixel) + half;

            var minTx = ClampTile(cx - reach);
            var maxTx = ClampTile(cx + reach);
            var minTz = ClampTile(cy - reach);
            var maxTz = ClampTile(cy + reach);

            for (var tz = minTz; tz <= maxTz; tz++)
            {
                for (var tx = minTx; tx <= maxTx; tx++)
                {
                    Mark(_dirty, (tz * _tilesPerSide) + tx, IfChanged, countPending: false);
                }
            }
        }

        private int ClampTile(double cell)
        {
            if (double.IsNaN(cell)) return 0;
            var tile = (int)Math.Floor(Math.Max(0, Math.Min(_size - 1, cell)) / FogTiles.TileSize);
            return Math.Max(0, Math.Min(_tilesPerSide - 1, tile));
        }

        /// <summary>
        /// The pending tile closest to the player, or to the middle of the map
        /// when there is no position yet. A scan rather than a heap: 256 tiles
        /// on an unmodded world, and the player moves between calls, so a heap
        /// would need rebuilding anyway.
        /// </summary>
        private int NearestPending()
        {
            if (_pendingCount == 0) return -1;

            double px, pz;
            if (_hasPosition)
            {
                px = (_lastX / _metresPerPixel) + (_size / 2.0);
                pz = (_lastZ / _metresPerPixel) + (_size / 2.0);
            }
            else
            {
                px = pz = _size / 2.0;
            }

            const double halfTile = FogTiles.TileSize / 2.0;
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < _pending.Length; i++)
            {
                if (_pending[i] == NotPending) continue;
                var dx = ((i % _tilesPerSide) * FogTiles.TileSize) + halfTile - px;
                var dz = ((i / _tilesPerSide) * FogTiles.TileSize) + halfTile - pz;
                var distance = (dx * dx) + (dz * dz);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }
    }
}
