using System;

namespace DEEPAi.ServiceDirectory.Domain.Registration
{
    public enum SubmissionStatus
    {
        PendingNew,
        PendingModify,
        PendingExists,
        AlreadyRegistered
    }

    public enum ApprovalStatus
    {
        Created,
        Updated,
        AlreadySatisfied
    }

    public abstract class StateTransitionResult
    {
        protected StateTransitionResult(
            bool isSuccess,
            DomainErrorCode? errorCode,
            DirectorySnapshot nextSnapshot,
            bool stateChanged,
            bool requiresPersistence,
            bool scheduleSync)
        {
            IsSuccess = isSuccess;
            ErrorCode = errorCode;
            NextSnapshot = nextSnapshot ?? throw new ArgumentNullException(nameof(nextSnapshot));
            StateChanged = stateChanged;
            RequiresPersistence = requiresPersistence;
            ScheduleSync = scheduleSync;
        }

        public bool IsSuccess { get; }

        public DomainErrorCode? ErrorCode { get; }

        public DirectorySnapshot NextSnapshot { get; }

        public bool StateChanged { get; }

        public bool RequiresPersistence { get; }

        public bool ScheduleSync { get; }
    }

    public sealed class SubmissionResult : StateTransitionResult
    {
        private SubmissionResult(
            bool isSuccess,
            DomainErrorCode? errorCode,
            DirectorySnapshot nextSnapshot,
            bool stateChanged,
            bool requiresPersistence,
            SubmissionStatus? status,
            Guid? pendingId)
            : base(isSuccess, errorCode, nextSnapshot, stateChanged, requiresPersistence, false)
        {
            Status = status;
            PendingId = pendingId;
        }

        public SubmissionStatus? Status { get; }

        public Guid? PendingId { get; }

        internal static SubmissionResult Success(
            DirectorySnapshot nextSnapshot,
            SubmissionStatus status,
            Guid? pendingId,
            bool stateChanged)
        {
            return new SubmissionResult(
                true,
                null,
                nextSnapshot,
                stateChanged,
                stateChanged,
                status,
                pendingId);
        }

        internal static SubmissionResult Failure(DirectorySnapshot current, DomainErrorCode errorCode)
        {
            return new SubmissionResult(false, errorCode, current, false, false, null, null);
        }
    }

    public sealed class ApprovalResult : StateTransitionResult
    {
        private ApprovalResult(
            bool isSuccess,
            DomainErrorCode? errorCode,
            DirectorySnapshot nextSnapshot,
            bool stateChanged,
            bool scheduleSync,
            ApprovalStatus? status,
            ProductCode? productCode)
            : base(isSuccess, errorCode, nextSnapshot, stateChanged, stateChanged, scheduleSync)
        {
            Status = status;
            ProductCode = productCode;
        }

        public ApprovalStatus? Status { get; }

        public ProductCode? ProductCode { get; }

        internal static ApprovalResult Success(
            DirectorySnapshot nextSnapshot,
            ApprovalStatus status,
            ProductCode productCode,
            bool scheduleSync)
        {
            return new ApprovalResult(true, null, nextSnapshot, true, scheduleSync, status, productCode);
        }

        internal static ApprovalResult Failure(DirectorySnapshot current, DomainErrorCode errorCode)
        {
            return new ApprovalResult(false, errorCode, current, false, false, null, null);
        }
    }

    public sealed class RejectResult : StateTransitionResult
    {
        private RejectResult(
            bool isSuccess,
            DomainErrorCode? errorCode,
            DirectorySnapshot nextSnapshot,
            ProductCode? productCode)
            : base(isSuccess, errorCode, nextSnapshot, isSuccess, isSuccess, false)
        {
            ProductCode = productCode;
        }

        public ProductCode? ProductCode { get; }

        internal static RejectResult Success(DirectorySnapshot nextSnapshot, ProductCode productCode)
        {
            return new RejectResult(true, null, nextSnapshot, productCode);
        }

        internal static RejectResult Failure(DirectorySnapshot current, DomainErrorCode errorCode)
        {
            return new RejectResult(false, errorCode, current, null);
        }
    }

    public sealed class DeleteResult : StateTransitionResult
    {
        private DeleteResult(
            bool isSuccess,
            DomainErrorCode? errorCode,
            DirectorySnapshot nextSnapshot,
            ProductCode? productCode)
            : base(isSuccess, errorCode, nextSnapshot, isSuccess, isSuccess, isSuccess)
        {
            ProductCode = productCode;
        }

        public ProductCode? ProductCode { get; }

        internal static DeleteResult Success(DirectorySnapshot nextSnapshot, ProductCode productCode)
        {
            return new DeleteResult(true, null, nextSnapshot, productCode);
        }

        internal static DeleteResult Failure(DirectorySnapshot current, DomainErrorCode errorCode)
        {
            return new DeleteResult(false, errorCode, current, null);
        }
    }
}
