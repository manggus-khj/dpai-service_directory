using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal sealed class WatchdogMonitorDecision
    {
        internal WatchdogMonitorDecision(bool shouldRestart)
        {
            ShouldRestart = shouldRestart;
        }

        internal bool ShouldRestart { get; }
    }

    internal sealed class WatchdogMonitorPolicy
    {
        internal const int ConsecutiveFailureThreshold = 3;
        internal const int MaximumRestartsInWindow = 3;
        internal static readonly TimeSpan RestartWindow =
            TimeSpan.FromMinutes(10);

        private readonly object _gate = new object();
        private readonly Queue<TimeSpan> _automaticRestartAttempts =
            new Queue<TimeSpan>();
        private long _consecutiveFailures;
        private long _failuresAtLastRestartDecision;
        private TimeSpan _lastMonotonicNow;
        private bool _automaticRestartSuppressed;
        private bool _manualStopRequested;
        private WatchdogHealthStatus _healthStatus =
            WatchdogHealthStatus.NotRun;
        private DateTimeOffset? _lastHealthUtc;

        internal WatchdogMonitorDecision RecordObservation(
            bool mainServiceRunning,
            bool healthSucceeded,
            DateTimeOffset completedUtc,
            TimeSpan monotonicNow)
        {
            ValidateCompletedUtc(completedUtc);

            lock (_gate)
            {
                ValidateMonotonicNow(monotonicNow);
                PruneRestartAttempts(monotonicNow);

                bool healthy = mainServiceRunning && healthSucceeded;
                _healthStatus = healthy
                    ? WatchdogHealthStatus.Ok
                    : WatchdogHealthStatus.Failed;
                _lastHealthUtc = completedUtc;

                if (healthy)
                {
                    _consecutiveFailures = 0;
                    _failuresAtLastRestartDecision = 0;
                    return new WatchdogMonitorDecision(false);
                }

                if (_consecutiveFailures < int.MaxValue)
                {
                    _consecutiveFailures++;
                }

                long failuresSinceLastDecision =
                    _consecutiveFailures - _failuresAtLastRestartDecision;
                if (_manualStopRequested
                    || _automaticRestartSuppressed
                    || failuresSinceLastDecision
                        < ConsecutiveFailureThreshold)
                {
                    return new WatchdogMonitorDecision(false);
                }

                // The budget counts an initiated automatic restart attempt,
                // regardless of whether Windows later completes the control
                // transition. This prevents a failing control path from being
                // retried every ten seconds without a bound.
                _failuresAtLastRestartDecision = _consecutiveFailures;
                _automaticRestartAttempts.Enqueue(monotonicNow);
                if (_automaticRestartAttempts.Count
                    >= MaximumRestartsInWindow)
                {
                    _automaticRestartSuppressed = true;
                }

                return new WatchdogMonitorDecision(true);
            }
        }

        internal void RecordManualStop(TimeSpan monotonicNow)
        {
            lock (_gate)
            {
                ValidateMonotonicNow(monotonicNow);
                PruneRestartAttempts(monotonicNow);
                _manualStopRequested = true;
                _failuresAtLastRestartDecision = _consecutiveFailures;
            }
        }

        internal void RecordManualStartOrRestart(TimeSpan monotonicNow)
        {
            lock (_gate)
            {
                ValidateMonotonicNow(monotonicNow);
                _manualStopRequested = false;
                _automaticRestartSuppressed = false;
                _automaticRestartAttempts.Clear();
                _failuresAtLastRestartDecision = _consecutiveFailures;
            }
        }

        internal WatchdogStatusSnapshot CreateStatusSnapshot(
            WatchdogServiceStatus serviceStatus,
            TimeSpan monotonicNow)
        {
            lock (_gate)
            {
                ValidateMonotonicNow(monotonicNow);
                PruneRestartAttempts(monotonicNow);
                return new WatchdogStatusSnapshot(
                    serviceStatus,
                    _healthStatus,
                    checked((int)_consecutiveFailures),
                    _automaticRestartAttempts.Count,
                    _automaticRestartSuppressed
                        ? WatchdogAutoRestartStatus.Suppressed
                        : WatchdogAutoRestartStatus.Enabled,
                    _lastHealthUtc);
            }
        }

        internal bool IsManualStopRequested
        {
            get
            {
                lock (_gate)
                {
                    return _manualStopRequested;
                }
            }
        }

        private static void ValidateCompletedUtc(DateTimeOffset completedUtc)
        {
            if (completedUtc.Offset != TimeSpan.Zero
                || completedUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            {
                throw new ArgumentException(
                    "A health completion time must be millisecond-precision UTC.",
                    nameof(completedUtc));
            }
        }

        private void ValidateMonotonicNow(TimeSpan monotonicNow)
        {
            if (monotonicNow < TimeSpan.Zero
                || monotonicNow < _lastMonotonicNow)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(monotonicNow),
                    "Watchdog monotonic time must not move backwards.");
            }

            _lastMonotonicNow = monotonicNow;
        }

        private void PruneRestartAttempts(TimeSpan monotonicNow)
        {
            while (_automaticRestartAttempts.Count > 0
                && monotonicNow - _automaticRestartAttempts.Peek()
                    >= RestartWindow)
            {
                _automaticRestartAttempts.Dequeue();
            }
        }
    }
}
