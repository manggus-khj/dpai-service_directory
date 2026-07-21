using System;
using System.Collections.Generic;
using System.Diagnostics;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal enum RenewalNonceRegistrationStatus
    {
        Accepted = 1,
        ReplayDetected = 2,
        CapacityExceeded = 3
    }

    internal sealed class RenewalNonceReplayCache
    {
        internal const int RetentionSeconds = 10 * 60;
        internal const int MaximumEntries = 32768;

        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _expiresByKey =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Func<long> _timestampProvider;
        private readonly long _retentionTicks;
        private long _lastObservedTimestamp;

        internal RenewalNonceReplayCache()
            : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
        {
        }

        internal RenewalNonceReplayCache(
            Func<long> timestampProvider,
            long timestampFrequency)
        {
            _timestampProvider = timestampProvider
                ?? throw new ArgumentNullException(nameof(timestampProvider));
            if (timestampFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampFrequency));
            }

            long initialTimestamp = timestampProvider();
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider));
            }

            double retentionTicks =
                timestampFrequency * (double)RetentionSeconds;
            if (retentionTicks > long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampFrequency));
            }

            _retentionTicks = Math.Max(
                1L,
                (long)Math.Ceiling(retentionTicks));
            _lastObservedTimestamp = initialTimestamp;
        }

        internal RenewalNonceRegistrationStatus Register(
            CertificateSerialNumber serialNumber,
            byte[] nonce)
        {
            if (!serialNumber.IsValid)
            {
                throw new ArgumentException(
                    "The renewal serial number must be valid.",
                    nameof(serialNumber));
            }

            if (nonce == null
                || nonce.Length != ExternalApiContract.RenewalNonceBytes)
            {
                throw new ArgumentException(
                    "The renewal nonce must contain exactly 16 bytes.",
                    nameof(nonce));
            }

            string key = serialNumber.Hex
                + "|"
                + Convert.ToBase64String(nonce);
            lock (_gate)
            {
                long now = ReadTimestamp();
                RemoveExpired(now);
                if (_expiresByKey.ContainsKey(key))
                {
                    return RenewalNonceRegistrationStatus.ReplayDetected;
                }

                if (_expiresByKey.Count >= MaximumEntries)
                {
                    return RenewalNonceRegistrationStatus.CapacityExceeded;
                }

                long expires = now > long.MaxValue - _retentionTicks
                    ? long.MaxValue
                    : now + _retentionTicks;
                _expiresByKey.Add(key, expires);
                return RenewalNonceRegistrationStatus.Accepted;
            }
        }

        private long ReadTimestamp()
        {
            long value = _timestampProvider();
            if (value < 0)
            {
                throw new InvalidOperationException(
                    "The renewal nonce clock returned a negative value.");
            }

            if (value < _lastObservedTimestamp)
            {
                return _lastObservedTimestamp;
            }

            _lastObservedTimestamp = value;
            return value;
        }

        private void RemoveExpired(long now)
        {
            var expired = new List<string>();
            foreach (KeyValuePair<string, long> pair in _expiresByKey)
            {
                if (pair.Value <= now)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (string key in expired)
            {
                _expiresByKey.Remove(key);
            }
        }
    }
}
