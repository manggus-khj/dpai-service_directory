using System;
using System.Diagnostics;

namespace DEEPAi.ServiceDirectory.Application.Registration
{
    public interface IRegistrationModeClock
    {
        DateTime UtcNow { get; }

        TimeSpan MonotonicElapsed { get; }
    }

    public sealed class SystemRegistrationModeClock
        : IRegistrationModeClock
    {
        public DateTime UtcNow => DateTime.UtcNow;

        public TimeSpan MonotonicElapsed
        {
            get
            {
                long timestamp = Stopwatch.GetTimestamp();
                long wholeSeconds = timestamp / Stopwatch.Frequency;
                long remainder = timestamp % Stopwatch.Frequency;
                long ticks = checked(
                    (wholeSeconds * TimeSpan.TicksPerSecond)
                    + ((remainder * TimeSpan.TicksPerSecond)
                        / Stopwatch.Frequency));
                return TimeSpan.FromTicks(ticks);
            }
        }
    }
}
