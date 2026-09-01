using System;
using System.Collections.Generic;
using ValheimRelay.Core.Session;

namespace ValheimRelay.Core.Election
{
    public enum CodeDecision
    {
        /// <summary>Nothing to do — already on this code, or the announcement is stale.</summary>
        Ignore,

        /// <summary>Switch to the announced code.</summary>
        Adopt,

        /// <summary>Ours wins; re-announce so the sender migrates to us.</summary>
        Defend
    }

    /// <summary>
    /// Resolves competing session codes heard over the game channel (§5.1).
    /// <para>
    /// PLAN.md gives one rule — the lexicographically smaller code wins — which
    /// settles a simultaneous double-create but not the two cases that follow
    /// from it:
    /// </para>
    /// <list type="number">
    /// <item>
    /// After a rotation (§5.3) the group is deliberately on a <em>new</em> code
    /// that may sort larger than the dead one. A peer that has not noticed the
    /// rotation re-announces the old code and, under "smaller wins" alone, drags
    /// everyone back onto a room the relay has already swept. An
    /// <see cref="CodeAnnouncement.Epoch"/> fixes the ordering: a later
    /// generation always beats an earlier one, and the code comparison only
    /// breaks ties <em>within</em> a generation.
    /// </item>
    /// <item>
    /// A code that the relay has answered with 4004 is known-dead. Adopting it
    /// again on the next announcement produces a migrate/fail/re-migrate loop,
    /// so dead codes are remembered — per epoch, so that a creator legitimately
    /// reclaiming its old code in a later generation is still heard.
    /// </item>
    /// </list>
    /// </summary>
    public sealed class CodeArbiter
    {
        private readonly IClock _clock;
        private readonly TimeSpan _deadCodeTtl;
        private readonly Dictionary<string, DeadCode> _dead =
            new Dictionary<string, DeadCode>(StringComparer.OrdinalIgnoreCase);

        public CodeArbiter(IClock clock, TimeSpan? deadCodeTtl = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _deadCodeTtl = deadCodeTtl ?? TimeSpan.FromMinutes(10);
        }

        /// <summary>The code this client currently holds, canonical uppercase, or null.</summary>
        public string? CurrentCode { get; private set; }

        /// <summary>The generation of <see cref="CurrentCode"/>. Monotonic across rotations.</summary>
        public long CurrentEpoch { get; private set; }

        /// <summary>Highest epoch ever seen, including from peers. A new room is created above this.</summary>
        public long HighestSeenEpoch { get; private set; }

        /// <summary>Adopt a code we created or successfully joined.</summary>
        public void SetCurrent(string code, long epoch)
        {
            if (string.IsNullOrEmpty(code)) throw new ArgumentException("code required", nameof(code));
            CurrentCode = code;
            CurrentEpoch = epoch;
            if (epoch > HighestSeenEpoch) HighestSeenEpoch = epoch;
        }

        public void ClearCurrent()
        {
            CurrentCode = null;
            CurrentEpoch = 0;
        }

        /// <summary>The epoch a freshly created room should claim: one past anything we have heard.</summary>
        public long NextEpoch() => HighestSeenEpoch + 1;

        /// <summary>
        /// Record that a code is dead at a given generation — call this on close
        /// code 4004 (§1.4). A later generation of the same code is still adoptable,
        /// which is what makes token reclaim (§5.3) survive this.
        /// </summary>
        public void MarkDead(string code, long epoch)
        {
            if (string.IsNullOrEmpty(code)) return;
            PruneDead();

            if (_dead.TryGetValue(code, out var existing) && existing.Epoch >= epoch)
            {
                _dead[code] = new DeadCode(existing.Epoch, _clock.Elapsed);
            }
            else
            {
                _dead[code] = new DeadCode(epoch, _clock.Elapsed);
            }

            if (string.Equals(CurrentCode, code, StringComparison.OrdinalIgnoreCase) && CurrentEpoch <= epoch)
            {
                ClearCurrent();
            }
        }

        public bool IsKnownDead(string code, long epoch)
        {
            PruneDead();
            return _dead.TryGetValue(code, out var dead) && dead.Epoch >= epoch;
        }

        /// <summary>Apply the arbitration rules to a code heard from a peer.</summary>
        public CodeDecision Consider(in CodeAnnouncement announcement)
        {
            var code = announcement.Code;
            if (string.IsNullOrEmpty(code)) return CodeDecision.Ignore;

            if (announcement.Epoch > HighestSeenEpoch) HighestSeenEpoch = announcement.Epoch;

            if (IsKnownDead(code, announcement.Epoch)) return CodeDecision.Ignore;

            if (CurrentCode == null) return CodeDecision.Adopt;

            if (string.Equals(CurrentCode, code, StringComparison.OrdinalIgnoreCase))
            {
                // Same room. Take the higher epoch so a rotation that reused the
                // code (a reclaim) does not leave us one generation behind.
                if (announcement.Epoch > CurrentEpoch) CurrentEpoch = announcement.Epoch;
                return CodeDecision.Ignore;
            }

            if (announcement.Epoch > CurrentEpoch) return CodeDecision.Adopt;
            if (announcement.Epoch < CurrentEpoch) return CodeDecision.Defend;

            // Same generation: two clients created at once. The lexicographically
            // smaller code wins (§5.1). Ordinal, on the canonical uppercase form
            // the relay hands back, so every client computes the same winner.
            var comparison = string.CompareOrdinal(
                code.ToUpperInvariant(),
                CurrentCode.ToUpperInvariant());

            if (comparison < 0) return CodeDecision.Adopt;
            if (comparison > 0) return CodeDecision.Defend;
            return CodeDecision.Ignore;
        }

        private void PruneDead()
        {
            if (_dead.Count == 0) return;
            var now = _clock.Elapsed;

            List<string>? expired = null;
            foreach (var pair in _dead)
            {
                if (now - pair.Value.At >= _deadCodeTtl)
                {
                    (expired ??= new List<string>()).Add(pair.Key);
                }
            }

            if (expired == null) return;
            foreach (var key in expired) _dead.Remove(key);
        }

        private readonly struct DeadCode
        {
            public DeadCode(long epoch, TimeSpan at)
            {
                Epoch = epoch;
                At = at;
            }

            public long Epoch { get; }
            public TimeSpan At { get; }
        }
    }
}
