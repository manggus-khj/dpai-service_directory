using System;
using System.Diagnostics;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal interface IWatchdogClock
    {
        DateTimeOffset UtcNow { get; }

        TimeSpan MonotonicNow { get; }
    }

    internal sealed class SystemWatchdogClock : IWatchdogClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public DateTimeOffset UtcNow
        {
            get
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                long canonicalTicks = now.Ticks
                    - (now.Ticks % TimeSpan.TicksPerMillisecond);
                return new DateTimeOffset(canonicalTicks, TimeSpan.Zero);
            }
        }

        public TimeSpan MonotonicNow => _stopwatch.Elapsed;
    }
}
