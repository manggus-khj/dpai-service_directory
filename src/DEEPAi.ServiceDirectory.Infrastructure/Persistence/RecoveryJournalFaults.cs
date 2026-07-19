using System;
using System.IO;

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

    internal sealed class RecoveryRequiredException : IOException
    {
        internal RecoveryRequiredException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class RecoveryJournalException : IOException
    {
        internal RecoveryJournalException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
