using System;

namespace ValheimRelay.Core.Qr
{
    /// <summary>
    /// Draws a QR symbol: the fixed patterns a scanner locks onto, the data
    /// woven around them, and the mask that keeps the result from looking like
    /// one of those patterns by accident.
    /// </summary>
    internal sealed class QrMatrix
    {
        /// <summary>
        /// Modules a scanner needs where they are — finders, timing, alignment,
        /// format and version information. Data flows around them, and the mask
        /// leaves them alone.
        /// </summary>
        private readonly bool[] _function;

        private readonly bool[] _modules;

        internal QrMatrix(int version)
        {
            Version = version;
            Size = QrVersions.Size(version);

            _modules = new bool[Size * Size];
            _function = new bool[Size * Size];

            DrawFinder(0, 0);
            DrawFinder(Size - 7, 0);
            DrawFinder(0, Size - 7);
            DrawTiming();
            DrawAlignmentPatterns();

            // Drawn once with a placeholder purely to reserve the modules, so
            // the data placement below skips them. The real bits go in once a
            // mask has been chosen, which is after the data is already placed.
            DrawFormat(0);
            DrawVersion();
        }

        internal int Version { get; }

        internal int Size { get; }

        // ---------------------------------------------------------- patterns

        private void Set(int x, int y, bool dark, bool function)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size) return;

            _modules[(y * Size) + x] = dark;
            if (function) _function[(y * Size) + x] = true;
        }

        private void DrawFinder(int left, int top)
        {
            // One module wider than the pattern on every side: that border is
            // the separator, and it is light.
            for (var dy = -1; dy <= 7; dy++)
            {
                for (var dx = -1; dx <= 7; dx++)
                {
                    var inside = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6;
                    var ring = dx == 0 || dx == 6 || dy == 0 || dy == 6;
                    var core = dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4;

                    Set(left + dx, top + dy, inside && (ring || core), function: true);
                }
            }
        }

        private void DrawTiming()
        {
            for (var i = 8; i < Size - 8; i++)
            {
                var dark = i % 2 == 0;
                Set(i, 6, dark, function: true);
                Set(6, i, dark, function: true);
            }
        }

        private void DrawAlignmentPatterns()
        {
            var centres = QrVersions.Alignment(Version);
            if (centres.Length == 0) return;

            var last = centres.Length - 1;

            for (var row = 0; row <= last; row++)
            {
                for (var column = 0; column <= last; column++)
                {
                    // The three corners are already finder patterns.
                    if (row == 0 && column == 0) continue;
                    if (row == 0 && column == last) continue;
                    if (row == last && column == 0) continue;

                    DrawAlignment(centres[column], centres[row]);
                }
            }
        }

        private void DrawAlignment(int cx, int cy)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    // Dark centre, light ring, dark outer ring.
                    var dark = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                    Set(cx + dx, cy + dy, dark, function: true);
                }
            }
        }

        /// <summary>
        /// The 15 format bits, twice: once around the top-left finder and once
        /// split between the other two. Two copies because losing the format
        /// information loses the whole symbol.
        /// </summary>
        private void DrawFormat(int bits)
        {
            for (var i = 0; i < 15; i++)
            {
                var dark = ((bits >> i) & 1) != 0;

                // First copy, wrapped around the top-left finder: down
                // column 8, then left along row 8. The jumps at row 6 and
                // column 6 are the timing patterns, which is why neither leg
                // is a straight run.
                if (i <= 5) Set(8, i, dark, function: true);
                else if (i == 6) Set(8, 7, dark, function: true);
                else if (i == 7) Set(8, 8, dark, function: true);
                else if (i == 8) Set(7, 8, dark, function: true);
                else Set(14 - i, 8, dark, function: true);

                // Second copy, split between the other two finders: along row
                // 8 from the right edge, then down column 8 from the bottom.
                if (i <= 7) Set(Size - 1 - i, 8, dark, function: true);
                else Set(8, Size - 15 + i, dark, function: true);
            }

            // The one module that is always dark, whatever the format says.
            Set(8, Size - 8, true, function: true);
        }

        /// <summary>
        /// The 18 version bits, in two blocks. Only from version 7: below that a
        /// scanner works the version out from the symbol's size alone.
        /// </summary>
        private void DrawVersion()
        {
            if (Version < 7) return;

            var bits = VersionBits(Version);

            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) != 0;
                var near = i / 3;
                var far = Size - 11 + (i % 3);

                Set(near, far, dark, function: true);
                Set(far, near, dark, function: true);
            }
        }

        // -------------------------------------------------------------- data

        /// <summary>
        /// Threads the codewords through every module the patterns left free,
        /// two columns at a time, alternating up and down and stepping over the
        /// vertical timing pattern.
        /// </summary>
        internal void PlaceData(byte[] codewords)
        {
            var bit = 0;
            var totalBits = codewords.Length * 8;
            var upward = true;

            for (var right = Size - 1; right >= 1; right -= 2)
            {
                // Column 6 is timing, so the pair either side of it is shifted.
                if (right == 6) right = 5;

                for (var step = 0; step < Size; step++)
                {
                    var y = upward ? Size - 1 - step : step;

                    for (var column = 0; column < 2; column++)
                    {
                        var x = right - column;
                        if (_function[(y * Size) + x]) continue;

                        var dark = false;
                        if (bit < totalBits)
                        {
                            dark = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                            bit++;
                        }

                        // Anything past the last codeword stays light; those are
                        // the remainder bits, which carry nothing.
                        _modules[(y * Size) + x] = dark;
                    }
                }

                upward = !upward;
            }
        }

        // ------------------------------------------------------------- masks

        /// <summary>
        /// Applies whichever of the eight masks scores best, writes the matching
        /// format information, and returns the finished modules.
        /// </summary>
        internal bool[] Finish(int forcedMask, out int chosenMask)
        {
            var best = new bool[_modules.Length];
            var bestPenalty = int.MaxValue;
            chosenMask = 0;

            var candidate = new bool[_modules.Length];

            for (var mask = 0; mask < 8; mask++)
            {
                if (forcedMask >= 0 && mask != forcedMask) continue;

                Array.Copy(_modules, candidate, _modules.Length);
                ApplyMask(candidate, mask);
                WriteFormat(candidate, mask);

                var penalty = Penalty(candidate);
                if (penalty >= bestPenalty) continue;

                bestPenalty = penalty;
                chosenMask = mask;
                Array.Copy(candidate, best, candidate.Length);
            }

            return best;
        }

        private void ApplyMask(bool[] modules, int mask)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var index = (y * Size) + x;
                    if (_function[index]) continue;
                    if (Masked(mask, x, y)) modules[index] = !modules[index];
                }
            }
        }

        private static bool Masked(int mask, int x, int y) => mask switch
        {
            0 => (x + y) % 2 == 0,
            1 => y % 2 == 0,
            2 => x % 3 == 0,
            3 => (x + y) % 3 == 0,
            4 => (((y / 2) + (x / 3)) % 2) == 0,
            5 => ((x * y) % 2) + ((x * y) % 3) == 0,
            6 => ((((x * y) % 2) + ((x * y) % 3)) % 2) == 0,
            7 => ((((x + y) % 2) + ((x * y) % 3)) % 2) == 0,
            _ => false
        };

        private void WriteFormat(bool[] modules, int mask)
        {
            var bits = FormatBits(QrVersions.EccLevelBits, mask);

            for (var i = 0; i < 15; i++)
            {
                var dark = ((bits >> i) & 1) != 0;

                if (i <= 5) modules[(i * Size) + 8] = dark;
                else if (i == 6) modules[(7 * Size) + 8] = dark;
                else if (i == 7) modules[(8 * Size) + 8] = dark;
                else if (i == 8) modules[(8 * Size) + 7] = dark;
                else modules[(8 * Size) + 14 - i] = dark;

                if (i <= 7) modules[(8 * Size) + Size - 1 - i] = dark;
                else modules[((Size - 15 + i) * Size) + 8] = dark;
            }
        }

        /// <summary>
        /// The five format bits — error correction level, then mask — extended
        /// by a BCH(15, 5) code and masked with 0x5412, so that an all-zero
        /// format is not a valid one.
        /// </summary>
        internal static int FormatBits(int eccBits, int mask)
        {
            var data = (eccBits << 3) | mask;

            var remainder = data;
            for (var i = 0; i < 10; i++)
            {
                remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            }

            return (((data << 10) | remainder) ^ 0x5412) & 0x7FFF;
        }

        /// <summary>The six version bits extended by a BCH(18, 6) code.</summary>
        internal static int VersionBits(int version)
        {
            var remainder = version;
            for (var i = 0; i < 12; i++)
            {
                remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
            }

            return (version << 12) | remainder;
        }

        // ---------------------------------------------------------- penalties

        /// <summary>
        /// The four penalty rules of the specification. They exist to steer the
        /// mask away from symbols that are hard to read: long same-colour runs,
        /// solid blocks, anything resembling a finder pattern, and an overall
        /// balance far from half dark.
        /// </summary>
        private int Penalty(bool[] modules)
        {
            var penalty = 0;
            var dark = 0;

            // Rules 1 and 3, along both axes.
            for (var line = 0; line < Size; line++)
            {
                penalty += LinePenalty(modules, line, horizontal: true);
                penalty += LinePenalty(modules, line, horizontal: false);
            }

            // Rule 2: every 2x2 block of one colour.
            for (var y = 0; y < Size - 1; y++)
            {
                for (var x = 0; x < Size - 1; x++)
                {
                    var first = modules[(y * Size) + x];
                    if (first == modules[(y * Size) + x + 1]
                        && first == modules[((y + 1) * Size) + x]
                        && first == modules[((y + 1) * Size) + x + 1])
                    {
                        penalty += 3;
                    }
                }
            }

            // Rule 4: how far the balance of dark to light strays from even.
            for (var i = 0; i < modules.Length; i++)
            {
                if (modules[i]) dark++;
            }

            var total = modules.Length;
            penalty += Math.Abs((dark * 2) - total) * 10 / total * 10;

            return penalty;
        }

        private int LinePenalty(bool[] modules, int line, bool horizontal)
        {
            var penalty = 0;
            var runColour = false;
            var runLength = 0;

            // The eleven-module window rule 3 looks for: a finder-like
            // 1:1:3:1:1 run with four light modules on one side of it.
            var window = 0;

            for (var i = 0; i < Size; i++)
            {
                var value = horizontal
                    ? modules[(line * Size) + i]
                    : modules[(i * Size) + line];

                if (i > 0 && value == runColour)
                {
                    runLength++;
                    if (runLength == 5) penalty += 3;
                    else if (runLength > 5) penalty++;
                }
                else
                {
                    runColour = value;
                    runLength = 1;
                }

                window = ((window << 1) | (value ? 1 : 0)) & 0x7FF;
                if (i >= 10 && (window == 0b10111010000 || window == 0b00001011101))
                {
                    penalty += 40;
                }
            }

            return penalty;
        }
    }
}
