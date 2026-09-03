using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using ValheimRelay.Core.Qr;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    /// <summary>
    /// Shared helpers for the QR suite.
    /// <para>
    /// The expected symbols below came from an outside implementation — the
    /// Python <c>qrcode</c> package — and are checked in rather than generated,
    /// so the tests need nothing installed. §12.14 is why they are literals in
    /// this file rather than fixture files: a fixture that never got committed
    /// once went unnoticed by CI, and a constant cannot go missing.
    /// </para>
    /// <para>
    /// Every expectation here forces the mask rather than letting the encoder
    /// choose one. That keeps the comparison honest: which mask is best is a
    /// scoring judgement that implementations genuinely differ on (see
    /// <see cref="QrMaskChoiceTests"/>), and folding it into these goldens
    /// would mean testing one opinion against another rather than testing the
    /// encoding. Reproduce a row with:
    /// </para>
    /// <code>
    /// pip install qrcode
    /// python -c "import qrcode; from qrcode.util import QRData, MODE_8BIT_BYTE; \
    ///   from qrcode.constants import ERROR_CORRECT_M; \
    ///   q = qrcode.QRCode(version=V, error_correction=ERROR_CORRECT_M, \
    ///                     box_size=1, border=0, mask_pattern=M); \
    ///   q.add_data(QRData(TEXT.encode(), mode=MODE_8BIT_BYTE)); q.make(fit=False); \
    ///   print(chr(10).join(''.join('#' if c else '.' for c in r) for r in q.get_matrix()))"
    /// </code>
    /// </summary>
    internal static class Qr
    {
        internal const string Code = "K7MQ2XR4";
        internal const string Link = "https://bobmitch.com/valheim?seed=cVSqYlpMn0#K7MQ2XR4";
        internal const string LongLink = "https://a-very-long-self-hosted-valheim-map.example.com/maps/live?theme=dark&seed=Midgard2024#Q8W7E6R5";

        /// <summary>The longest payload each version holds in byte mode at level M.</summary>
        internal static readonly int[] Capacity = { 14, 26, 42, 62, 84, 106, 122, 152, 180 };

        /// <summary>A deterministic printable payload, so the sweeps need no fixtures.</summary>
        internal static string Sweep(int length)
        {
            var builder = new StringBuilder(length);
            for (var i = 0; i < length; i++) builder.Append((char)(33 + ((i * 7) % 94)));
            return builder.ToString();
        }

        internal static string[] Rows(QrCode qr)
        {
            var rows = new string[qr.Size];
            for (var y = 0; y < qr.Size; y++)
            {
                var builder = new StringBuilder(qr.Size);
                for (var x = 0; x < qr.Size; x++) builder.Append(qr[x, y] ? '#' : '.');
                rows[y] = builder.ToString();
            }

            return rows;
        }

        /// <summary>The leading 64 bits of the SHA-256 of the rendered symbol.</summary>
        internal static string Digest(QrCode qr)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", Rows(qr))));

            var builder = new StringBuilder(16);
            for (var i = 0; i < 8; i++) builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }

        internal static QrCode Encode(string text)
        {
            var qr = QrCode.Encode(text);
            Assert.NotNull(qr);
            return qr!;
        }

        internal static QrCode Encode(string text, int mask)
        {
            var qr = QrCode.Encode(text, mask);
            Assert.NotNull(qr);
            return qr!;
        }
    }

    /// <summary>Whole symbols, compared module for module against the reference.</summary>
    public class QrGoldenTests
    {
        private static readonly string[] BareCodeRows =
        {
            "#######....#..#######",
            "#.....#.#..##.#.....#",
            "#.###.#..##.#.#.###.#",
            "#.###.#...#...#.###.#",
            "#.###.#.#####.#.###.#",
            "#.....#..###..#.....#",
            "#######.#.#.#.#######",
            "..........#..........",
            "#.#.#.#..#..#...#..#.",
            "###.##.##.##....#...#",
            "..#.###.####..#.##..#",
            "..####.#.#####.##...#",
            "...####..#.#..#.#.#..",
            "........##....#...##.",
            "#######..#..#..######",
            "#.....#..#....##...#.",
            "#.###.#.#.#.#.#..##..",
            "#.###.#...##.#...#.#.",
            "#.###.#.#.##.######.#",
            "#.....#...####.###.#.",
            "#######.####.##.##.##",
        };

        private static readonly string[] DefaultShareLinkRows =
        {
            "#######.##.#..#.#..#####..#######",
            "#.....#.##..#.#.###..#.##.#.....#",
            "#.###.#...##.###....#.....#.###.#",
            "#.###.#.#.#.......#######.#.###.#",
            "#.###.#..#..#...##.##.....#.###.#",
            "#.....#..#.#...#####......#.....#",
            "#######.#.#.#.#.#.#.#.#.#.#######",
            "........##.....###.##..#.........",
            "#.##.###....#.##.###.#....#..#.##",
            ".#.#...##...##...###...#..##.####",
            "#..####.##.###..#.#..#.##..###..#",
            ".#####.#..##.....#.##...#..#.#.#.",
            "#..#..#.##.##.#.#..##..#.#...#.#.",
            "##...#.....#.##..#....#....#..##.",
            "#..##.###.#.#......#.##...####...",
            "###.#..##..###.#.....###.###.##..",
            ".#.#..##....#.....#####..##.###..",
            "####.#....#...##....#.#####.##.##",
            "##....##.#####.##.#.#.#.#####.##.",
            "##..##..#.##.##..###..#...#.#..##",
            ".##...#.###.#####...#......#.##..",
            "#........#....####..#..#.##...#.#",
            "..#..####..##..#.##.#..##.###.###",
            ".#..##.##..#.###......#.###.##...",
            "#..##.##....###..#.....#########.",
            "........#.....###..#.#.##...##...",
            "#######.##.#.....####..##.#.#....",
            "#.....#.##....######.####...####.",
            "#.###.#...####..###...#######.###",
            "#.###.#.##.####.##...#.##..#.#..#",
            "#.###.#.##....###.#.#.##..##.....",
            "#.....#..###.....#..#..#..###...#",
            "#######.#.#.###.####.#.#.#..##...",
        };

        private static readonly string[] LongSelfHostedLinkRows =
        {
            "#######..##.#.#..#...##..##..##...#######",
            "#.....#.#....#....#...#..#........#.....#",
            "#.###.#.#...##.##..####...###.#.#.#.###.#",
            "#.###.#.#.#.......#.####....#####.#.###.#",
            "#.###.#..#...#.....#####.##.#..##.#.###.#",
            "#.....#.....#...##...##.....#.#...#.....#",
            "#######.#.#.#.#.#.#.#.#.#.#.#.#.#.#######",
            "........#...##.#..#.#.#...#.#...#........",
            "#.....#.#.######.###....##..####.##..###.",
            "..##...#..#..#.##.##.###.###.###.#.##..#.",
            ".#.#.##.##...#.#.....#..#.##..#.#..##..#.",
            "######....####......#..#..##..###...##...",
            "#...###.#.#.#.#...#.#...#..##...#.##....#",
            "#...##.##...#.###.#.#....#.#.####.#.#...#",
            "#.#.#.#..#.#.#######.##....#..#..#..###..",
            ".##.#..##.#.###....#..##..#....###.#####.",
            "...######..##...##.#..#..#####...#.#.##.#",
            ".#.##...#####.##.###...##..##..##########",
            "..##..###.#.#..##..#...#.###..##...##.#.#",
            "#..#........########......###.####..#..##",
            ".#.##.##..#...#.#..#.##.##...###......#.#",
            "#........#.###..####.#.#.#.###.#.#.###...",
            "....###.#..##.##.....##.#.###...##.###...",
            "##..#..#.###....#.....##..#.#........#..#",
            "##..###..#...##.#..##...#......##.##.##.#",
            ".#####..##.#.##.#####.##...##..#.#####..#",
            ".#.##.#.###.####.#..##.....##...#..##....",
            "...###.#.#.#....#.#....#.......#.#.#.####",
            ".#.#.##.#.##.#.###....##.#####.##....##..",
            "##..#..#..#.##.#..#....##.##..###.####..#",
            "##.####.###.#.####.#.###.#.##..#.#..###.#",
            "#..#.#..#..#........#.##..##..#...####.##",
            "#..####.###.##...#....#.######..#####.##.",
            "........####......##...#.####.#.#...##...",
            "#######...#.#...#.#........##.###.#.###..",
            "#.....#....###.....#........#.#.#...##...",
            "#.###.#...###.#.####..#.##.##.########.#.",
            "#.###.#..#.###..#..##.#######..##..#.##.#",
            "#.###.#....##.#..#..#...#.####...#####...",
            "#.....#..##.###..##.....#.#.#.#..##.###.#",
            "#######.##..##..####..#.###..#.#.####.#..",
        };

        [Fact]
        public void EncodesBareCode()
        {
            var qr = Qr.Encode(Qr.Code, mask: 0);
            Assert.Equal(1, qr.Version);
            Assert.Equal(BareCodeRows, Qr.Rows(qr));
        }

        [Fact]
        public void EncodesDefaultShareLink()
        {
            var qr = Qr.Encode(Qr.Link, mask: 3);
            Assert.Equal(4, qr.Version);
            Assert.Equal(DefaultShareLinkRows, Qr.Rows(qr));
        }

        [Fact]
        public void EncodesLongSelfHostedLink()
        {
            var qr = Qr.Encode(Qr.LongLink, mask: 5);
            Assert.Equal(6, qr.Version);
            Assert.Equal(LongSelfHostedLinkRows, Qr.Rows(qr));
        }

        /// <summary>
        /// The link the mod actually hands a player, built the way the mod
        /// builds it rather than pasted in — so a change to MapLink that moved
        /// the code or the seed would surface here rather than in the field.
        /// </summary>
        [Fact]
        public void TheShareLinkTheModBuildsEncodesToTheSameSymbol()
        {
            var link = MapLink.Build(MapLink.Default, Qr.Code, "cVSqYlpMn0");

            Assert.Equal(Qr.Link, link);
            Assert.Equal(DefaultShareLinkRows, Qr.Rows(Qr.Encode(link, mask: 3)));
        }
    }

    /// <summary>
    /// Every version against every mask. This is what pins the parts a decoder
    /// is forgiving about — the dark module, the reserved areas, the alignment
    /// pattern layout, and the version information that only appears from
    /// version 7 onwards.
    /// </summary>
    public class QrVersionSweepTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var entry in Expected) yield return new object[] { entry.Key, entry.Value };
        }

        private static readonly Dictionary<string, string> Expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1/0"] = "1df7e21abfa15f4f",
            ["1/1"] = "b534ab7928077b6e",
            ["1/2"] = "3ccff5d06cedac50",
            ["1/3"] = "01a1fdb53303caa0",
            ["1/4"] = "bdf7d0dcf1e60695",
            ["1/5"] = "678d6167589a69c0",
            ["1/6"] = "8c7019e0627943dd",
            ["1/7"] = "2451b8cc3ee2349f",
            ["2/0"] = "c1fa034e6e11a016",
            ["2/1"] = "e2c915715aa496cc",
            ["2/2"] = "4ce3ac5b29019b4f",
            ["2/3"] = "9b51e46f811540d2",
            ["2/4"] = "6f6ca92b7b458003",
            ["2/5"] = "13bced0860dbedc8",
            ["2/6"] = "90a864482dda16b1",
            ["2/7"] = "cad43f69947c9680",
            ["3/0"] = "ea5345b2d9b5d4dd",
            ["3/1"] = "38eb0edcefebb3e0",
            ["3/2"] = "c0785339a343c128",
            ["3/3"] = "234938fa061319df",
            ["3/4"] = "9246f4591765ddcd",
            ["3/5"] = "1ea890cd7e43cb5b",
            ["3/6"] = "4570685d52354361",
            ["3/7"] = "6f47c1d968e310eb",
            ["4/0"] = "73721f9084ef0877",
            ["4/1"] = "e6b1c42f15852f1e",
            ["4/2"] = "61435a7a6caee99c",
            ["4/3"] = "10882c73328cafa7",
            ["4/4"] = "07dc2579708463ed",
            ["4/5"] = "a841f0be21be7987",
            ["4/6"] = "540194d1d7da63f3",
            ["4/7"] = "3b2e7097aaba5789",
            ["5/0"] = "6d77b92a2edf5e35",
            ["5/1"] = "621be068bad75863",
            ["5/2"] = "34fddd872157b6e6",
            ["5/3"] = "6fda966eeb8d5ced",
            ["5/4"] = "3c8f59810a660171",
            ["5/5"] = "ebfa295525ae595d",
            ["5/6"] = "2e282fca784d5fdf",
            ["5/7"] = "9b74a71c43d9280c",
            ["6/0"] = "7c46aa7784d0f896",
            ["6/1"] = "9bdd01017bb09519",
            ["6/2"] = "0750fefcf57c2f08",
            ["6/3"] = "60422f26a33d61ff",
            ["6/4"] = "8e1f07e5dec1cf43",
            ["6/5"] = "d317c79cf321edef",
            ["6/6"] = "4b43078ce8ac56ac",
            ["6/7"] = "5c563cfe5ed18690",
            ["7/0"] = "5e5b7fe34932d63c",
            ["7/1"] = "5ac59de2a63c9f9e",
            ["7/2"] = "9245f04793127cac",
            ["7/3"] = "a1d7592f92e27423",
            ["7/4"] = "03140c5e87e63ff8",
            ["7/5"] = "b6371458b2a845b1",
            ["7/6"] = "c16c2e43535a689d",
            ["7/7"] = "570b8c03a877dc2a",
            ["8/0"] = "f409cd2701d5b200",
            ["8/1"] = "1de8d6046ae7ddfb",
            ["8/2"] = "5b22f55c211b26b5",
            ["8/3"] = "df12cae43df68ec3",
            ["8/4"] = "b252a17da6f759cf",
            ["8/5"] = "68fc79c7614af32c",
            ["8/6"] = "b242128720c6e9c5",
            ["8/7"] = "e93821d24352dc5f",
            ["9/0"] = "baeddf7a9905f888",
            ["9/1"] = "3be9f819ede5e1f0",
            ["9/2"] = "6349d991c5d72353",
            ["9/3"] = "4e6384e51bbdb446",
            ["9/4"] = "bf552e9e9bf419ea",
            ["9/5"] = "32a2fd86fb81f4f1",
            ["9/6"] = "290cdc86b89a4df8",
            ["9/7"] = "eab8173be7596d99",
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void MatchesTheReference(string key, string expected)
        {
            var parts = key.Split('/');
            var version = int.Parse(parts[0]);
            var mask = int.Parse(parts[1]);

            var qr = Qr.Encode(new string('A', Qr.Capacity[version - 1]), mask);

            Assert.Equal(version, qr.Version);
            Assert.Equal(mask, qr.Mask);
            Assert.Equal(expected, Qr.Digest(qr));
        }

        [Fact]
        public void EachVersionIsFilledToItsLastByte()
        {
            for (var version = 1; version <= 9; version++)
            {
                var capacity = Qr.Capacity[version - 1];

                Assert.Equal(version, Qr.Encode(new string('A', capacity)).Version);
                if (version < 9) Assert.Equal(version + 1, Qr.Encode(new string('A', capacity + 1)).Version);
            }
        }
    }

    /// <summary>
    /// Payload lengths from 1 to 180, rotating through the masks. Between this
    /// and the version sweep every block layout, every pad length and every
    /// mask is exercised against the reference.
    /// </summary>
    public class QrLengthSweepTests
    {
        public static IEnumerable<object[]> Cases()
        {
            for (var i = 0; i < Expected.Length; i++)
            {
                var length = (i * 3) + 1;
                yield return new object[] { length, length % 8, Expected[i] };
            }
        }

        private static readonly string[] Expected =
        {
            "9f96c649e581f2ed", "22dbc718b13e767f", "0b391be0b112b657", "d12ecf454a738ab4",
            "cc900549d495f04d", "3edb74206ec79c7c", "7ee73bd472535d6a", "32ac82fadcc963a3",
            "2ba1f5ed10004ce5", "45b54d7f6f4aed5c", "bbfd01185de1c508", "8ca61e0a3db05c4a",
            "e7abb2fb2a9e633d", "c6af9b84ae686292", "7e28ee3e27766c20", "03ea83f6e4d6b6fe",
            "6d74ec3a16fb3b25", "a300bd4d65e6d8b2", "2b8978a7a80b664e", "ea1edf72b17fb411",
            "55583a4573fae118", "15ae5af28e0f7c6d", "5a7d78aa81bfc5c3", "b65f3cecad42743b",
            "86602e987800e166", "6727911d2f21683d", "fec08c730ae7e147", "cfd229830339c206",
            "8701f97256667e01", "5851e68ce429e5f1", "a92ba46235f548a0", "7027a1c90eb9eaf2",
            "83a889d09919eea5", "60c15cddf0ffc53b", "6389c2cd724757d6", "cb0fa514e0340aaa",
            "e6595f9af59c60a5", "65d9d1c07515cef3", "c55c56bb48b6b8c7", "3145a63958c34cbc",
            "841f63d34138e70e", "2d1b458750e87b8d", "7ab104b7e9a0e835", "fc0eda20cec30a5a",
            "5296dd53d0de019b", "97b6ac0a6a3207b5", "1eac301d443cb3f3", "e5de4d661edf2927",
            "84c3cf246ae5f46c", "bd923f67606edfd6", "44ab2252727ec6a3", "c4c573593cafdb6a",
            "7ccfbec8d2517e07", "bb4e42bdf262f3e5", "890b76904f812a2b", "9f41f45e4fd2f94b",
            "e66ba5bc751f79a9", "cfef91475f66f459", "bf16146d9b16eb5b", "9df22cb044d7cb51",
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void MatchesTheReference(int length, int mask, string expected)
        {
            Assert.Equal(expected, Qr.Digest(Qr.Encode(Qr.Sweep(length), mask)));
        }
    }

    /// <summary>
    /// The mask the encoder settles on, checked against the four penalty rules
    /// implemented here from the specification rather than shared with Core.
    /// <para>
    /// This is the one judgement a golden matrix cannot pin without becoming
    /// circular, and it is worth checking on its own: every mask produces a
    /// legal symbol, so a scoring mistake never shows up as "it does not scan"
    /// — it shows up as a symbol that scans a little worse than it should, on
    /// someone else's phone, at some other angle.
    /// </para>
    /// <para>
    /// Rule 3 is the one implementations genuinely disagree on. Both this and
    /// Core look for the eleven-module finder lookalike strictly inside the
    /// symbol; segno, for one, also treats the space beyond the edge as light,
    /// which makes the real finder patterns score against every mask. Either
    /// reading produces a scannable symbol, so what matters is that Core's
    /// scoring is the one written down here.
    /// </para>
    /// </summary>
    public class QrMaskChoiceTests
    {
        [Theory]
        [InlineData(Qr.Code)]
        [InlineData(Qr.Link)]
        [InlineData(Qr.LongLink)]
        public void PicksTheLowestScoringMask(string text)
        {
            Assert.Equal(BestMask(text), Qr.Encode(text).Mask);
        }

        [Fact]
        public void PicksTheLowestScoringMaskAtEveryLength()
        {
            for (var length = 1; length <= 180; length += 11)
            {
                var text = Qr.Sweep(length);
                Assert.Equal(BestMask(text), Qr.Encode(text).Mask);
            }
        }

        /// <summary>Ties go to the lower mask, so the choice is deterministic.</summary>
        private static int BestMask(string text)
        {
            var best = 0;
            var bestScore = int.MaxValue;

            for (var mask = 0; mask < 8; mask++)
            {
                var score = Penalty(Qr.Rows(Qr.Encode(text, mask)));
                if (score >= bestScore) continue;

                bestScore = score;
                best = mask;
            }

            return best;
        }

        private static int Penalty(string[] rows)
        {
            var size = rows.Length;
            var penalty = 0;

            // Rule 1: runs of five or more of one colour, along both axes.
            for (var line = 0; line < size; line++)
            {
                foreach (var horizontal in new[] { true, false })
                {
                    var run = 0;
                    var colour = '\0';

                    for (var i = 0; i < size; i++)
                    {
                        var value = horizontal ? rows[line][i] : rows[i][line];
                        if (value == colour)
                        {
                            run++;
                        }
                        else
                        {
                            colour = value;
                            run = 1;
                        }

                        if (run == 5) penalty += 3;
                        else if (run > 5) penalty += 1;
                    }
                }
            }

            // Rule 2: blocks of two by two in one colour.
            for (var y = 0; y < size - 1; y++)
            {
                for (var x = 0; x < size - 1; x++)
                {
                    var c = rows[y][x];
                    if (c == rows[y][x + 1] && c == rows[y + 1][x] && c == rows[y + 1][x + 1]) penalty += 3;
                }
            }

            // Rule 3: anything a scanner could mistake for a finder pattern.
            const string Forward = "#.###.#....";
            const string Backward = "....#.###.#";
            for (var line = 0; line < size; line++)
            {
                foreach (var horizontal in new[] { true, false })
                {
                    var builder = new StringBuilder(size);
                    for (var i = 0; i < size; i++) builder.Append(horizontal ? rows[line][i] : rows[i][line]);

                    var text = builder.ToString();
                    for (var i = 0; i + 11 <= size; i++)
                    {
                        var window = text.Substring(i, 11);
                        if (window == Forward || window == Backward) penalty += 40;
                    }
                }
            }

            // Rule 4: how far the balance of dark to light strays from even.
            var dark = 0;
            foreach (var row in rows)
            {
                foreach (var c in row)
                {
                    if (c == '#') dark++;
                }
            }

            var total = size * size;
            penalty += Math.Abs((dark * 2) - total) * 10 / total * 10;

            return penalty;
        }
    }

    /// <summary>
    /// The things a decoder is happy to ignore but a camera is not: that the
    /// patterns a scanner locks onto are exactly where it expects them, and
    /// that the encoder refuses rather than guesses.
    /// </summary>
    public class QrStructureTests
    {
        [Fact]
        public void SizeFollowsTheVersion()
        {
            for (var version = 1; version <= 9; version++)
            {
                var qr = Qr.Encode(new string('A', Qr.Capacity[version - 1]));
                Assert.Equal((4 * version) + 17, qr.Size);
            }
        }

        [Fact]
        public void TheThreeFindersAreWhereAScannerLooks()
        {
            var qr = Qr.Encode(Qr.Link);

            foreach (var (left, top) in new[] { (0, 0), (qr.Size - 7, 0), (0, qr.Size - 7) })
            {
                for (var dy = 0; dy < 7; dy++)
                {
                    for (var dx = 0; dx < 7; dx++)
                    {
                        var ring = dx == 0 || dx == 6 || dy == 0 || dy == 6;
                        var core = dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4;
                        Assert.Equal(ring || core, qr[left + dx, top + dy]);
                    }
                }
            }
        }

        [Fact]
        public void TheSeparatorsAroundTheFindersAreLight()
        {
            var qr = Qr.Encode(Qr.Link);

            for (var i = 0; i < 8; i++)
            {
                Assert.False(qr[i, 7]);
                Assert.False(qr[7, i]);
                Assert.False(qr[qr.Size - 1 - i, 7]);
                Assert.False(qr[qr.Size - 8, i]);
                Assert.False(qr[i, qr.Size - 8]);
                Assert.False(qr[7, qr.Size - 1 - i]);
            }
        }

        [Fact]
        public void TheTimingPatternsAlternate()
        {
            var qr = Qr.Encode(Qr.Link);

            for (var i = 8; i < qr.Size - 8; i++)
            {
                Assert.Equal(i % 2 == 0, qr[i, 6]);
                Assert.Equal(i % 2 == 0, qr[6, i]);
            }
        }

        /// <summary>
        /// The one module that is dark in every symbol ever made. A decoder
        /// ignores it, so nothing but a test like this catches an off-by-one.
        /// </summary>
        [Fact]
        public void TheDarkModuleIsDark()
        {
            for (var version = 1; version <= 9; version++)
            {
                var qr = Qr.Encode(new string('A', Qr.Capacity[version - 1]));
                Assert.True(qr[8, qr.Size - 8]);
            }
        }

        /// <summary>
        /// The format information is written twice. Losing one copy is
        /// survivable; the two disagreeing is not.
        /// </summary>
        [Fact]
        public void TheTwoFormatCopiesAgree()
        {
            for (var mask = 0; mask < 8; mask++)
            {
                var qr = Qr.Encode(Qr.Link, mask);

                for (var i = 0; i < 15; i++)
                {
                    var first =
                        i <= 5 ? qr[8, i]
                        : i == 6 ? qr[8, 7]
                        : i == 7 ? qr[8, 8]
                        : i == 8 ? qr[7, 8]
                        : qr[14 - i, 8];

                    var second = i <= 7 ? qr[qr.Size - 1 - i, 8] : qr[8, qr.Size - 15 + i];

                    Assert.Equal(first, second);
                }
            }
        }

        [Fact]
        public void TheFormatBitsCarryLevelMAndTheChosenMask()
        {
            for (var mask = 0; mask < 8; mask++)
            {
                // Unmasking with 0x5412 is what stops an all-light format from
                // being a legal one, so the fields read back through it.
                var bits = QrMatrix.FormatBits(QrVersions.EccLevelBits, mask) ^ 0x5412;

                Assert.Equal(mask, (bits >> 10) & 0b111);
                Assert.Equal(QrVersions.EccLevelBits, (bits >> 13) & 0b11);
            }
        }

        /// <summary>The three published version strings, as a check on the BCH code.</summary>
        [Theory]
        [InlineData(7, 0x07C94)]
        [InlineData(8, 0x085BC)]
        [InlineData(9, 0x09A99)]
        public void VersionInformationMatchesTheSpecification(int version, int expected)
        {
            Assert.Equal(expected, QrMatrix.VersionBits(version));
        }

        [Fact]
        public void NothingToEncodeIsNotASymbol()
        {
            Assert.Null(QrCode.Encode(null));
            Assert.Null(QrCode.Encode(string.Empty));
        }

        /// <summary>
        /// Past version 9 the encoder says no rather than guessing, and the
        /// panel draws nothing. That is the honest outcome: a symbol that
        /// scanned to the wrong thing would be worse than no symbol at all.
        /// </summary>
        [Fact]
        public void TooLongIsRefusedRatherThanTruncated()
        {
            Assert.NotNull(QrCode.Encode(new string('A', 180)));
            Assert.Null(QrCode.Encode(new string('A', 181)));

            // Length is counted in UTF-8 bytes, not characters. An escaped
            // non-ASCII world seed is the one realistic way to approach this.
            Assert.Null(QrCode.Encode(new string('中', 61)));
        }
    }
}
