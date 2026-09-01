using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ValheimRelay.Core.Identity
{
    /// <summary>
    /// Derives the stable, non-identifying <c>uid</c> a mod sends in <c>hello</c> (§3.1).
    /// <para>
    /// §8 says to send a hash of the local profile id rather than a raw platform
    /// id. A bare hash is not enough: a Valheim profile id derives from a Steam
    /// ID, and the space of real Steam IDs is small and enumerable, so anyone
    /// with a list can invert an unsalted SHA-256 by brute force in seconds. The
    /// hash is therefore keyed with a random per-install salt, which makes the
    /// output unlinkable to the account while staying stable for as long as the
    /// install lives — which is all §3.1 actually requires of it.
    /// </para>
    /// <para>
    /// The salt is generated once and persisted next to the config. Losing it is
    /// harmless: the player gets a new <c>uid</c> and maps treat them as a new
    /// player.
    /// </para>
    /// </summary>
    public static class StableUid
    {
        public const string Prefix = "vh_";

        /// <summary>Length in hex characters of the identifier after the prefix.</summary>
        public const int DigestChars = 16;

        public static byte[] NewSalt()
        {
            var salt = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }

        /// <summary>
        /// HMAC-SHA256 over the profile id, keyed with the install salt, truncated
        /// to 64 bits and hex-encoded. Truncation is fine here: the value only has
        /// to be collision-resistant across the 16 players of one room.
        /// </summary>
        public static string Derive(string profileId, byte[] salt)
        {
            if (profileId == null) throw new ArgumentNullException(nameof(profileId));
            if (salt == null) throw new ArgumentNullException(nameof(salt));
            if (salt.Length == 0) throw new ArgumentException("salt must not be empty", nameof(salt));

            using (var hmac = new HMACSHA256(salt))
            {
                var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(profileId));
                var sb = new StringBuilder(Prefix.Length + DigestChars);
                sb.Append(Prefix);
                for (var i = 0; i < DigestChars / 2; i++)
                {
                    sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        public static string EncodeSalt(byte[] salt) => Convert.ToBase64String(salt);

        public static bool TryDecodeSalt(string? encoded, out byte[] salt)
        {
            salt = System.Array.Empty<byte>();
            if (string.IsNullOrEmpty(encoded)) return false;
            try
            {
                var decoded = Convert.FromBase64String(encoded!);
                if (decoded.Length < 16) return false;
                salt = decoded;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
