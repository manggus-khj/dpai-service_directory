using System;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Application.Persistence
{
    public enum StateLoadFailureCode
    {
        None = 0,
        InvalidData,
        AccessDenied,
        IoFailure,
        RecoveryFailed
    }

    public enum StateCommitFailureCode
    {
        None = 0,
        AccessDenied,
        DiskFull,
        IoFailure,
        RecoveryRequired
    }

    public sealed class StateLoadResult
    {
        private StateLoadResult(
            bool isSuccess,
            DirectorySnapshot snapshot,
            StateLoadFailureCode failureCode)
        {
            IsSuccess = isSuccess;
            Snapshot = snapshot;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public DirectorySnapshot Snapshot { get; }

        public StateLoadFailureCode FailureCode { get; }

        public static StateLoadResult Success(DirectorySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new System.ArgumentNullException(nameof(snapshot));
            }

            return new StateLoadResult(true, snapshot, StateLoadFailureCode.None);
        }

        public static StateLoadResult Failure(StateLoadFailureCode failureCode)
        {
            if (failureCode == StateLoadFailureCode.None
                || !Enum.IsDefined(typeof(StateLoadFailureCode), failureCode))
            {
                throw new ArgumentException("A defined failure code is required.", nameof(failureCode));
            }

            return new StateLoadResult(false, null, failureCode);
        }
    }

    public sealed class StateCommitResult
    {
        private StateCommitResult(bool isSuccess, StateCommitFailureCode failureCode)
        {
            IsSuccess = isSuccess;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public StateCommitFailureCode FailureCode { get; }

        public bool RequiresReload => FailureCode == StateCommitFailureCode.RecoveryRequired;

        public static StateCommitResult Success()
        {
            return new StateCommitResult(true, StateCommitFailureCode.None);
        }

        public static StateCommitResult Failure(StateCommitFailureCode failureCode)
        {
            if (failureCode == StateCommitFailureCode.None
                || !Enum.IsDefined(typeof(StateCommitFailureCode), failureCode))
            {
                throw new ArgumentException("A defined failure code is required.", nameof(failureCode));
            }

            return new StateCommitResult(false, failureCode);
        }
    }

    public interface IServiceDirectoryStateStore
    {
        StateLoadResult Load();

        // This initial contract covers directory.xml and pending.xml only. The config.xml
        // contract is added only after its schema and journal participation are decided.
        // The implementation derives the affected documents from the two snapshots so a
        // caller cannot omit part of a multi-document mutation. Success is allowed only
        // after every affected document is durably committed. Once a commit may have
        // changed any target but cannot prove the final state, it must return
        // RecoveryRequired. The caller then stops mutations until Load has completed
        // journal recovery and published a verified snapshot.
        StateCommitResult Commit(
            DirectorySnapshot expectedSnapshot,
            DirectorySnapshot nextSnapshot);
    }
}
