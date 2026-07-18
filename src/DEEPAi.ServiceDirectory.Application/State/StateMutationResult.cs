using System;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Domain.Registration;

namespace DEEPAi.ServiceDirectory.Application.State
{
    public enum StateMutationStatus
    {
        Completed = 0,
        PersistenceFailed = 1,
        RecoveryRequired = 2,
        BlockedForRecovery = 3
    }

    public sealed class StateMutationResult<TTransition>
        where TTransition : StateTransitionResult
    {
        private StateMutationResult(
            StateMutationStatus status,
            TTransition domainTransition,
            StateCommitFailureCode commitFailureCode,
            bool snapshotPublished)
        {
            ValidateState(status, domainTransition, commitFailureCode, snapshotPublished);
            Status = status;
            DomainTransition = domainTransition;
            CommitFailureCode = commitFailureCode;
            SnapshotPublished = snapshotPublished;
            ShouldScheduleSync = status == StateMutationStatus.Completed
                && domainTransition != null
                && domainTransition.IsSuccess
                && snapshotPublished
                && domainTransition.ScheduleSync;
        }

        public StateMutationStatus Status { get; }

        // Exposed only for a completed execution. Persistence failures never
        // expose an uncommitted candidate transition to downstream callers.
        public TTransition DomainTransition { get; }

        public bool HasDomainTransition => DomainTransition != null;

        public bool IsSuccessful => Status == StateMutationStatus.Completed
            && DomainTransition != null
            && DomainTransition.IsSuccess;

        public StateCommitFailureCode CommitFailureCode { get; }

        public bool SnapshotPublished { get; }

        public bool ShouldScheduleSync { get; }

        internal static StateMutationResult<TTransition> Completed(
            TTransition domainTransition,
            bool snapshotPublished)
        {
            if (domainTransition == null)
            {
                throw new ArgumentNullException(nameof(domainTransition));
            }

            return new StateMutationResult<TTransition>(
                StateMutationStatus.Completed,
                domainTransition,
                StateCommitFailureCode.None,
                snapshotPublished);
        }

        internal static StateMutationResult<TTransition> PersistenceFailed(
            StateCommitFailureCode failureCode)
        {
            if (failureCode == StateCommitFailureCode.None
                || failureCode == StateCommitFailureCode.RecoveryRequired
                || !Enum.IsDefined(typeof(StateCommitFailureCode), failureCode))
            {
                throw new ArgumentException(
                    "An ordinary persistence failure code is required.",
                    nameof(failureCode));
            }

            return new StateMutationResult<TTransition>(
                StateMutationStatus.PersistenceFailed,
                null,
                failureCode,
                false);
        }

        internal static StateMutationResult<TTransition> RequiresRecovery(
            StateCommitFailureCode failureCode)
        {
            if (failureCode != StateCommitFailureCode.RecoveryRequired)
            {
                throw new ArgumentException(
                    "A recovery-required commit failure code is required.",
                    nameof(failureCode));
            }

            return new StateMutationResult<TTransition>(
                StateMutationStatus.RecoveryRequired,
                null,
                failureCode,
                false);
        }

        internal static StateMutationResult<TTransition> Blocked()
        {
            return new StateMutationResult<TTransition>(
                StateMutationStatus.BlockedForRecovery,
                null,
                StateCommitFailureCode.None,
                false);
        }

        private static void ValidateState(
            StateMutationStatus status,
            TTransition domainTransition,
            StateCommitFailureCode commitFailureCode,
            bool snapshotPublished)
        {
            bool validCompleted = status == StateMutationStatus.Completed
                && domainTransition != null
                && commitFailureCode == StateCommitFailureCode.None
                && ((snapshotPublished
                        && domainTransition.IsSuccess
                        && domainTransition.RequiresPersistence)
                    || (!snapshotPublished
                        && !domainTransition.RequiresPersistence));
            bool validPersistenceFailure = status == StateMutationStatus.PersistenceFailed
                && domainTransition == null
                && commitFailureCode != StateCommitFailureCode.None
                && commitFailureCode != StateCommitFailureCode.RecoveryRequired
                && Enum.IsDefined(typeof(StateCommitFailureCode), commitFailureCode)
                && !snapshotPublished;
            bool validRecoveryRequired = status == StateMutationStatus.RecoveryRequired
                && domainTransition == null
                && commitFailureCode == StateCommitFailureCode.RecoveryRequired
                && !snapshotPublished;
            bool validBlocked = status == StateMutationStatus.BlockedForRecovery
                && domainTransition == null
                && commitFailureCode == StateCommitFailureCode.None
                && !snapshotPublished;

            if (!validCompleted
                && !validPersistenceFailure
                && !validRecoveryRequired
                && !validBlocked)
            {
                throw new ArgumentException(
                    "The state mutation result contains an inconsistent status combination.");
            }
        }
    }
}
