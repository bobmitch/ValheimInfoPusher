using System;
using System.Text;

namespace ValheimRelay.Core.Qr
{
    /// <summary>
    /// A QR symbol, as a grid of dark and light modules. Nothing here knows
    /// about pixels, textures or Unity — the plugin turns the grid into
    /// something drawable (§4.1).
    /// <para>
    /// The panel shows a link that carries the session code, and §8 establishes
    /// that the code is a credential. That is why this is generated locally
    /// rather than fetched from one of the QR image services: handing the code
    /// to a third party to draw would hand it the session, and would undo the
    /// care <see cref="Session.MapLink"/> takes to keep it out of even the map's
    /// own server logs.
    /// </para>
    /// <para>
    /// One configuration only: byte mode, error correction level M, versions 1
    /// to 9. That covers every share link by a wide margin and leaves out most
    /// of the specification — see <see cref="QrVersions"/> for why the ceiling
    /// is where it is.
    /// </para>
    /// </summary>
    public sealed class QrCode
    {
        private readonly bool[] _modules;

        private QrCode(int version, int mask, bool[] modules)
        {
            Version = version;
            Mask = mask;
            Size = QrVersions.Size(version);
            _modules = modules;
        }

        /// <summary>The symbol version, 1 to 9.</summary>
        public int Version { get; }

        /// <summary>Which of the eight mask patterns scored best.</summary>
        public int Mask { get; }

        /// <summary>The symbol's width and height in modules, excluding the quiet zone.</summary>
        public int Size { get; }

        /// <summary>True where the module is dark. Origin is the top-left corner.</summary>
        public bool this[int x, int y] => _modules[(y * Size) + x];

        /// <summary>
        /// Encodes <paramref name="text"/>, or returns null when it will not fit
        /// — which callers should treat as "show something else", not as an
        /// error. Nothing the mod produces comes close to the 180-byte ceiling,
        /// so this is a guard against a configured map URL that has gone
        /// somewhere unreasonable rather than an expected outcome.
        /// </summary>
        public static QrCode? Encode(string? text) => Encode(text, forcedMask: -1);

        /// <summary>
        /// Encoding with the mask chosen rather than scored, so a test can
        /// compare against a reference encoder without both having to agree on
        /// the penalty heuristic.
        /// </summary>
        internal static QrCode? Encode(string? text, int forcedMask)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var bytes = Encoding.UTF8.GetBytes(text!);
            var version = QrVersions.SmallestFor(bytes.Length);
            if (version == 0) return null;

            var codewords = BuildCodewords(bytes, version);

            var matrix = new QrMatrix(version);
            matrix.PlaceData(codewords);

            var modules = matrix.Finish(forcedMask, out var mask);
            return new QrCode(version, mask, modules);
        }

        /// <summary>
        /// The data codewords, interleaved with their error correction
        /// codewords. Interleaving is what makes a scratch survivable: it
        /// spreads consecutive damage across every block instead of wiping one
        /// block past its correction capacity.
        /// </summary>
        private static byte[] BuildCodewords(byte[] bytes, int version)
        {
            var plan = QrVersions.Plan(version);
            var data = EncodeData(bytes, plan);

            var generator = ReedSolomon.Generator(plan.ErrorCodewords);
            var blocks = new byte[plan.Blocks][];
            var correction = new byte[plan.Blocks][];

            var offset = 0;
            for (var i = 0; i < plan.Blocks; i++)
            {
                var length = i < plan.Group1Blocks ? plan.Group1Data : plan.Group2Data;

                var block = new byte[length];
                Array.Copy(data, offset, block, 0, length);
                offset += length;

                blocks[i] = block;
                correction[i] = ReedSolomon.Remainder(block, 0, length, generator);
            }

            var result = new byte[data.Length + (plan.Blocks * plan.ErrorCodewords)];
            var at = 0;

            var longest = plan.Group2Blocks > 0 ? plan.Group2Data : plan.Group1Data;
            for (var i = 0; i < longest; i++)
            {
                foreach (var block in blocks)
                {
                    if (i < block.Length) result[at++] = block[i];
                }
            }

            for (var i = 0; i < plan.ErrorCodewords; i++)
            {
                foreach (var block in correction)
                {
                    result[at++] = block[i];
                }
            }

            return result;
        }

        /// <summary>
        /// The data codewords before error correction: a four-bit mode
        /// indicator, an eight-bit length, the bytes themselves, and then
        /// whatever it takes to fill the version exactly.
        /// </summary>
        private static byte[] EncodeData(byte[] bytes, BlockPlan plan)
        {
            var data = new byte[plan.DataCodewords];
            var capacity = data.Length * 8;
            var bit = 0;

            Append(data, ref bit, 0b0100, 4);
            Append(data, ref bit, bytes.Length, 8);
            foreach (var value in bytes) Append(data, ref bit, value, 8);

            // The terminator, then the run up to a byte boundary, are zeroes,
            // and the array already is. Only the cursor has to move.
            bit += Math.Min(4, capacity - bit);
            bit = (bit + 7) / 8 * 8;

            // The two pad codewords the specification names, alternating.
            for (var index = bit / 8; index < data.Length; index++)
            {
                data[index] = (index - (bit / 8)) % 2 == 0 ? (byte)0xEC : (byte)0x11;
            }

            return data;
        }

        private static void Append(byte[] data, ref int bit, int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0) data[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
                bit++;
            }
        }
    }
}
