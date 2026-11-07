using System;
using System.Collections;

namespace ValheimRelay.Core.Protocol
{
    /// <summary>
    /// The game's explored map, read in place, whichever container the game
    /// keeps it in (§3.7).
    /// <para>
    /// Valheim 0.221.12 holds <c>Minimap.m_explored</c> as a <c>bool[]</c>;
    /// 1.0.7 holds it as a <see cref="BitArray"/>
    /// (<c>new BitArray(m_textureSize * m_textureSize, false)</c> in
    /// <c>Minimap.Awake</c>). Both are indexed <c>y · size + x</c>, so only the
    /// container changed, and this is the one place that knows it did.
    /// </para>
    /// <para>
    /// A view, never a copy. The fog path reads this every frame on the main
    /// thread, and copying four million cells into a fresh <c>bool[]</c> per
    /// frame would be exactly the hitch <see cref="FogTileEncoder"/> exists to
    /// avoid. A struct with an implicit conversion from <c>bool[]</c>, so every
    /// caller that already has an array passes it unchanged.
    /// </para>
    /// </summary>
    public readonly struct ExploredCells
    {
        private readonly bool[]? _array;
        private readonly BitArray? _bits;

        public ExploredCells(bool[]? array)
        {
            _array = array;
            _bits = null;
        }

        public ExploredCells(BitArray? bits)
        {
            _array = null;
            _bits = bits;
        }

        public static implicit operator ExploredCells(bool[]? array) => new ExploredCells(array);

        public static implicit operator ExploredCells(BitArray? bits) => new ExploredCells(bits);

        /// <summary>
        /// Wraps the value of a game field, if it is a container this knows.
        /// False for anything else, so a build that changes the type again
        /// costs fog of war and a log line rather than an exception.
        /// </summary>
        public static bool TryWrap(object? value, out ExploredCells cells)
        {
            switch (value)
            {
                case bool[] array:
                    cells = new ExploredCells(array);
                    return true;
                case BitArray bits:
                    cells = new ExploredCells(bits);
                    return true;
                default:
                    cells = default;
                    return false;
            }
        }

        /// <summary>True when there is no container at all.</summary>
        public bool IsNull => _array == null && _bits == null;

        /// <summary>Cells held: <c>size²</c> for a well-formed map.</summary>
        public int Length => _array != null ? _array.Length : _bits != null ? _bits.Length : 0;

        /// <summary>Cell <paramref name="index"/>, explored or not. Throws out of range, as both containers do.</summary>
        public bool this[int index]
        {
            get
            {
                if (_array != null) return _array[index];
                if (_bits != null) return _bits[index];
                throw new InvalidOperationException("no explored map");
            }
        }
    }
}
