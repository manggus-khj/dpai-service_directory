using System;
using System.Threading;
using DEEPAi.ServiceDirectory.Application.State;

namespace DEEPAi.ServiceDirectory.Application.Registration
{
    public enum RegistrationModeState
    {
        Closed = 1,
        Open = 2,
        Claimed = 3
    }

    public enum RegistrationModeClaimStatus
    {
        Claimed = 1,
        InvalidRequest = 2,
        Closed = 3,
        AlreadyClaimed = 4
    }

    public enum RegistrationModeCompletionOutcome
    {
        Registered = 1,
        Reregistered = 2,
        Failed = 3
    }

    public sealed class RegistrationModeLastResult
    {
        private RegistrationModeLastResult(
            DateTime completedUtc,
            RegistrationModeCompletionOutcome outcome,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            string certificateSerialNumber,
            DateTime? certificateNotAfterUtc,
            string failureReason)
        {
            CompletedUtc = completedUtc;
            Outcome = outcome;
            ProductCode = productCode;
            ServiceHostName = serviceHostName;
            ServiceIpv4Address = serviceIpv4Address;
            CertificateSerialNumber = certificateSerialNumber;
            CertificateNotAfterUtc = certificateNotAfterUtc;
            FailureReason = failureReason;
        }

        public DateTime CompletedUtc { get; }

        public RegistrationModeCompletionOutcome Outcome { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public string CertificateSerialNumber { get; }

        public DateTime? CertificateNotAfterUtc { get; }

        public string FailureReason { get; }

        public static RegistrationModeLastResult Success(
            DateTime completedUtc,
            RegistrationModeCompletionOutcome outcome,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            string certificateSerialNumber,
            DateTime certificateNotAfterUtc)
        {
            EnsureUtc(completedUtc, nameof(completedUtc));
            EnsureUtc(
                certificateNotAfterUtc,
                nameof(certificateNotAfterUtc));
            if (outcome != RegistrationModeCompletionOutcome.Registered
                && outcome !=
                    RegistrationModeCompletionOutcome.Reregistered)
            {
                throw new ArgumentOutOfRangeException(nameof(outcome));
            }

            if (string.IsNullOrWhiteSpace(productCode)
                || string.IsNullOrWhiteSpace(serviceHostName)
                || string.IsNullOrWhiteSpace(serviceIpv4Address)
                || string.IsNullOrWhiteSpace(certificateSerialNumber)
                || certificateNotAfterUtc <= completedUtc)
            {
                throw new ArgumentException(
                    "A successful registration result is incomplete.");
            }

            return new RegistrationModeLastResult(
                completedUtc,
                outcome,
                productCode,
                serviceHostName,
                serviceIpv4Address,
                certificateSerialNumber,
                certificateNotAfterUtc,
                null);
        }

        public static RegistrationModeLastResult Failure(
            DateTime completedUtc,
            string failureReason)
        {
            EnsureUtc(completedUtc, nameof(completedUtc));
            if (string.IsNullOrWhiteSpace(failureReason))
            {
                throw new ArgumentException(
                    "A safe registration failure reason is required.",
                    nameof(failureReason));
            }

            return new RegistrationModeLastResult(
                completedUtc,
                RegistrationModeCompletionOutcome.Failed,
                null,
                null,
                null,
                null,
                null,
                failureReason.Trim());
        }

        private static void EnsureUtc(
            DateTime value,
            string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Registration completion timestamps must use UTC.",
                    parameterName);
            }
        }
    }

    public sealed class RegistrationModeSnapshot
    {
        internal RegistrationModeSnapshot(
            RegistrationModeState state,
            DateTime? openedUtc,
            DateTime? expiresUtc,
            int? remainingSeconds,
            RegistrationModeLastResult lastResult)
        {
            State = state;
            OpenedUtc = openedUtc;
            ExpiresUtc = expiresUtc;
            RemainingSeconds = remainingSeconds;
            LastResult = lastResult;
        }

        public RegistrationModeState State { get; }

        public DateTime? OpenedUtc { get; }

        public DateTime? ExpiresUtc { get; }

        public int? RemainingSeconds { get; }

        public RegistrationModeLastResult LastResult { get; }
    }

    public sealed class RegistrationModeClaim : IDisposable
    {
        private readonly RegistrationModeOwner _owner;
        private readonly long _claimId;
        private int _closed;

        internal RegistrationModeClaim(
            RegistrationModeOwner owner,
            long claimId)
        {
            _owner = owner;
            _claimId = claimId;
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _owner.CloseClaim(_claimId);
        }

        public void Complete(RegistrationModeLastResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The registration claim is already closed.");
            }

            _owner.CompleteClaim(_claimId, result);
        }

        public void Dispose()
        {
            Close();
        }
    }

    public sealed class RegistrationModeClaimResult
    {
        internal RegistrationModeClaimResult(
            RegistrationModeClaimStatus status,
            RegistrationModeSnapshot snapshot,
            RegistrationModeClaim claim)
        {
            Status = status;
            Snapshot = snapshot;
            Claim = claim;
        }

        public RegistrationModeClaimStatus Status { get; }

        public RegistrationModeSnapshot Snapshot { get; }

        public RegistrationModeClaim Claim { get; }

        public bool IsClaimed =>
            Status == RegistrationModeClaimStatus.Claimed;
    }

    public sealed class RegistrationModeOwner
    {
        public const int DurationSeconds = 60 * 60;

        private static readonly TimeSpan Duration =
            TimeSpan.FromSeconds(DurationSeconds);

        private readonly StateMutationGate _mutationGate;
        private readonly IRegistrationModeClock _clock;
        private RegistrationModeState _state;
        private DateTime _openedUtc;
        private DateTime _expiresUtc;
        private TimeSpan _openedMonotonic;
        private long _nextClaimId;
        private long _activeClaimId;
        private RegistrationModeLastResult _lastResult;

        public RegistrationModeOwner(StateMutationGate mutationGate)
            : this(mutationGate, new SystemRegistrationModeClock())
        {
        }

        public RegistrationModeOwner(
            StateMutationCoordinator stateCoordinator)
            : this(
                GetMutationGate(stateCoordinator),
                new SystemRegistrationModeClock())
        {
        }

        public RegistrationModeOwner(
            StateMutationGate mutationGate,
            IRegistrationModeClock clock)
        {
            _mutationGate = mutationGate
                ?? throw new ArgumentNullException(nameof(mutationGate));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _state = RegistrationModeState.Closed;
        }

        public RegistrationModeSnapshot GetSnapshot()
        {
            return _mutationGate.Execute(() =>
            {
                RefreshExpiration();
                return CreateSnapshot();
            });
        }

        public RegistrationModeSnapshot Open()
        {
            return _mutationGate.Execute(() =>
            {
                RefreshExpiration();
                if (_state != RegistrationModeState.Closed)
                {
                    return CreateSnapshot();
                }

                DateTime utcNow = ReadUtcNow();
                if (utcNow > DateTime.MaxValue - Duration)
                {
                    throw new InvalidOperationException(
                        "The registration mode UTC deadline cannot be represented.");
                }

                _openedUtc = utcNow;
                _expiresUtc = utcNow.Add(Duration);
                _openedMonotonic = ReadMonotonicElapsed();
                _state = RegistrationModeState.Open;
                return CreateSnapshot();
            });
        }

        public RegistrationModeSnapshot Close()
        {
            return _mutationGate.Execute(() =>
            {
                if (_state == RegistrationModeState.Claimed)
                {
                    return CreateSnapshot();
                }

                CloseCore();
                return CreateSnapshot();
            });
        }

        // The caller must finish API-key, product, CSR/SAN, domain and capacity
        // validation before passing true. Invalid requests are represented
        // explicitly so they can never consume the one-shot window.
        public RegistrationModeClaimResult TryClaimValidatedRequest(
            bool isFullyValidated)
        {
            return _mutationGate.Execute(() =>
            {
                RefreshExpiration();
                if (!isFullyValidated)
                {
                    return new RegistrationModeClaimResult(
                        RegistrationModeClaimStatus.InvalidRequest,
                        CreateSnapshot(),
                        null);
                }

                if (_state == RegistrationModeState.Closed)
                {
                    return new RegistrationModeClaimResult(
                        RegistrationModeClaimStatus.Closed,
                        CreateSnapshot(),
                        null);
                }

                if (_state == RegistrationModeState.Claimed)
                {
                    return new RegistrationModeClaimResult(
                        RegistrationModeClaimStatus.AlreadyClaimed,
                        CreateSnapshot(),
                        null);
                }

                _nextClaimId = NextClaimId(_nextClaimId);
                _activeClaimId = _nextClaimId;
                _state = RegistrationModeState.Claimed;
                var claim = new RegistrationModeClaim(
                    this,
                    _activeClaimId);
                return new RegistrationModeClaimResult(
                    RegistrationModeClaimStatus.Claimed,
                    CreateSnapshot(),
                    claim);
            });
        }

        internal void CloseClaim(long claimId)
        {
            _mutationGate.Execute(() =>
            {
                if (_state == RegistrationModeState.Claimed
                    && claimId == _activeClaimId)
                {
                    CloseCore();
                }
            });
        }

        internal void CompleteClaim(
            long claimId,
            RegistrationModeLastResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            _mutationGate.Execute(() =>
            {
                if (_state != RegistrationModeState.Claimed
                    || claimId != _activeClaimId)
                {
                    throw new InvalidOperationException(
                        "The registration claim is no longer active.");
                }

                _lastResult = result;
                CloseCore();
            });
        }

        private void RefreshExpiration()
        {
            if (_state != RegistrationModeState.Open)
            {
                return;
            }

            DateTime utcNow = ReadUtcNow();
            TimeSpan monotonicNow = ReadMonotonicElapsed();
            if (utcNow >= _expiresUtc
                || monotonicNow < _openedMonotonic
                || monotonicNow - _openedMonotonic >= Duration)
            {
                CloseCore();
            }
        }

        private RegistrationModeSnapshot CreateSnapshot()
        {
            if (_state != RegistrationModeState.Open)
            {
                return new RegistrationModeSnapshot(
                    _state,
                    null,
                    null,
                    null,
                    _lastResult);
            }

            DateTime utcNow = ReadUtcNow();
            TimeSpan monotonicNow = ReadMonotonicElapsed();
            TimeSpan utcRemaining = _expiresUtc - utcNow;
            TimeSpan monotonicRemaining = Duration
                - (monotonicNow - _openedMonotonic);
            TimeSpan remaining = utcRemaining <= monotonicRemaining
                ? utcRemaining
                : monotonicRemaining;
            int remainingSeconds = remaining <= TimeSpan.Zero
                ? 0
                : (int)Math.Min(
                    DurationSeconds,
                    Math.Ceiling(remaining.TotalSeconds));
            return new RegistrationModeSnapshot(
                _state,
                _openedUtc,
                _expiresUtc,
                remainingSeconds,
                _lastResult);
        }

        private void CloseCore()
        {
            _state = RegistrationModeState.Closed;
            _openedUtc = default(DateTime);
            _expiresUtc = default(DateTime);
            _openedMonotonic = default(TimeSpan);
            _activeClaimId = 0;
        }

        private DateTime ReadUtcNow()
        {
            DateTime value = _clock.UtcNow;
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new InvalidOperationException(
                    "The registration mode clock must return UTC time.");
            }

            return value;
        }

        private TimeSpan ReadMonotonicElapsed()
        {
            TimeSpan value = _clock.MonotonicElapsed;
            if (value < TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    "The registration mode monotonic clock cannot be negative.");
            }

            return value;
        }

        private static long NextClaimId(long current)
        {
            return current == long.MaxValue ? 1 : current + 1;
        }

        private static StateMutationGate GetMutationGate(
            StateMutationCoordinator stateCoordinator)
        {
            if (stateCoordinator == null)
            {
                throw new ArgumentNullException(nameof(stateCoordinator));
            }

            return stateCoordinator.MutationGate;
        }
    }
}
