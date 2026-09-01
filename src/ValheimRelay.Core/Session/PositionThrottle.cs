using System;
using ValheimRelay.Core.Protocol;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// Decides whether a freshly-read position is worth sending.
    /// <para>
    /// PLAN.md budgets for an unconditional 1 Hz (§3.6). Most of those frames are
    /// a player standing at a workbench sending byte-identical telemetry, so this
    /// applies a movement dead-band with a slow keepalive underneath it. The
    /// keepalive matters: without one the map cannot distinguish "not moving"
    /// from "stopped sending", which is the same ambiguity the <c>share</c> flag
    /// on <c>hello</c> exists to remove.
    /// </para>
    /// </summary>
    public sealed class PositionThrottle
    {
        private readonly SessionOptions _options;
        private PositionSample _last;
        private TimeSpan _lastSentAt;
        private bool _hasSent;

        public PositionThrottle(SessionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Reset()
        {
            _hasSent = false;
            _lastSentAt = TimeSpan.Zero;
        }

        public bool ShouldSend(in PositionSample sample, TimeSpan now)
        {
            if (!_hasSent) return true;

            // Anything a viewer would notice as a discrete change goes out at
            // once, dead-band or not.
            if (sample.Dead != _last.Dead) return true;
            if (sample.IncludeHealth && _last.IncludeHealth && sample.Health != _last.Health) return true;
            if (!string.Equals(sample.Biome, _last.Biome, StringComparison.Ordinal)) return true;

            if (now - _lastSentAt >= _options.PositionKeepalive) return true;

            var minimum = _options.PositionMinMetres;
            if (sample.HorizontalDistanceSquaredTo(_last) >= minimum * minimum) return true;

            var turned = Math.Abs(AngleDelta(sample.RotationDegrees, _last.RotationDegrees));
            return turned >= _options.PositionMinRotationDegrees;
        }

        public void MarkSent(in PositionSample sample, TimeSpan now)
        {
            _last = sample;
            _lastSentAt = now;
            _hasSent = true;
        }

        /// <summary>Signed shortest angular difference in degrees, so 359° → 1° reads as 2, not 358.</summary>
        internal static double AngleDelta(double a, double b)
        {
            var delta = (a - b) % 360.0;
            if (delta > 180.0) delta -= 360.0;
            if (delta < -180.0) delta += 360.0;
            return delta;
        }
    }
}
