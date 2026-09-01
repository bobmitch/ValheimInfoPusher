using System;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// Builds the one copyable thing the mod hands a player: a link to the web
    /// map with the session code in the fragment (§11.3).
    /// <para>
    /// The fragment matters. It is the only part of a URL a browser does not
    /// send to the server, so the code — which §8 establishes is the credential
    /// — stays out of the map's access logs, out of any referrer header, and out
    /// of whatever analytics the page loads. Putting it in a query string would
    /// leak the credential to every one of those.
    /// </para>
    /// </summary>
    public static class MapLink
    {
        /// <summary>
        /// The map the mod ships pointed at. Paired with
        /// <see cref="RelayUrl.Default"/>: shipping one without the other leaves
        /// the player copying a bare code and looking for somewhere to paste it.
        /// </summary>
        public const string Default = "https://bobmitch.com/valheim";

        /// <summary>
        /// Cleans up a configured map address. Browsers speak http, so a
        /// <c>ws://</c> address here is a paste error rather than a preference,
        /// and is corrected rather than rejected.
        /// </summary>
        public static string Normalise(string? raw)
        {
            var url = (raw ?? string.Empty).Trim();
            if (url.Length == 0) return string.Empty;

            if (StartsWith(url, "wss://")) url = "https://" + url.Substring("wss://".Length);
            else if (StartsWith(url, "ws://")) url = "http://" + url.Substring("ws://".Length);
            else if (!StartsWith(url, "http://") && !StartsWith(url, "https://")) url = "https://" + url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return string.Empty;
            if (string.IsNullOrEmpty(parsed.Host)) return string.Empty;

            // Drop any fragment the player left on it; Build supplies its own.
            return parsed.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        }

        /// <summary>
        /// The share text for a code: a link when a map is configured, the bare
        /// code when one is not. Never returns something that looks like a link
        /// but is not one.
        /// </summary>
        public static string Build(string? mapUrl, string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;

            var normalised = Normalise(mapUrl);
            if (normalised.Length == 0) return code;

            // §11.3 wrote this as "<base>/#<code>", which is right for a map at
            // the root of a host but wrong for one at a path: "/valheim/#CODE"
            // relies on the server redirecting the trailing slash, and plenty do
            // not. Append the slash only where there is no path to speak of.
            var separator = HasPath(normalised) ? "#" : "/#";

            return normalised.TrimEnd('/') + separator + Uri.EscapeDataString(code);
        }

        private static bool HasPath(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
            var path = parsed.AbsolutePath.Trim('/');
            return path.Length > 0;
        }

        private static bool StartsWith(string value, string prefix)
            => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
