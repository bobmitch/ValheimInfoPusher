using System;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// Exponential backoff with jitter (§5.2): 1, 2, 4, 8, 16, capped at 30 s,
    /// reset after a spell of healthy connection.
    /// </summary>
    public sealed class Backoff
    {
        private readonly double _baseSeconds;
        private readonly double _capSeconds;
        private readonly double _jitterFraction;
        private readonly Func<double> _random;
        private int _attempt;

        public Backoff(
            double baseSeconds = 1.0,
            double capSeconds = 30.0,
            double jitterFraction = 0.25,
            Func<double>? random = null)
        {
            if (baseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(baseSeconds));
            if (capSeconds < baseSeconds) throw new ArgumentOutOfRangeException(nameof(capSeconds));
            if (jitterFraction < 0 || jitterFraction > 1) throw new ArgumentOutOfRangeException(nameof(jitterFraction));

            _baseSeconds = baseSeconds;
            _capSeconds = capSeconds;
            _jitterFraction = jitterFraction;
            _random = random ?? SharedRandom.NextDouble;
        }

        public int Attempt => _attempt;

        public void Reset() => _attempt = 0;

        /// <summary>
        /// The delay before the next attempt, and advances the ladder. Jitter is
        /// applied symmetrically around the nominal delay so a room full of
        /// clients that dropped together does not reconnect in lockstep.
        /// </summary>
        public TimeSpan Next()
        {
            var nominal = _baseSeconds * Math.Pow(2, _attempt);
            if (nominal > _capSeconds || double.IsInfinity(nominal)) nominal = _capSeconds;
            if (_attempt < 30) _attempt++;

            var spread = nominal * _jitterFraction;
            var delay = nominal - spread + (_random() * spread * 2);
            if (delay < 0) delay = 0;
            return TimeSpan.FromSeconds(delay);
        }

        /// <summary>
        /// A deliberately harsher ladder for close code 4013 (relay full, §5.2).
        /// Same shape, but it starts well past the point where a synchronised
        /// herd would re-arrive, and jitters much wider.
        /// </summary>
        public static Backoff ForRelayFull(Func<double>? random = null)
            => new Backoff(baseSeconds: 5.0, capSeconds: 120.0, jitterFraction: 0.5, random: random);
    }

    internal static class SharedRandom
    {
        [ThreadStatic]
        private static Random? _random;

        public static double NextDouble()
        {
            // Seeded per thread: Random is not thread-safe, and on Mono the
            // default seed is time-based, so two threads created in the same
            // tick would otherwise produce identical jitter.
            _random ??= new Random(Environment.TickCount ^ (System.Threading.Thread.CurrentThread.ManagedThreadId * 7919));
            return _random.NextDouble();
        }
    }
}
