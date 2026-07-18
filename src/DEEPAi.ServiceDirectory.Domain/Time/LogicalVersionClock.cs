using System;

namespace DEEPAi.ServiceDirectory.Domain.Time
{
    public sealed class LogicalClockExhaustedException : InvalidOperationException
    {
        internal LogicalClockExhaustedException()
            : base("The logical version clock is exhausted and cannot issue another revision.")
        {
        }
    }

    public static class LogicalVersionClock
    {
        public static ulong Next(ulong current)
        {
            if (current == ulong.MaxValue)
            {
                throw new LogicalClockExhaustedException();
            }

            return current + 1UL;
        }

        public static ulong Observe(ulong current, ulong observed)
        {
            return observed > current ? observed : current;
        }
    }
}
