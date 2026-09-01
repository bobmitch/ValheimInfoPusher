namespace ValheimRelay.Core.Protocol
{
    /// <summary>The <c>type</c> values defined in PLAN.md §3, plus the relay's own.</summary>
    public static class FrameTypes
    {
        // Relay-originated (§1.2, §1.3).
        public const string Welcome = "welcome";
        public const string PlayerJoined = "player_joined";
        public const string PlayerLeft = "player_left";

        // Mod → relay (§3.1, §3.2).
        public const string Hello = "hello";
        public const string Position = "position";

        // Either direction (§3.3, §3.4).
        public const string Ping = "ping";
        public const string Marker = "marker";

        // Map → relay (§3.5).
        public const string RequestState = "request_state";
    }

    /// <summary>Protocol version carried on every frame as <c>v</c>.</summary>
    public static class ProtocolVersion
    {
        public const int Current = 1;
    }

    /// <summary>
    /// Marker icons (§3.4). A fixed vocabulary; anything unrecognised degrades
    /// to <see cref="Dot"/> rather than being dropped.
    /// </summary>
    public static class MarkerIcons
    {
        public const string Dot = "dot";
        public const string Ore = "ore";
        public const string Boss = "boss";
        public const string Home = "home";
        public const string Death = "death";
        public const string Danger = "danger";

        private static readonly string[] Known = { Dot, Ore, Boss, Home, Death, Danger };

        public static string Normalise(string? icon)
        {
            if (string.IsNullOrEmpty(icon)) return Dot;
            foreach (var known in Known)
            {
                if (string.Equals(known, icon, System.StringComparison.OrdinalIgnoreCase)) return known;
            }
            return Dot;
        }

        public static bool IsKnown(string? icon)
            => !string.IsNullOrEmpty(icon) && System.Array.IndexOf(Known, icon) >= 0;
    }

    public static class MarkerOps
    {
        public const string Add = "add";
        public const string Remove = "remove";
    }
}
