using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PeerNonceRegistrationResult
    {
        Accepted = 0,
        ReplayDetected = 1,
        CapacityExceeded = 2
    }

    internal sealed class PeerNonceReplayCache
    {
        private readonly int _maximumEntries;
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, long> _expirationByKey =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private long _lastObservedTimestamp;

        public PeerNonceReplayCache()
            : this(
                PeerAuthenticationContract.MaximumReplayCacheEntries,
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal PeerNonceReplayCache(
            int maximumEntries,
            Func<long> timestampProvider,
            long timestampFrequency)
        {
            if (maximumEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumEntries),
                    maximumEntries,
                    "The replay cache capacity must be positive.");
            }

            if (timestampProvider == null)
            {
                throw new ArgumentNullException(nameof(timestampProvider));
            }

            if (timestampFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampFrequency),
                    timestampFrequency,
                    "The timestamp frequency must be positive.");
            }

            long initialTimestamp = timestampProvider();
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider),
                    "The timestamp provider returned a negative value.");
            }

            _maximumEntries = maximumEntries;
            _timestampProvider = timestampProvider;
            _timestampFrequency = timestampFrequency;
            _lastObservedTimestamp = initialTimestamp;
        }

        public int Count
        {
            get
            {
                lock (_syncRoot)
                {
                    RemoveExpired(GetMonotonicTimestamp());
                    return _expirationByKey.Count;
                }
            }
        }

        public PeerNonceRegistrationResult RegisterNonSession(
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] nonce)
        {
            return Register(
                peerInstanceId,
                keyEpoch,
                null,
                nonce,
                TimeSpan.FromMinutes(
                    PeerAuthenticationContract
                        .NonSessionReplayRetentionMinutes));
        }

        public PeerNonceRegistrationResult RegisterSession(
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            byte[] nonce,
            TimeSpan remainingSessionLifetime)
        {
            PeerAuthenticationContract.ValidateExactLength(
                sessionId,
                nameof(sessionId),
                PeerAuthenticationContract.SessionIdLength);
            if (remainingSessionLifetime <= TimeSpan.Zero
                || remainingSessionLifetime
                    > TimeSpan.FromMinutes(
                        PeerAuthenticationContract.SessionLifetimeMinutes))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingSessionLifetime),
                    remainingSessionLifetime,
                    "The remaining session lifetime must be positive and no more than 10 minutes.");
            }

            return Register(
                peerInstanceId,
                keyEpoch,
                sessionId,
                nonce,
                remainingSessionLifetime);
        }

        private PeerNonceRegistrationResult Register(
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            byte[] nonce,
            TimeSpan retention)
        {
            if (peerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The peer instance ID must not be empty.",
                    nameof(peerInstanceId));
            }

            if (keyEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyEpoch),
                    keyEpoch,
                    "The key epoch must be positive.");
            }

            PeerAuthenticationContract.ValidateExactLength(
                nonce,
                nameof(nonce),
                PeerAuthenticationContract.NonceLength);

            string key = CreateKey(
                peerInstanceId,
                keyEpoch,
                sessionId,
                nonce);
            lock (_syncRoot)
            {
                long now = GetMonotonicTimestamp();
                RemoveExpired(now);

                if (_expirationByKey.ContainsKey(key))
                {
                    return PeerNonceRegistrationResult.ReplayDetected;
                }

                if (_expirationByKey.Count >= _maximumEntries)
                {
                    return PeerNonceRegistrationResult.CapacityExceeded;
                }

                _expirationByKey.Add(
                    key,
                    GetExpirationTimestamp(now, retention));
                return PeerNonceRegistrationResult.Accepted;
            }
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

        private long GetExpirationTimestamp(long now, TimeSpan retention)
        {
            double durationTicks = Math.Ceiling(
                retention.TotalSeconds * _timestampFrequency);
            if (durationTicks >= long.MaxValue - now)
            {
                return long.MaxValue;
            }

            return now + (long)durationTicks;
        }

        private void RemoveExpired(long now)
        {
            List<string> expiredKeys = null;
            foreach (KeyValuePair<string, long> entry in _expirationByKey)
            {
                if (entry.Value > now)
                {
                    continue;
                }

                if (expiredKeys == null)
                {
                    expiredKeys = new List<string>();
                }

                expiredKeys.Add(entry.Key);
            }

            if (expiredKeys == null)
            {
                return;
            }

            for (int index = 0; index < expiredKeys.Count; index++)
            {
                _expirationByKey.Remove(expiredKeys[index]);
            }
        }

        private static string CreateKey(
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            byte[] nonce)
        {
            return peerInstanceId.ToString("D").ToLowerInvariant()
                + "|"
                + keyEpoch.ToString(CultureInfo.InvariantCulture)
                + "|"
                + (sessionId == null
                    ? string.Empty
                    : Convert.ToBase64String(sessionId))
                + "|"
                + Convert.ToBase64String(nonce);
        }
    }
}
