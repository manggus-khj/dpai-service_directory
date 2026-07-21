using System;
using DEEPAi.ServiceDirectory.Application.State;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal enum RecoveryJournalFaultPoint
    {
        ImagesFlushed = 0,
        PreparedFlushed = 1,
        TargetApplied = 2,
        CommittedFlushed = 3,
        CleanupStarting = 4
    }

    internal interface IRecoveryJournalFaultInjector
    {
        void OnFault(
            RecoveryJournalFaultPoint faultPoint,
            StateFileTarget? target);
    }

    internal sealed class NoOpRecoveryJournalFaultInjector
        : IRecoveryJournalFaultInjector
    {
        internal static readonly NoOpRecoveryJournalFaultInjector Instance =
            new NoOpRecoveryJournalFaultInjector();

        private NoOpRecoveryJournalFaultInjector()
        {
        }

        public void OnFault(
            RecoveryJournalFaultPoint faultPoint,
            StateFileTarget? target)
        {
        }
    }

    internal sealed class RecoveryRequiredException
        : StateRecoveryRequiredException
    {
        internal RecoveryRequiredException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class RecoveryJournalException
        : StateRecoveryRequiredException
    {
        internal RecoveryJournalException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
