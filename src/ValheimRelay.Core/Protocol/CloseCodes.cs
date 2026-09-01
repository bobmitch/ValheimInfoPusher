namespace ValheimRelay.Core.Protocol
{
    /// <summary>Relay close codes (PLAN.md §1.4) and what they imply for retry policy.</summary>
    public static class CloseCodes
    {
        /// <summary>Reclaim token does not match. Discard the stored token+code, create fresh.</summary>
        public const int TokenMismatch = 4003;

        /// <summary>Unknown or expired code. Creator creates fresh; joiner waits for a new code.</summary>
        public const int UnknownCode = 4004;

        /// <summary>Room is full. Not transient — the 17th player will never fit.</summary>
        public const int RoomFull = 4008;

        /// <summary>Relay is at its room limit. Transient; back off hard with jitter.</summary>
        public const int RelayFull = 4013;

        /// <summary>
        /// True for close codes that must not be retried on the normal backoff
        /// ladder. <see cref="RoomFull"/> never resolves on its own;
        /// <see cref="RelayFull"/> does, but tight retries from every client at
        /// once produce exactly the thundering herd the relay is shedding.
        /// </summary>
        public static bool RequiresSpecialHandling(int code)
            => code == RoomFull || code == RelayFull;

        public static string Describe(int code) => code switch
        {
            TokenMismatch => "reclaim token rejected",
            UnknownCode => "unknown or expired code",
            RoomFull => "room is full",
            RelayFull => "relay is at its room limit",
            1000 => "normal closure",
            1001 => "endpoint going away",
            1006 => "connection lost",
            _ => "close code " + code.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }
}
