using System;

namespace ValheimRelay.Core.Qr
{
    /// <summary>
    /// The GF(256) arithmetic behind a QR symbol's error correction.
    /// <para>
    /// Hand-rolled for the same reason <see cref="Json.JsonWriter"/> is: Core
    /// takes no package references, because a second copy of a common library in
    /// <c>BepInEx/plugins</c> is how a modpack breaks. There is not much to it —
    /// one field, one polynomial division — and it is covered by tests that do
    /// not need the game.
    /// </para>
    /// </summary>
    internal static class ReedSolomon
    {
        /// <summary>
        /// The field QR uses: GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1. The
        /// exponent table is doubled so <c>Log[a] + Log[b]</c> can index it
        /// without a modulo on every multiply.
        /// </summary>
        private const int Primitive = 0x11D;

        private static readonly byte[] Exp = new byte[512];
        private static readonly byte[] Log = new byte[256];

        static ReedSolomon()
        {
            var x = 1;
            for (var i = 0; i < 255; i++)
            {
                Exp[i] = (byte)x;
                Log[x] = (byte)i;

                x <<= 1;
                if ((x & 0x100) != 0) x ^= Primitive;
            }

            for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        internal static byte Multiply(byte a, byte b)
            => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

        /// <summary>
        /// The generator polynomial for <paramref name="degree"/> error
        /// correction codewords: the product of (x - a^0)…(x - a^(degree-1)).
        /// Coefficients run highest power first, and the leading one is always 1.
        /// </summary>
        internal static byte[] Generator(int degree)
        {
            var poly = new byte[] { 1 };

            for (var i = 0; i < degree; i++)
            {
                var root = Exp[i];
                var next = new byte[poly.Length + 1];

                // Multiplying by (x + a^i). Subtraction is XOR in GF(2), so the
                // sign the textbook writes on the root does not survive here.
                for (var j = 0; j < poly.Length; j++)
                {
                    next[j] ^= poly[j];
                    next[j + 1] ^= Multiply(poly[j], root);
                }

                poly = next;
            }

            return poly;
        }

        /// <summary>
        /// The error correction codewords for one data block: the remainder of
        /// the block divided by <paramref name="generator"/>, by synthetic
        /// division in the field.
        /// </summary>
        internal static byte[] Remainder(byte[] data, int offset, int count, byte[] generator)
        {
            var length = generator.Length - 1;
            var remainder = new byte[length];

            for (var i = 0; i < count; i++)
            {
                var factor = (byte)(data[offset + i] ^ remainder[0]);

                Array.Copy(remainder, 1, remainder, 0, length - 1);
                remainder[length - 1] = 0;

                if (factor == 0) continue;
                for (var j = 0; j < length; j++)
                {
                    remainder[j] ^= Multiply(generator[j + 1], factor);
                }
            }

            return remainder;
        }
    }
}
