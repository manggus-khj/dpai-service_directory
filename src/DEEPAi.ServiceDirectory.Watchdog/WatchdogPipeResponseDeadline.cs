using System;
using System.Diagnostics;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal sealed class WatchdogPipeResponseDeadline
    {
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;
        private readonly long _startedAt;
        private readonly TimeSpan _timeout;
        private long _lastObservedTimestamp;
        private bool _expired;

        internal WatchdogPipeResponseDeadline(TimeSpan timeout)
            : this(timeout, Stopwatch.GetTimestamp, Stopwatch.Frequency)
        {
        }

        internal WatchdogPipeResponseDeadline(
            TimeSpan timeout,
            Func<long> timestampProvider,
            long timestampFrequency)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
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

            long startedAt = timestampProvider();
            if (startedAt < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider));
            }

            _timeout = timeout;
            _timestampProvider = timestampProvider;
            _timestampFrequency = timestampFrequency;
            _startedAt = startedAt;
            _lastObservedTimestamp = startedAt;
        }

        internal TimeSpan Remaining
        {
            get
            {
                if (_expired)
                {
                    return TimeSpan.Zero;
                }

                long now = _timestampProvider();
                if (now < _lastObservedTimestamp)
                {
                    _expired = true;
                    return TimeSpan.Zero;
                }

                _lastObservedTimestamp = now;
                double elapsedSeconds =
                    (now - _startedAt) / (double)_timestampFrequency;
                if (elapsedSeconds >= _timeout.TotalSeconds)
                {
                    _expired = true;
                    return TimeSpan.Zero;
                }

                TimeSpan remaining = _timeout
                    - TimeSpan.FromSeconds(elapsedSeconds);
                if (remaining <= TimeSpan.Zero)
                {
                    _expired = true;
                    return TimeSpan.Zero;
                }

                return remaining;
            }
        }
    }
}
