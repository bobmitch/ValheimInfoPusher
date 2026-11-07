using System;

namespace ValheimRelay.Core.Protocol
{
    /// <summary>The values of a <c>fog</c> frame's <c>enc</c> field (§3.7).</summary>
    public static class FogEncodings
    {
        /// <summary>Alternating run lengths as LEB128 varints, starting with an unexplored run.</summary>
        public const string Rle = "rle";

        /// <summary>One bit per cell, least significant bit first.</summary>
        public const string Bits = "bits";
    }

    /// <summary>
    /// The tile grid of PLAN.md §3.7, and the two encodings a tile travels in.
    /// <para>
    /// A tile is a sub-rectangle of the game's own explored array, in the same
    /// index order: cell <c>(cx, cy)</c> is <c>explored[cy · size + cx]</c>, and
    /// tile cell <c>i</c> is array cell <c>(tx·n + i mod n, tz·n + ⌊i / n⌋)</c>.
    /// Nothing here knows which way is north, and nothing needs to.
    /// </para>
    /// </summary>
    public static class FogTiles
    {
        /// <summary>
        /// Tile edge in cells. The largest power of two whose uncompressed form
        /// still fits a frame: 128² bits is 2048 bytes, 2732 base64'd. At 256 the
        /// <c>bits</c> fallback would be 10.9 KB, over the relay's 8192-byte cap.
        /// </summary>
        public const int TileSize = 128;

        public const int CellsPerTile = TileSize * TileSize;

        /// <summary>
        /// A sanity bound on the map size, not a game limit. The unmodded map is
        /// 2048; world-enlarging mods go higher. Past this the array is 256 MB of
        /// bools, which no real install has, so it is read as a broken source.
        /// </summary>
        public const int MaxMapSize = 16384;

        private const int BitsBytes = CellsPerTile / 8;

        public static int TilesPerSide(int mapSize) => (mapSize + TileSize - 1) / TileSize;

        /// <summary>
        /// Decodes a tile's <c>d</c> back into <see cref="CellsPerTile"/> cells.
        /// Returns false for an unknown encoding or anything malformed, including
        /// RLE runs that do not sum to exactly <see cref="CellsPerTile"/>, which
        /// §3.7 says the receiver must drop. The mod never receives tiles; this is
        /// the reference decoder the encoder is tested against, and the one a map
        /// author can port.
        /// </summary>
        public static bool TryDecode(string? encoding, string? data, out bool[] cells)
        {
            cells = new bool[CellsPerTile];
            if (data == null) return false;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                return false;
            }

            if (string.Equals(encoding, FogEncodings.Bits, StringComparison.Ordinal))
            {
                if (bytes.Length != BitsBytes) return false;
                for (var i = 0; i < CellsPerTile; i++)
                {
                    cells[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
                }
                return true;
            }

            if (!string.Equals(encoding, FogEncodings.Rle, StringComparison.Ordinal)) return false;

            var position = 0;
            var cell = 0;
            var explored = false;
            while (position < bytes.Length)
            {
                if (!TryReadVarint(bytes, ref position, out var run)) return false;
                if (run > CellsPerTile - cell) return false;
                if (explored)
                {
                    for (var i = 0; i < run; i++) cells[cell + i] = true;
                }
                cell += (int)run;
                explored = !explored;
            }

            return cell == CellsPerTile;
        }

        private static bool TryReadVarint(byte[] bytes, ref int position, out long value)
        {
            value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                if (position >= bytes.Length) return false;
                var b = bytes[position++];
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Encodes tiles out of the live explored array. An instance rather than a
    /// static so its scratch buffers are reused: this runs on the main thread up
    /// to ten times a second during a snapshot, and allocating 50 KB a time
    /// would be garbage the game pays for in frame hitches.
    /// </summary>
    public sealed class FogTileEncoder
    {
        // Worst case for RLE is a run per cell, each one byte; the first run can
        // be a zero, and no run exceeds 16384, which is three varint bytes.
        private readonly byte[] _rle = new byte[FogTiles.CellsPerTile + 8];
        private readonly byte[] _bits = new byte[FogTiles.CellsPerTile / 8];

        /// <summary>
        /// Encodes tile <c>(tx, tz)</c> in whichever of the two encodings is
        /// shorter. Returns false when no cell of the tile is explored: an empty
        /// tile is never sent, because a map treats a tile it has never received
        /// as unexplored already.
        /// <para>
        /// Cells beyond the edge of the map — the last row and column of tiles
        /// when the size is not a multiple of <see cref="FogTiles.TileSize"/> —
        /// are unexplored.
        /// </para>
        /// </summary>
        public bool TryEncode(bool[] explored, int mapSize, int tx, int tz, out string encoding, out string data)
        {
            if (explored == null) throw new ArgumentNullException(nameof(explored));

            encoding = string.Empty;
            data = string.Empty;

            const int n = FogTiles.TileSize;
            Array.Clear(_bits, 0, _bits.Length);

            var rleLength = 0;
            var current = false;
            var run = 0;
            var exploredCount = 0;
            var i = 0;

            for (var row = 0; row < n; row++)
            {
                var cy = (tz * n) + row;
                var rowInMap = cy >= 0 && cy < mapSize;
                var rowStart = cy * mapSize;

                for (var col = 0; col < n; col++, i++)
                {
                    var cx = (tx * n) + col;
                    var value = rowInMap && cx >= 0 && cx < mapSize && explored[rowStart + cx];

                    if (value)
                    {
                        exploredCount++;
                        _bits[i >> 3] |= (byte)(1 << (i & 7));
                    }

                    if (value == current)
                    {
                        run++;
                    }
                    else
                    {
                        rleLength = WriteVarint(_rle, rleLength, run);
                        current = value;
                        run = 1;
                    }
                }
            }

            if (exploredCount == 0) return false;

            rleLength = WriteVarint(_rle, rleLength, run);

            // Ties go to RLE, which is the cheaper one to decode.
            if (rleLength <= _bits.Length)
            {
                encoding = FogEncodings.Rle;
                data = Convert.ToBase64String(_rle, 0, rleLength);
            }
            else
            {
                encoding = FogEncodings.Bits;
                data = Convert.ToBase64String(_bits, 0, _bits.Length);
            }

            return true;
        }

        private static int WriteVarint(byte[] buffer, int position, int value)
        {
            var v = (uint)value;
            while (v >= 0x80)
            {
                buffer[position++] = (byte)(v | 0x80);
                v >>= 7;
            }
            buffer[position++] = (byte)v;
            return position;
        }
    }
}
