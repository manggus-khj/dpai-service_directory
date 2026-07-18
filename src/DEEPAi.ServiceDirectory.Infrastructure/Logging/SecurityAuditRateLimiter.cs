using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DEEPAi.ServiceDirectory.Infrastructure.Logging
{
    internal struct SecurityAuditRateLimitKey : IEquatable<SecurityAuditRateLimitKey>
    {
        public SecurityAuditRateLimitKey(
            SecurityAuditEventId eventId,
            SecurityAuditBoundary boundary,
            string remoteAddress)
        {
            if (remoteAddress == null)
            {
                throw new ArgumentNullException(nameof(remoteAddress));
            }

            EventId = eventId;
            Boundary = boundary;
            RemoteAddress = remoteAddress;
        }

        public SecurityAuditEventId EventId { get; }

        public SecurityAuditBoundary Boundary { get; }

        public string RemoteAddress { get; }

        public bool Equals(SecurityAuditRateLimitKey other)
        {
            return EventId == other.EventId
                && Boundary == other.Boundary
                && StringComparer.Ordinal.Equals(
                    RemoteAddress,
                    other.RemoteAddress);
        }

        public override bool Equals(object obj)
        {
            return obj is SecurityAuditRateLimitKey
                && Equals((SecurityAuditRateLimitKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = 17;
                hashCode = (hashCode * 31) + (int)EventId;
                hashCode = (hashCode * 31) + (int)Boundary;
                hashCode = (hashCode * 31)
                    + StringComparer.Ordinal.GetHashCode(RemoteAddress);
                return hashCode;
            }
        }
    }

    internal struct SecurityAuditRateLimitDecision
    {
        private SecurityAuditRateLimitDecision(
            bool shouldWrite,
            long suppressedCount)
        {
            ShouldWrite = shouldWrite;
            SuppressedCount = suppressedCount;
        }

        public bool ShouldWrite { get; }

        public long SuppressedCount { get; }

        public static SecurityAuditRateLimitDecision Write(long suppressedCount)
        {
            if (suppressedCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(suppressedCount));
            }

            return new SecurityAuditRateLimitDecision(true, suppressedCount);
        }

        public static SecurityAuditRateLimitDecision Suppress()
        {
            return new SecurityAuditRateLimitDecision(false, 0);
        }
    }

    internal sealed class SecurityAuditRateLimiter
    {
        internal const int MaximumTrackedKeys = 2048;
        internal const int PerKeyBurstCapacity = 5;
        internal const int GlobalWritesPerMinute = 60;
        internal const int StaleEntryMinutes = 10;

        private const double PerKeyTokensPerSecond = 1.0 / 60.0;
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<SecurityAuditRateLimitKey, PerKeyState>
            _perKeyStates =
                new Dictionary<SecurityAuditRateLimitKey, PerKeyState>();
        private readonly LinkedList<SecurityAuditRateLimitKey> _recency =
            new LinkedList<SecurityAuditRateLimitKey>();
        private readonly Queue<long> _globalWriteTimestamps = new Queue<long>();
        private long _lastObservedTimestamp;

        public SecurityAuditRateLimiter()
            : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
        {
        }

        internal SecurityAuditRateLimiter(
            Func<long> timestampProvider,
            long timestampFrequency)
        {
            if (timestampProvider == null)
            {
                throw new ArgumentNullException(nameof(timestampProvider));
            }

            if (timestampFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampFrequency),
                    timestampFrequency,
                    "Timestamp frequency must be positive.");
            }

            long initialTimestamp = timestampProvider();
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider),
                    "The timestamp provider returned a negative value.");
            }

            _timestampProvider = timestampProvider;
            _timestampFrequency = timestampFrequency;
            _lastObservedTimestamp = initialTimestamp;
        }

        public SecurityAuditRateLimitDecision Evaluate(
            SecurityAuditRateLimitKey key)
        {
            lock (_syncRoot)
            {
                long now = GetMonotonicTimestamp();
                RemoveStaleStates(now);
                PerKeyState state = GetOrCreateState(key, now);

                Refill(
                    state.Bucket,
                    now,
                    PerKeyBurstCapacity,
                    PerKeyTokensPerSecond);
                RemoveExpiredGlobalWrites(now);

                if (state.Bucket.Tokens < 1.0
                    || _globalWriteTimestamps.Count >= GlobalWritesPerMinute)
                {
                    state.SuppressedCount = SaturatingIncrement(
                        state.SuppressedCount);
                    return SecurityAuditRateLimitDecision.Suppress();
                }

                state.Bucket.Tokens -= 1.0;
                _globalWriteTimestamps.Enqueue(now);

                long suppressedCount = state.SuppressedCount;
                state.SuppressedCount = 0;

                return SecurityAuditRateLimitDecision.Write(suppressedCount);
            }
        }

        private PerKeyState GetOrCreateState(
            SecurityAuditRateLimitKey key,
            long now)
        {
            PerKeyState existing;
            if (_perKeyStates.TryGetValue(key, out existing))
            {
                existing.LastSeenTimestamp = now;
                _recency.Remove(existing.RecencyNode);
                _recency.AddLast(existing.RecencyNode);
                return existing;
            }

            if (_perKeyStates.Count >= MaximumTrackedKeys)
            {
                RemoveOldestState();
            }

            LinkedListNode<SecurityAuditRateLimitKey> recencyNode =
                _recency.AddLast(key);
            var created = new PerKeyState(
                PerKeyBurstCapacity,
                now,
                recencyNode);
            _perKeyStates.Add(key, created);
            return created;
        }

        private void RemoveStaleStates(long now)
        {
            while (_recency.First != null)
            {
                SecurityAuditRateLimitKey oldestKey = _recency.First.Value;
                PerKeyState oldestState = _perKeyStates[oldestKey];
                if (!HasElapsed(
                    oldestState.LastSeenTimestamp,
                    now,
                    StaleEntryMinutes * 60.0))
                {
                    return;
                }

                RemoveOldestState();
            }
        }

        private void RemoveOldestState()
        {
            LinkedListNode<SecurityAuditRateLimitKey> oldest = _recency.First;
            if (oldest == null)
            {
                throw new InvalidOperationException(
                    "The security audit rate limiter has no state to evict.");
            }

            _perKeyStates.Remove(oldest.Value);
            _recency.RemoveFirst();
        }

        private long GetMonotonicTimestamp()
        {
            long timestamp = _timestampProvider();
            if (timestamp < 0)
            {
                throw new InvalidOperationException(
                    "The monotonic timestamp provider returned a negative value.");
            }

            if (timestamp < _lastObservedTimestamp)
            {
                return _lastObservedTimestamp;
            }

            _lastObservedTimestamp = timestamp;
            return timestamp;
        }

        private void RemoveExpiredGlobalWrites(long now)
        {
            while (_globalWriteTimestamps.Count > 0
                && HasElapsed(
                    _globalWriteTimestamps.Peek(),
                    now,
                    60.0))
            {
                _globalWriteTimestamps.Dequeue();
            }
        }

        private void Refill(
            TokenBucket bucket,
            long now,
            double capacity,
            double tokensPerSecond)
        {
            double elapsedSeconds = GetElapsedSeconds(
                bucket.LastRefillTimestamp,
                now);
            if (elapsedSeconds <= 0)
            {
                return;
            }

            bucket.Tokens = Math.Min(
                capacity,
                bucket.Tokens + (elapsedSeconds * tokensPerSecond));
            bucket.LastRefillTimestamp = now;
        }

        private bool HasElapsed(
            long startTimestamp,
            long endTimestamp,
            double requiredSeconds)
        {
            return GetElapsedSeconds(startTimestamp, endTimestamp)
                >= requiredSeconds;
        }

        private double GetElapsedSeconds(
            long startTimestamp,
            long endTimestamp)
        {
            if (endTimestamp <= startTimestamp)
            {
                return 0;
            }

            double elapsedTicks = (double)endTimestamp - startTimestamp;
            return elapsedTicks / _timestampFrequency;
        }

        private static long SaturatingIncrement(long value)
        {
            return value == long.MaxValue ? long.MaxValue : value + 1;
        }

        private sealed class PerKeyState
        {
            public PerKeyState(
                double initialTokens,
                long initialTimestamp,
                LinkedListNode<SecurityAuditRateLimitKey> recencyNode)
            {
                if (recencyNode == null)
                {
                    throw new ArgumentNullException(nameof(recencyNode));
                }

                Bucket = new TokenBucket(initialTokens, initialTimestamp);
                LastSeenTimestamp = initialTimestamp;
                RecencyNode = recencyNode;
            }

            public TokenBucket Bucket { get; }

            public long LastSeenTimestamp { get; set; }

            public long SuppressedCount { get; set; }

            public LinkedListNode<SecurityAuditRateLimitKey> RecencyNode
            {
                get;
            }
        }

        private sealed class TokenBucket
        {
            public TokenBucket(double initialTokens, long initialTimestamp)
            {
                Tokens = initialTokens;
                LastRefillTimestamp = initialTimestamp;
            }

            public double Tokens { get; set; }

            public long LastRefillTimestamp { get; set; }
        }
    }
}
