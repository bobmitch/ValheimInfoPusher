namespace ValheimRelay.Core.Qr
{
    /// <summary>
    /// How one version's codewords are split into blocks. Error correction is
    /// computed per block and the blocks are then interleaved, so a scratch
    /// across the symbol damages a few codewords in every block rather than
    /// destroying one block outright.
    /// </summary>
    internal readonly struct BlockPlan
    {
        internal BlockPlan(int errorCodewords, int group1Blocks, int group1Data, int group2Blocks, int group2Data)
        {
            ErrorCodewords = errorCodewords;
            Group1Blocks = group1Blocks;
            Group1Data = group1Data;
            Group2Blocks = group2Blocks;
            Group2Data = group2Data;
        }

        /// <summary>Error correction codewords per block — the same for every block.</summary>
        internal int ErrorCodewords { get; }

        internal int Group1Blocks { get; }
        internal int Group1Data { get; }

        /// <summary>Zero for versions whose blocks are all the same size.</summary>
        internal int Group2Blocks { get; }
        internal int Group2Data { get; }

        internal int Blocks => Group1Blocks + Group2Blocks;

        internal int DataCodewords => Group1Blocks * Group1Data + Group2Blocks * Group2Data;
    }

    /// <summary>
    /// The per-version tables of ISO/IEC 18004, for the one configuration this
    /// encoder produces: error correction level M, versions 1 to 9.
    /// <para>
    /// The ceiling is version 9 on purpose. Byte mode's character count
    /// indicator is a flat 8 bits below version 10 and becomes 16 bits at and
    /// above it, so stopping here keeps the header a constant — and version 9
    /// already carries 180 bytes of payload, several times the longest map link
    /// anyone is going to configure.
    /// </para>
    /// </summary>
    internal static class QrVersions
    {
        internal const int MinVersion = 1;
        internal const int MaxVersion = 9;

        /// <summary>Error correction level M, as the two bits the format information carries.</summary>
        internal const int EccLevelBits = 0b00;

        private static readonly BlockPlan[] Plans =
        {
            new BlockPlan(10, 1, 16, 0, 0),  // 1
            new BlockPlan(16, 1, 28, 0, 0),  // 2
            new BlockPlan(26, 1, 44, 0, 0),  // 3
            new BlockPlan(18, 2, 32, 0, 0),  // 4
            new BlockPlan(24, 2, 43, 0, 0),  // 5
            new BlockPlan(16, 4, 27, 0, 0),  // 6
            new BlockPlan(18, 4, 31, 0, 0),  // 7
            new BlockPlan(22, 2, 38, 2, 39), // 8
            new BlockPlan(22, 3, 36, 2, 37), // 9
        };

        /// <summary>
        /// Alignment pattern centre coordinates. A pattern sits at every pairing
        /// of these, minus the three corners already occupied by finder
        /// patterns. Version 1 has none.
        /// </summary>
        private static readonly int[][] AlignmentCentres =
        {
            new int[0],                  // 1
            new[] { 6, 18 },             // 2
            new[] { 6, 22 },             // 3
            new[] { 6, 26 },             // 4
            new[] { 6, 30 },             // 5
            new[] { 6, 34 },             // 6
            new[] { 6, 22, 38 },         // 7
            new[] { 6, 24, 42 },         // 8
            new[] { 6, 26, 46 },         // 9
        };

        internal static BlockPlan Plan(int version) => Plans[version - 1];

        internal static int[] Alignment(int version) => AlignmentCentres[version - 1];

        /// <summary>The symbol's width in modules.</summary>
        internal static int Size(int version) => 4 * version + 17;

        /// <summary>
        /// The smallest version that holds <paramref name="byteCount"/> bytes in
        /// byte mode, or 0 when none of them does. The header is a 4-bit mode
        /// indicator plus an 8-bit length.
        /// </summary>
        internal static int SmallestFor(int byteCount)
        {
            var neededBits = 4 + 8 + (8 * byteCount);

            for (var version = MinVersion; version <= MaxVersion; version++)
            {
                if (Plan(version).DataCodewords * 8 >= neededBits) return version;
            }

            return 0;
        }
    }
}
