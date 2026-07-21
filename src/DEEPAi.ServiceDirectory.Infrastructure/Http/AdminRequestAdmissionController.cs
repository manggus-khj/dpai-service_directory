using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal enum AdminHttpOperation
    {
        Undefined = 0,
        GetServices = 1,
        GetRegistrationMode = 2,
        OpenRegistrationMode = 3,
        CloseRegistrationMode = 4,
        DeleteService = 5,
        GetSyncStatus = 6,
        EnableSync = 7,
        ConfirmPairing = 8,
        CancelPairing = 9,
        DisableSync = 10,
        SynchronizeNow = 11,
        GetLoggingSettings = 12,
        PutLoggingSettings = 13,
        GetCaStatus = 14,
        CreateCaBackup = 15,
        GetCertificates = 16,
        RevokeCertificate = 17,
        GetCaRotation = 18,
        PrepareCaRotation = 19,
        CancelCaRotation = 20
    }

    internal sealed class AdminRequestAdmissionResult
    {
        private AdminRequestAdmissionResult(
            bool isGranted,
            AdminRequestAdmissionLease lease,
            int? retryAfterSeconds)
        {
            if (isGranted
                ? lease == null || retryAfterSeconds.HasValue
                : lease != null)
            {
                throw new ArgumentException(
                    "An Admin admission result must be granted or denied.");
            }

            if (retryAfterSeconds.HasValue
                && retryAfterSeconds.Value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retryAfterSeconds));
            }

            IsGranted = isGranted;
            Lease = lease;
            RetryAfterSeconds = retryAfterSeconds;
        }

        public bool IsGranted { get; }

        public AdminRequestAdmissionLease Lease { get; }

        public int? RetryAfterSeconds { get; }

        public static AdminRequestAdmissionResult Granted(
            AdminRequestAdmissionLease lease)
        {
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            return new AdminRequestAdmissionResult(true, lease, null);
        }

        public static AdminRequestAdmissionResult Denied(
            int? retryAfterSeconds)
        {
            return new AdminRequestAdmissionResult(
                false,
                null,
                retryAfterSeconds);
        }
    }

    internal sealed class AdminRequestAdmissionLease : IDisposable
    {
        private AdminRequestAdmissionController _owner;

        internal AdminRequestAdmissionLease(
            AdminRequestAdmissionController owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public void Dispose()
        {
            AdminRequestAdmissionController owner = Interlocked.Exchange(
                ref _owner,
                null);
            if (owner != null)
            {
                owner.Release();
            }
        }
    }

    internal sealed class AdminRequestAdmissionController
    {
        internal const int MaximumConcurrentRequests = 8;
        internal const int MaximumTrackedIdentities = 2048;
        internal const int StaleIdentityMinutes = 10;

        internal const int ReadRequestsPerMinute = 60;
        internal const int ChangeRequestsPerMinute = 10;
        internal const int SyncNowRequestsPerMinute = 2;

        private const double WindowSeconds = 60.0;
        private const double ReadBurstCapacity = 15.0;
        private const double ReadBurstTokensPerSecond = 1.0;

        private readonly object _syncRoot = new object();
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;
        private readonly Dictionary<string, IdentityState> _states =
            new Dictionary<string, IdentityState>(StringComparer.Ordinal);
        private long _lastObservedTimestamp;
        private int _activeRequests;

        public AdminRequestAdmissionController()
            : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
        {
        }

        internal AdminRequestAdmissionController(
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
                    nameof(timestampFrequency));
            }

            long initialTimestamp = timestampProvider();
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider));
            }

            _timestampProvider = timestampProvider;
            _timestampFrequency = timestampFrequency;
            _lastObservedTimestamp = initialTimestamp;
        }

        public AdminRequestAdmissionResult TryAcquire(
            AdminHttpOperation operation,
            SecurityIdentifier actorSid)
        {
            if (!Enum.IsDefined(typeof(AdminHttpOperation), operation)
                || operation == AdminHttpOperation.Undefined)
            {
                throw new ArgumentOutOfRangeException(nameof(operation));
            }

            if (actorSid == null)
            {
                throw new ArgumentNullException(nameof(actorSid));
            }

            string identityKey = actorSid.Value;
            lock (_syncRoot)
            {
                if (_activeRequests >= MaximumConcurrentRequests)
                {
                    return AdminRequestAdmissionResult.Denied(null);
                }

                long now = GetMonotonicTimestamp();
                RemoveStaleStates(now);

                IdentityState state;
                if (!_states.TryGetValue(identityKey, out state))
                {
                    if (_states.Count >= MaximumTrackedIdentities)
                    {
                        return AdminRequestAdmissionResult.Denied(null);
                    }

                    state = new IdentityState(now);
                    _states.Add(identityKey, state);
                }

                state.LastSeenTimestamp = now;
                bool isRead = IsRead(operation);
                bool isSyncNow =
                    operation == AdminHttpOperation.SynchronizeNow;
                PruneWindow(state.ReadRequests, now);
                PruneWindow(state.ChangeRequests, now);
                PruneWindow(state.SyncNowRequests, now);

                double waitSeconds;
                if (isRead)
                {
                    RefillReadBurst(state.ReadBurst, now);
                    waitSeconds = Math.Max(
                        SecondsUntilWindowSlot(
                            state.ReadRequests,
                            now,
                            ReadRequestsPerMinute),
                        SecondsUntilReadBurstToken(state.ReadBurst));
                }
                else
                {
                    waitSeconds = SecondsUntilWindowSlot(
                        state.ChangeRequests,
                        now,
                        ChangeRequestsPerMinute);
                    if (isSyncNow)
                    {
                        waitSeconds = Math.Max(
                            waitSeconds,
                            SecondsUntilWindowSlot(
                                state.SyncNowRequests,
                                now,
                                SyncNowRequestsPerMinute));
                    }
                }

                if (waitSeconds > 0)
                {
                    return AdminRequestAdmissionResult.Denied(
                        Math.Max(1, checked((int)Math.Ceiling(waitSeconds))));
                }

                if (isRead)
                {
                    state.ReadBurst.Tokens -= 1.0;
                    state.ReadRequests.Enqueue(now);
                }
                else
                {
                    state.ChangeRequests.Enqueue(now);
                    if (isSyncNow)
                    {
                        state.SyncNowRequests.Enqueue(now);
                    }
                }

                _activeRequests++;
                return AdminRequestAdmissionResult.Granted(
                    new AdminRequestAdmissionLease(this));
            }
        }

        internal void Release()
        {
            lock (_syncRoot)
            {
                if (_activeRequests <= 0)
                {
                    throw new InvalidOperationException(
                        "The Admin admission lease count is invalid.");
                }

                _activeRequests--;
            }
        }

        private static bool IsRead(AdminHttpOperation operation)
        {
            return operation == AdminHttpOperation.GetServices
                || operation == AdminHttpOperation.GetRegistrationMode
                || operation == AdminHttpOperation.GetSyncStatus
                || operation == AdminHttpOperation.GetLoggingSettings
                || operation == AdminHttpOperation.GetCaStatus
                || operation == AdminHttpOperation.GetCaRotation
                || operation == AdminHttpOperation.GetCertificates;
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

        private void RemoveStaleStates(long now)
        {
            var staleKeys = new List<string>();
            foreach (KeyValuePair<string, IdentityState> pair in _states)
            {
                if (GetElapsedSeconds(
                    pair.Value.LastSeenTimestamp,
                    now) >= StaleIdentityMinutes * 60.0)
                {
                    staleKeys.Add(pair.Key);
                }
            }

            for (int index = 0; index < staleKeys.Count; index++)
            {
                _states.Remove(staleKeys[index]);
            }
        }

        private void RefillReadBurst(
            TokenBucket bucket,
            long now)
        {
            double elapsedSeconds = GetElapsedSeconds(
                bucket.LastRefillTimestamp,
                now);
            if (elapsedSeconds <= 0)
            {
                return;
            }

            bucket.Tokens = Math.Min(
                ReadBurstCapacity,
                bucket.Tokens
                    + (elapsedSeconds * ReadBurstTokensPerSecond));
            bucket.LastRefillTimestamp = now;
        }

        private double GetElapsedSeconds(long start, long end)
        {
            if (end <= start)
            {
                return 0;
            }

            return ((double)end - start) / _timestampFrequency;
        }

        private void PruneWindow(Queue<long> requests, long now)
        {
            while (requests.Count > 0
                && GetElapsedSeconds(requests.Peek(), now)
                    >= WindowSeconds)
            {
                requests.Dequeue();
            }
        }

        private double SecondsUntilWindowSlot(
            Queue<long> requests,
            long now,
            int maximumRequests)
        {
            if (requests.Count < maximumRequests)
            {
                return 0;
            }

            double elapsed = GetElapsedSeconds(requests.Peek(), now);
            return Math.Max(0, WindowSeconds - elapsed);
        }

        private static double SecondsUntilReadBurstToken(
            TokenBucket bucket)
        {
            return bucket.Tokens >= 1.0
                ? 0
                : (1.0 - bucket.Tokens) / ReadBurstTokensPerSecond;
        }

        private sealed class IdentityState
        {
            public IdentityState(long initialTimestamp)
            {
                ReadBurst = new TokenBucket(
                    ReadBurstCapacity,
                    initialTimestamp);
                ReadRequests = new Queue<long>();
                ChangeRequests = new Queue<long>();
                SyncNowRequests = new Queue<long>();
                LastSeenTimestamp = initialTimestamp;
            }

            public TokenBucket ReadBurst { get; }

            public Queue<long> ReadRequests { get; }

            public Queue<long> ChangeRequests { get; }

            public Queue<long> SyncNowRequests { get; }

            public long LastSeenTimestamp { get; set; }
        }

        private sealed class TokenBucket
        {
            public TokenBucket(double tokens, long timestamp)
            {
                Tokens = tokens;
                LastRefillTimestamp = timestamp;
            }

            public double Tokens { get; set; }

            public long LastRefillTimestamp { get; set; }
        }
    }
}
