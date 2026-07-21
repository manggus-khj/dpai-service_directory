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

        // This snapshot contract owns directory.xml. Config, peer credential, and PKI
        // mutations use the same fixed-target journal engine through their dedicated
        // stores. This store derives the Directory change from the two snapshots so a
        // caller cannot provide arbitrary target bytes. Success is allowed only after
        // the affected document is durably committed. Once a commit may have changed a
        // target but cannot prove the final state, it must return RecoveryRequired. The
        // caller then stops mutations until Load has completed journal recovery and
        // published a verified snapshot.
        StateCommitResult Commit(
            DirectorySnapshot expectedSnapshot,
            DirectorySnapshot nextSnapshot);
    }
}
