using System;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// Cleans up the relay URL a player might paste into their config.
    /// <para>
    /// This lives in Core rather than in the plugin config purely so it can be
    /// tested: it is small, but it is the difference between a working install
    /// and a silent connection failure, and the mistakes it absorbs — an
    /// <c>https://</c> address, a bare host, a missing <c>/ws</c> — are the ones
    /// a player will actually make when handed a relay address by a friend.
    /// </para>
    /// </summary>
    public static class RelayUrl
    {
        public const string PathSuffix = "/ws";

        public static string Normalise(string? raw, string fallback = "ws://localhost:8080/ws")
        {
            var url = (raw ?? string.Empty).Trim();
            if (url.Length == 0) return fallback;

            if (StartsWith(url, "https://")) url = "wss://" + url.Substring("https://".Length);
            else if (StartsWith(url, "http://")) url = "ws://" + url.Substring("http://".Length);
            else if (!StartsWith(url, "ws://") && !StartsWith(url, "wss://"))
            {
                // No scheme at all. Default to the secure one: a player pasting a
                // hostname should not silently end up unencrypted.
                url = "wss://" + url;
            }

            var trimmed = url.TrimEnd('/');
            if (trimmed.Length == 0) return fallback;

            if (!EndsWith(trimmed, PathSuffix)) trimmed += PathSuffix;

            return Uri.TryCreate(trimmed, UriKind.Absolute, out _) ? trimmed : fallback;
        }

        /// <summary>True for a URL that carries telemetry in the clear.</summary>
        public static bool IsInsecure(string url)
        {
            if (StartsWith(url ?? string.Empty, "ws://"))
            {
                // Local development is the legitimate case (§9, M2).
                return !(url!.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("[::1]"));
            }
            return false;
        }

        private static bool StartsWith(string value, string prefix)
            => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static bool EndsWith(string value, string suffix)
            => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
