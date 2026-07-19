using System;
using System.Collections.Generic;
using System.Diagnostics;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PeerInboundOperation
    {
        Handshake = 1,
        Exchange = 2,
        Release = 3,
        Revoke = 4
    }

    internal sealed class PeerRateLimitDecision
    {
        private PeerRateLimitDecision(
            bool isAllowed,
            bool isConfigured,
            int? retryAfterSeconds)
        {
            if (!isAllowed
                && (!isConfigured || !retryAfterSeconds.HasValue))
            {
                throw new ArgumentException(
                    "A denied Peer rate decision needs a configured retry time.");
            }

            if (isAllowed && retryAfterSeconds.HasValue)
            {
                throw new ArgumentException(
                    "An allowed Peer rate decision cannot have a retry time.");
            }

            if (retryAfterSeconds.HasValue
                && retryAfterSeconds.Value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retryAfterSeconds));
            }

            IsAllowed = isAllowed;
            IsConfigured = isConfigured;
            RetryAfterSeconds = retryAfterSeconds;
        }

        public bool IsAllowed { get; }

        // Release and revoke have no approved numeric limit in the current
        // contract. Their allowed decision is deliberately distinguishable
        // from an operation that passed an implemented rate policy.
        public bool IsConfigured { get; }

        public int? RetryAfterSeconds { get; }

        internal static PeerRateLimitDecision Allowed(bool isConfigured)
        {
            return new PeerRateLimitDecision(true, isConfigured, null);
        }

        internal static PeerRateLimitDecision Denied(
            int retryAfterSeconds)
        {
            return new PeerRateLimitDecision(
                false,
                true,
                retryAfterSeconds);
        }
    }

    // One limiter is bound to one trusted configured endpoint and peer
    // identity. This makes the authenticated rate key
    // (canonical endpoint, InstanceId) explicit and prevents an untrusted
    // X-DPAI-Instance-Id value from selecting another bucket.
    internal sealed class PeerRequestRateLimiter
    {
        internal const int HandshakeRequestsPerMinute = 12;
        internal const int HandshakeBurstCapacity = 3;
        internal const int ExchangeRequestsPerMinute = 30;

        private const double WindowSeconds = 60.0;
        private const double HandshakeTokensPerSecond =
            (double)HandshakeRequestsPerMinute / WindowSeconds;

        private readonly object _syncRoot = new object();
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;
        private readonly Queue<long> _exchangeRequests = new Queue<long>();
        private long _lastObservedTimestamp;
        private long _lastHandshakeRefillTimestamp;
        private double _handshakeTokens;

        public PeerRequestRateLimiter(
            string peerEndpoint,
            Guid peerInstanceId)
            : this(
                peerEndpoint,
                peerInstanceId,
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal PeerRequestRateLimiter(
            string peerEndpoint,
            Guid peerInstanceId,
            Func<long> timestampProvider,
            long timestampFrequency)
        {
            string canonicalEndpoint;
            if (!AdminPeerEndpoint.TryNormalize(
                    peerEndpoint,
                    out canonicalEndpoint)
                || !StringComparer.Ordinal.Equals(
                    peerEndpoint,
                    canonicalEndpoint))
            {
                throw new ArgumentException(
                    "The trusted peer endpoint must be canonical.",
                    nameof(peerEndpoint));
            }

            if (peerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The trusted peer instance ID must not be empty.",
                    nameof(peerInstanceId));
            }

            if (timestampProvider == null)
            {
                throw new ArgumentNullException(nameof(timestampProvider));
            }

            if (timestampFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampFrequency));
            }

            long initialTimestamp = timestampProvider();
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider),
                    "The monotonic timestamp provider returned a negative value.");
            }

            PeerEndpoint = canonicalEndpoint;
            PeerInstanceId = peerInstanceId;
            _timestampProvider = timestampProvider;
            _timestampFrequency = timestampFrequency;
            _lastObservedTimestamp = initialTimestamp;
            _lastHandshakeRefillTimestamp = initialTimestamp;
            _handshakeTokens = HandshakeBurstCapacity;
        }

        public string PeerEndpoint { get; }

        public Guid PeerInstanceId { get; }

        public PeerRateLimitDecision TryAcquire(
            PeerInboundOperation operation)
        {
            if (!Enum.IsDefined(typeof(PeerInboundOperation), operation))
            {
                throw new ArgumentOutOfRangeException(nameof(operation));
            }

            lock (_syncRoot)
            {
                long now = GetMonotonicTimestamp();
                switch (operation)
                {
                    case PeerInboundOperation.Handshake:
                        return TryAcquireHandshake(now);
                    case PeerInboundOperation.Exchange:
                        return TryAcquireExchange(now);
                    case PeerInboundOperation.Release:
                    case PeerInboundOperation.Revoke:
                        return PeerRateLimitDecision.Allowed(false);
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(operation));
                }
            }
        }

        private PeerRateLimitDecision TryAcquireHandshake(long now)
        {
            RefillHandshakeTokens(now);
            if (_handshakeTokens >= 1.0)
            {
                _handshakeTokens -= 1.0;
                return PeerRateLimitDecision.Allowed(true);
            }

            double waitSeconds =
                (1.0 - _handshakeTokens) / HandshakeTokensPerSecond;
            return PeerRateLimitDecision.Denied(
                ToRetryAfterSeconds(waitSeconds));
        }

        private PeerRateLimitDecision TryAcquireExchange(long now)
        {
            while (_exchangeRequests.Count != 0
                && ElapsedSeconds(_exchangeRequests.Peek(), now)
                    >= WindowSeconds)
            {
                _exchangeRequests.Dequeue();
            }

            if (_exchangeRequests.Count < ExchangeRequestsPerMinute)
            {
                _exchangeRequests.Enqueue(now);
                return PeerRateLimitDecision.Allowed(true);
            }

            double waitSeconds = WindowSeconds
                - ElapsedSeconds(_exchangeRequests.Peek(), now);
            return PeerRateLimitDecision.Denied(
                ToRetryAfterSeconds(waitSeconds));
        }

        private void RefillHandshakeTokens(long now)
        {
            double elapsedSeconds = ElapsedSeconds(
                _lastHandshakeRefillTimestamp,
                now);
            _handshakeTokens = Math.Min(
                HandshakeBurstCapacity,
                _handshakeTokens
                    + (elapsedSeconds * HandshakeTokensPerSecond));
            _lastHandshakeRefillTimestamp = now;
        }

        private double ElapsedSeconds(long earlier, long later)
        {
            if (later <= earlier)
            {
                return 0.0;
            }

            return (double)(later - earlier) / _timestampFrequency;
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

        private static int ToRetryAfterSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || seconds <= 0.0)
            {
                return 1;
            }

            double rounded = Math.Ceiling(seconds);
            return rounded >= int.MaxValue
                ? int.MaxValue
                : Math.Max(1, (int)rounded);
        }
    }
}
