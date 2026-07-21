using System;
using System.IO;

namespace DEEPAi.ServiceDirectory.Application.State
{
    // Cross-assembly marker for a durable state operation whose commit point
    // cannot be determined without replaying the shared recovery journal.
    public class StateRecoveryRequiredException : IOException
    {
        public StateRecoveryRequiredException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
