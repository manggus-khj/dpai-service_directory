using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal enum ExternalHttpEndpoint
    {
        Undefined = 0,
        Health = 1,
        Services = 2,
        Registration = 3
    }

    internal enum ExternalRequestAdmissionFailure
    {
        None = 0,
        ConcurrencyLimit = 1,
        RateLimit = 2,
        TrackingCapacity = 3
    }

    internal sealed class ExternalRequestAdmissionResult
    {
        private ExternalRequestAdmissionResult(
            IDisposable lease,
            ExternalRequestAdmissionFailure failure,
            int? retryAfterSeconds)
        {
            bool granted = lease != null;
            if (granted)
            {
                if (failure != ExternalRequestAdmissionFailure.None
                    || retryAfterSeconds.HasValue)
                {
                    throw new ArgumentException(
                        "A granted admission result cannot contain failure metadata.");
                }
            }
            else if (failure == ExternalRequestAdmissionFailure.None
                || !Enum.IsDefined(
                    typeof(ExternalRequestAdmissionFailure),
                    failure)
                || (failure == ExternalRequestAdmissionFailure.RateLimit
                    && (!retryAfterSeconds.HasValue
                        || retryAfterSeconds.Value < 1))
                || (failure != ExternalRequestAdmissionFailure.RateLimit
                    && retryAfterSeconds.HasValue))
            {
                throw new ArgumentException(
                    "A denied admission result contains invalid failure metadata.");
            }

            Lease = lease;
            Failure = failure;
            RetryAfterSeconds = retryAfterSeconds;
        }

        public bool IsGranted => Lease != null;

        public IDisposable Lease { get; }

        public ExternalRequestAdmissionFailure Failure { get; }

        public int? RetryAfterSeconds { get; }

        public static ExternalRequestAdmissionResult Granted(
            IDisposable lease)
        {
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            return new ExternalRequestAdmissionResult(
                lease,
                ExternalRequestAdmissionFailure.None,
                null);
        }

        public static ExternalRequestAdmissionResult Denied(
            ExternalRequestAdmissionFailure failure,
            int? retryAfterSeconds = null)
        {
            return new ExternalRequestAdmissionResult(
                null,
                failure,
                retryAfterSeconds);
        }
    }

    internal sealed class ExternalRequestAdmissionController
    {
        internal const int MaximumTrackedProductCodeKeys = 4096;
        internal const int MaximumTrackedRemoteAddressKeys = 4096;
        internal const int IdleCleanupSeconds = 120;

        internal const double HealthCombinationCapacity = 5.0;
        internal const double HealthCombinationTokensPerMinute = 30.0;

        // "No separate burst" is fixed as a one-token capacity. This keeps
        // the documented per-minute rates without permitting a startup burst.
        internal const double ServiceProductCodeCapacity = 1.0;
        internal const double ServiceProductCodeTokensPerMinute = 12.0;
        internal const double ServiceRemoteAddressCapacity = 1.0;
        internal const double ServiceRemoteAddressTokensPerMinute = 60.0;

        internal const double RegistrationProductCodeCapacity = 2.0;
        internal const double RegistrationProductCodeTokensPerMinute = 3.0;
        internal const double RegistrationRemoteAddressCapacity = 20.0;
        internal const double RegistrationRemoteAddressTokensPerMinute = 20.0;

        private readonly ExternalRequestConcurrencyLimiter
            _concurrencyLimiter;
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;
        private readonly object _rateGate = new object();

        // Endpoint is part of every key, while the two maps enforce the
        // documented aggregate 4,096-key caps across all External endpoints.
        private readonly Dictionary<string, TokenBucketState>
            _productCodeStates = new Dictionary<string, TokenBucketState>(
                StringComparer.Ordinal);
        private readonly Dictionary<string, TokenBucketState>
            _remoteAddressStates = new Dictionary<string, TokenBucketState>(
                StringComparer.Ordinal);

        private long _lastObservedTimestamp;

        internal ExternalRequestAdmissionController(
            ExternalRequestConcurrencyLimiter concurrencyLimiter)
            : this(
                concurrencyLimiter,
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal ExternalRequestAdmissionController(
            ExternalRequestConcurrencyLimiter concurrencyLimiter,
            Func<long> timestampProvider,
            long timestampFrequency)
        {
            _concurrencyLimiter = concurrencyLimiter
                ?? throw new ArgumentNullException(nameof(concurrencyLimiter));
            _timestampProvider = timestampProvider
                ?? throw new ArgumentNullException(nameof(timestampProvider));
            if (timestampFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampFrequency),
                    "The monotonic timestamp frequency must be positive.");
            }

            long initialTimestamp = timestampProvider();
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider),
                    "The monotonic timestamp provider returned a negative value.");
            }

            _timestampFrequency = timestampFrequency;
            _lastObservedTimestamp = initialTimestamp;
        }

        internal int TrackedProductCodeKeyCount
        {
            get
            {
                lock (_rateGate)
                {
                    return _productCodeStates.Count;
                }
            }
        }

        internal int TrackedRemoteAddressKeyCount
        {
            get
            {
                lock (_rateGate)
                {
                    return _remoteAddressStates.Count;
                }
            }
        }

        internal ExternalRequestAdmissionResult TryAcquire(
            ExternalHttpEndpoint endpoint,
            ProductCode productCode,
            IPAddress remoteAddress)
        {
            if (!Enum.IsDefined(typeof(ExternalHttpEndpoint), endpoint))
            {
                throw new ArgumentOutOfRangeException(nameof(endpoint));
            }

            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "A valid authenticated ProductCode is required.",
                    nameof(productCode));
            }

            string canonicalRemoteAddress = GetCanonicalAddress(remoteAddress);

            IDisposable concurrencyLease;
            if (!_concurrencyLimiter.TryAcquire(out concurrencyLease))
            {
                return ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.ConcurrencyLimit);
            }

            try
            {
                if (endpoint == ExternalHttpEndpoint.Undefined)
                {
                    return ExternalRequestAdmissionResult.Granted(
                        concurrencyLease);
                }

                ExternalRequestAdmissionResult rateResult;
                lock (_rateGate)
                {
                    rateResult = TryAcquireRateTokens(
                        endpoint,
                        productCode.Value,
                        canonicalRemoteAddress);
                }

                if (rateResult != null)
                {
                    concurrencyLease.Dispose();
                    return rateResult;
                }

                return ExternalRequestAdmissionResult.Granted(
                    concurrencyLease);
            }
            catch
            {
                concurrencyLease.Dispose();
                throw;
            }
        }

        private ExternalRequestAdmissionResult TryAcquireRateTokens(
            ExternalHttpEndpoint endpoint,
            string productCode,
            string remoteAddress)
        {
            long now = GetMonotonicTimestamp();
            BucketRequirement productRequirement;
            BucketRequirement remoteRequirement;
            GetRequirements(
                endpoint,
                productCode,
                remoteAddress,
                out productRequirement,
                out remoteRequirement);

            if (!EnsureCapacityForNewKey(
                    _productCodeStates,
                    productRequirement.Key,
                    now,
                    MaximumTrackedProductCodeKeys)
                || (remoteRequirement != null
                    && !EnsureCapacityForNewKey(
                        _remoteAddressStates,
                        remoteRequirement.Key,
                        now,
                        MaximumTrackedRemoteAddressKeys)))
            {
                return ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.TrackingCapacity);
            }

            TokenBucketState productState = GetOrCreateState(
                _productCodeStates,
                productRequirement,
                now);
            TokenBucketState remoteState = remoteRequirement == null
                ? null
                : GetOrCreateState(
                    _remoteAddressStates,
                    remoteRequirement,
                    now);

            Refill(productState, now);
            productState.LastSeenTimestamp = now;
            double retrySeconds = SecondsUntilToken(productState);

            if (remoteState != null)
            {
                Refill(remoteState, now);
                remoteState.LastSeenTimestamp = now;
                retrySeconds = Math.Max(
                    retrySeconds,
                    SecondsUntilToken(remoteState));
            }

            if (retrySeconds > 0.0)
            {
                return ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.RateLimit,
                    ToRetryAfterSeconds(retrySeconds));
            }

            // Tokens are consumed only after every required bucket is known
            // to allow the request.
            productState.Tokens -= 1.0;
            if (remoteState != null)
            {
                remoteState.Tokens -= 1.0;
            }

            return null;
        }

        private static void GetRequirements(
            ExternalHttpEndpoint endpoint,
            string productCode,
            string remoteAddress,
            out BucketRequirement productRequirement,
            out BucketRequirement remoteRequirement)
        {
            switch (endpoint)
            {
                case ExternalHttpEndpoint.Health:
                    productRequirement = new BucketRequirement(
                        "H|" + productCode + "|" + remoteAddress,
                        HealthCombinationCapacity,
                        HealthCombinationTokensPerMinute);
                    remoteRequirement = null;
                    return;

                case ExternalHttpEndpoint.Services:
                    productRequirement = new BucketRequirement(
                        "S|" + productCode,
                        ServiceProductCodeCapacity,
                        ServiceProductCodeTokensPerMinute);
                    remoteRequirement = new BucketRequirement(
                        "S|" + remoteAddress,
                        ServiceRemoteAddressCapacity,
                        ServiceRemoteAddressTokensPerMinute);
                    return;

                case ExternalHttpEndpoint.Registration:
                    productRequirement = new BucketRequirement(
                        "R|" + productCode,
                        RegistrationProductCodeCapacity,
                        RegistrationProductCodeTokensPerMinute);
                    remoteRequirement = new BucketRequirement(
                        "R|" + remoteAddress,
                        RegistrationRemoteAddressCapacity,
                        RegistrationRemoteAddressTokensPerMinute);
                    return;

                default:
                    throw new ArgumentOutOfRangeException(nameof(endpoint));
            }
        }

        private bool EnsureCapacityForNewKey(
            Dictionary<string, TokenBucketState> states,
            string key,
            long now,
            int maximumCount)
        {
            if (states.ContainsKey(key) || states.Count < maximumCount)
            {
                return true;
            }

            var removableKeys = new List<string>();
            foreach (KeyValuePair<string, TokenBucketState> pair in states)
            {
                TokenBucketState state = pair.Value;
                Refill(state, now);
                if (state.Tokens >= state.Capacity
                    && HasElapsed(
                        state.LastSeenTimestamp,
                        now,
                        IdleCleanupSeconds))
                {
                    removableKeys.Add(pair.Key);
                }
            }

            foreach (string removableKey in removableKeys)
            {
                states.Remove(removableKey);
                if (states.Count < maximumCount)
                {
                    break;
                }
            }

            return states.Count < maximumCount;
        }

        private static TokenBucketState GetOrCreateState(
            IDictionary<string, TokenBucketState> states,
            BucketRequirement requirement,
            long now)
        {
            TokenBucketState state;
            if (states.TryGetValue(requirement.Key, out state))
            {
                return state;
            }

            state = new TokenBucketState(
                requirement.Capacity,
                requirement.TokensPerMinute / 60.0,
                now);
            states.Add(requirement.Key, state);
            return state;
        }

        private void Refill(TokenBucketState state, long now)
        {
            double elapsedSeconds = GetElapsedSeconds(
                state.LastRefillTimestamp,
                now);
            if (elapsedSeconds <= 0.0)
            {
                return;
            }

            state.Tokens = Math.Min(
                state.Capacity,
                state.Tokens + (elapsedSeconds * state.TokensPerSecond));
            state.LastRefillTimestamp = now;
        }

        private static double SecondsUntilToken(TokenBucketState state)
        {
            return state.Tokens >= 1.0
                ? 0.0
                : (1.0 - state.Tokens) / state.TokensPerSecond;
        }

        private static int ToRetryAfterSeconds(double seconds)
        {
            double rounded = Math.Ceiling(seconds);
            if (rounded < 1.0)
            {
                return 1;
            }

            return rounded >= int.MaxValue
                ? int.MaxValue
                : (int)rounded;
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
                return 0.0;
            }

            return ((double)endTimestamp - startTimestamp)
                / _timestampFrequency;
        }

        private static string GetCanonicalAddress(IPAddress address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return new IPAddress(bytes).ToString();
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return new IPAddress(bytes, address.ScopeId)
                    .ToString()
                    .ToLowerInvariant();
            }

            throw new ArgumentException(
                "The remote address must be IPv4 or IPv6.",
                nameof(address));
        }

        private sealed class BucketRequirement
        {
            public BucketRequirement(
                string key,
                double capacity,
                double tokensPerMinute)
            {
                Key = key;
                Capacity = capacity;
                TokensPerMinute = tokensPerMinute;
            }

            public string Key { get; }

            public double Capacity { get; }

            public double TokensPerMinute { get; }
        }

        private sealed class TokenBucketState
        {
            public TokenBucketState(
                double capacity,
                double tokensPerSecond,
                long now)
            {
                Capacity = capacity;
                TokensPerSecond = tokensPerSecond;
                Tokens = capacity;
                LastRefillTimestamp = now;
                LastSeenTimestamp = now;
            }

            public double Capacity { get; }

            public double TokensPerSecond { get; }

            public double Tokens { get; set; }

            public long LastRefillTimestamp { get; set; }

            public long LastSeenTimestamp { get; set; }
        }
    }
}
