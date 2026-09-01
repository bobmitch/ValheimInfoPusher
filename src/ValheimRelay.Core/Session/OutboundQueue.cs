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

        // The frame handed out by the last TryPeek, and which lane it came from.
        // Held by reference so a position superseded between peek and commit is
        // not silently discarded.
        private string? _peeked;
        private bool _peekedFromReliable;

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

        /// <summary>
        /// Look at the next frame without removing it. Call <see cref="CommitPeek"/>
        /// once it has actually been handed to the transport.
        /// <para>
        /// Peek-then-commit rather than dequeue-then-put-back, because putting a
        /// refused frame back is not something this structure can do correctly: a
        /// re-enqueue appends to the tail, which would reorder a marker
        /// add/remove pair into a resurrection, and a refused *position* has no
        /// lane to go back to at all — pushing it into the reliable queue would
        /// promote superseded telemetry above real frames.
        /// </para>
        /// </summary>
        public bool TryPeek(out string frame)
        {
            lock (_gate)
            {
                if (_reliable.Count > 0)
                {
                    _peeked = _reliable.Peek();
                    _peekedFromReliable = true;
                    frame = _peeked;
                    return true;
                }

                if (_latestPosition != null)
                {
                    _peeked = _latestPosition;
                    _peekedFromReliable = false;
                    frame = _peeked;
                    return true;
                }

                _peeked = null;
            }

            frame = string.Empty;
            return false;
        }

        /// <summary>Removes the frame handed out by the last <see cref="TryPeek"/>.</summary>
        public void CommitPeek()
        {
            lock (_gate)
            {
                if (_peeked == null) return;

                if (_peekedFromReliable)
                {
                    if (_reliable.Count > 0 && ReferenceEquals(_reliable.Peek(), _peeked)) _reliable.Dequeue();
                }
                else if (ReferenceEquals(_latestPosition, _peeked))
                {
                    // Only if it is still the same sample: a newer position may
                    // have arrived from the main thread while this one was in
                    // flight, and that one has not been sent.
                    _latestPosition = null;
                }

                _peeked = null;
            }
        }

        public bool TryDequeue(out string frame)
        {
            if (!TryPeek(out frame)) return false;
            CommitPeek();
            return true;
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
                _peeked = null;
            }
        }
    }
}
