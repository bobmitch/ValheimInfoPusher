using System;
using System.Collections.Generic;

namespace ValheimRelay.Core.Session
{
    /// <summary>
    /// The main-thread → socket-thread handoff (§4.2).
    /// <para>
    /// PLAN.md describes this as one bounded queue that drops the oldest
    /// <c>position</c> under backpressure. A single FIFO cannot do that without
    /// scanning, so the structure is split instead: one overwrite slot for the
    /// latest position, and one bounded FIFO for everything that must not be
    /// lost. That gives the intended policy exactly — position is lossy, a
    /// dropped marker is a bug the player sees — and it is O(1).
    /// </para>
    /// <para>
    /// Reliable frames are dequeued before the position slot, so a marker never
    /// waits behind telemetry.
    /// </para>
    /// </summary>
    public sealed class OutboundQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<string> _reliable = new Queue<string>();
        private readonly int _reliableCapacity;
        private string? _latestPosition;

        public OutboundQueue(int reliableCapacity = 64)
        {
            if (reliableCapacity < 1) throw new ArgumentOutOfRangeException(nameof(reliableCapacity));
            _reliableCapacity = reliableCapacity;
        }

        /// <summary>Frames dropped because the reliable queue was full. Surfaced for logging; should stay 0.</summary>
        public int DroppedReliable { get; private set; }

        /// <summary>Position samples superseded before they were sent. Expected to be non-zero under load.</summary>
        public int SupersededPositions { get; private set; }

        public int Count
        {
            get
            {
                lock (_gate) return _reliable.Count + (_latestPosition == null ? 0 : 1);
            }
        }

        /// <summary>Enqueue a frame that must not be lost: hello, ping, marker.</summary>
        public bool EnqueueReliable(string frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            lock (_gate)
            {
                if (_reliable.Count >= _reliableCapacity)
                {
                    // Full means the socket has been unable to drain for a long
                    // time. Drop the newest rather than the oldest: the older
                    // frames are the ones peers are already waiting on, and
                    // dropping the head would reorder a marker add/remove pair.
                    DroppedReliable++;
                    return false;
                }

                _reliable.Enqueue(frame);
                return true;
            }
        }

        /// <summary>Offer the latest position, superseding any unsent one.</summary>
        public void SetPosition(string frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            lock (_gate)
            {
                if (_latestPosition != null) SupersededPositions++;
                _latestPosition = frame;
            }
        }

        public bool TryDequeue(out string frame)
        {
            lock (_gate)
            {
                if (_reliable.Count > 0)
                {
                    frame = _reliable.Dequeue();
                    return true;
                }

                if (_latestPosition != null)
                {
                    frame = _latestPosition;
                    _latestPosition = null;
                    return true;
                }
            }

            frame = string.Empty;
            return false;
        }

        /// <summary>
        /// Discard everything pending. Called when a connection ends: the queued
        /// frames describe a session the next connection will not be part of,
        /// and replaying a stale position after a reconnect makes players jump.
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                _reliable.Clear();
                _latestPosition = null;
            }
        }
    }
}
