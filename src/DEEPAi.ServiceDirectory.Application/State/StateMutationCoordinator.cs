using System;
using System.Net;
using System.Threading;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Registration;
using DEEPAi.ServiceDirectory.Domain.Synchronization;

namespace DEEPAi.ServiceDirectory.Application.State
{
    public sealed class StateCoordinatorOpenResult
    {
        private StateCoordinatorOpenResult(
            StateMutationCoordinator coordinator,
            StateLoadFailureCode failureCode)
        {
            bool isSuccess = coordinator != null;
            if ((isSuccess && failureCode != StateLoadFailureCode.None)
                || (!isSuccess
                    && (failureCode == StateLoadFailureCode.None
                        || !Enum.IsDefined(typeof(StateLoadFailureCode), failureCode))))
            {
                throw new ArgumentException(
                    "The coordinator open result contains an inconsistent state.");
            }

            IsSuccess = isSuccess;
            Coordinator = coordinator;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public StateMutationCoordinator Coordinator { get; }

        public StateLoadFailureCode FailureCode { get; }

        internal static StateCoordinatorOpenResult Success(
            StateMutationCoordinator coordinator)
        {
            if (coordinator == null)
            {
                throw new ArgumentNullException(nameof(coordinator));
            }

            return new StateCoordinatorOpenResult(
                coordinator,
                StateLoadFailureCode.None);
        }

        internal static StateCoordinatorOpenResult Failure(
            StateLoadFailureCode failureCode)
        {
            return new StateCoordinatorOpenResult(null, failureCode);
        }
    }

    public enum StateCoordinatorStatus
    {
        Ready = 0,
        RecoveryRequired = 1,
        Recovering = 2
    }

    public sealed class StateMutationCoordinator
    {
        private readonly IServiceDirectoryStateStore _store;
        private readonly object _mutationGate = new object();
        private readonly object _recoveryGate = new object();
        private DirectorySnapshot _currentSnapshot;
        private int _status;

        private StateMutationCoordinator(
            IServiceDirectoryStateStore store,
            DirectorySnapshot initialSnapshot)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (initialSnapshot == null)
            {
                throw new ArgumentNullException(nameof(initialSnapshot));
            }

            _store = store;
            _currentSnapshot = initialSnapshot;
            _status = (int)StateCoordinatorStatus.Ready;
        }

        public static StateCoordinatorOpenResult Open(
            IServiceDirectoryStateStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            StateLoadResult loadResult = store.Load();
            if (loadResult == null)
            {
                throw new InvalidOperationException(
                    "The state store returned no initial load result.");
            }

            if (!loadResult.IsSuccess)
            {
                return StateCoordinatorOpenResult.Failure(
                    loadResult.FailureCode);
            }

            if (loadResult.Snapshot == null)
            {
                throw new InvalidOperationException(
                    "A successful initial state load must provide a snapshot.");
            }

            return StateCoordinatorOpenResult.Success(
                new StateMutationCoordinator(store, loadResult.Snapshot));
        }

        public DirectorySnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

        public StateCoordinatorStatus Status =>
            (StateCoordinatorStatus)Volatile.Read(ref _status);

        public bool IsReady => Status == StateCoordinatorStatus.Ready;

        public bool TryGetReadySnapshot(out DirectorySnapshot snapshot)
        {
            if (Status != StateCoordinatorStatus.Ready)
            {
                snapshot = null;
                return false;
            }

            DirectorySnapshot candidate = Volatile.Read(ref _currentSnapshot);
            if (Status != StateCoordinatorStatus.Ready)
            {
                snapshot = null;
                return false;
            }

            snapshot = candidate;
            return true;
        }

        public StateMutationResult<SubmissionResult> Submit(
            ServiceDefinition requested,
            IPAddress sourceAddress,
            Guid pendingId,
            DateTime requestedUtc)
        {
            return ExecuteMutation(current => RegistrationStateMachine.Submit(
                current,
                requested,
                sourceAddress,
                pendingId,
                requestedUtc));
        }

        public StateMutationResult<ApprovalResult> Approve(
            Guid pendingId,
            Guid localInstanceId,
            DateTime utcNow)
        {
            return ExecuteMutation(current => RegistrationStateMachine.Approve(
                current,
                pendingId,
                localInstanceId,
                utcNow));
        }

        public StateMutationResult<RejectResult> Reject(Guid pendingId)
        {
            return ExecuteMutation(current => RegistrationStateMachine.Reject(
                current,
                pendingId));
        }

        public StateMutationResult<DeleteResult> Delete(
            ProductCode productCode,
            Guid localInstanceId,
            DateTime utcNow)
        {
            return ExecuteMutation(current => RegistrationStateMachine.Delete(
                current,
                productCode,
                localInstanceId,
                utcNow));
        }

        // The caller must pass only a snapshot whose peer authentication and
        // complete batch set have already been validated outside the mutation gate.
        public StateMutationResult<SynchronizationMergeResult>
            MergeVerifiedSynchronizationSnapshot(
                SynchronizationSnapshot verifiedRemoteSnapshot)
        {
            if (verifiedRemoteSnapshot == null)
            {
                throw new ArgumentNullException(nameof(verifiedRemoteSnapshot));
            }

            return ExecuteMutation(current => SynchronizationMerger.Merge(
                current,
                verifiedRemoteSnapshot));
        }

        public StateLoadResult Recover()
        {
            lock (_recoveryGate)
            {
                lock (_mutationGate)
                {
                    StateCoordinatorStatus status = ReadStatus();
                    if (status == StateCoordinatorStatus.Ready)
                    {
                        return StateLoadResult.Success(
                            Volatile.Read(ref _currentSnapshot));
                    }

                    if (status == StateCoordinatorStatus.Recovering)
                    {
                        throw new InvalidOperationException(
                            "State recovery is already in progress.");
                    }

                    WriteStatus(StateCoordinatorStatus.Recovering);
                }

                StateLoadResult loadResult;
                try
                {
                    loadResult = _store.Load();
                    if (loadResult == null)
                    {
                        throw new InvalidOperationException(
                            "The state store returned no recovery result.");
                    }

                    if (loadResult.IsSuccess && loadResult.Snapshot == null)
                    {
                        throw new InvalidOperationException(
                            "A successful state recovery must provide a snapshot.");
                    }
                }
                catch
                {
                    lock (_mutationGate)
                    {
                        WriteStatus(StateCoordinatorStatus.RecoveryRequired);
                    }

                    throw;
                }

                lock (_mutationGate)
                {
                    if (loadResult.IsSuccess)
                    {
                        Volatile.Write(ref _currentSnapshot, loadResult.Snapshot);
                        WriteStatus(StateCoordinatorStatus.Ready);
                    }
                    else
                    {
                        WriteStatus(StateCoordinatorStatus.RecoveryRequired);
                    }
                }

                return loadResult;
            }
        }

        private StateMutationResult<TTransition> ExecuteMutation<TTransition>(
            Func<DirectorySnapshot, TTransition> transitionFactory)
            where TTransition : StateTransitionResult
        {
            lock (_mutationGate)
            {
                if (ReadStatus() != StateCoordinatorStatus.Ready)
                {
                    return StateMutationResult<TTransition>.Blocked();
                }

                DirectorySnapshot expectedSnapshot = Volatile.Read(ref _currentSnapshot);
                TTransition transition = transitionFactory(expectedSnapshot);
                if (transition == null)
                {
                    throw new InvalidOperationException(
                        "The domain state transition returned no result.");
                }

                if (!transition.IsSuccess || !transition.RequiresPersistence)
                {
                    return StateMutationResult<TTransition>.Completed(
                        transition,
                        false);
                }

                StateCommitResult commitResult;
                try
                {
                    commitResult = _store.Commit(
                        expectedSnapshot,
                        transition.NextSnapshot);
                    if (commitResult == null)
                    {
                        throw new InvalidOperationException(
                            "The state store returned no commit result.");
                    }
                }
                catch
                {
                    WriteStatus(StateCoordinatorStatus.RecoveryRequired);
                    throw;
                }

                if (!commitResult.IsSuccess)
                {
                    if (commitResult.RequiresReload)
                    {
                        WriteStatus(StateCoordinatorStatus.RecoveryRequired);
                        return StateMutationResult<TTransition>.RequiresRecovery(
                            commitResult.FailureCode);
                    }

                    return StateMutationResult<TTransition>.PersistenceFailed(
                        commitResult.FailureCode);
                }

                Volatile.Write(ref _currentSnapshot, transition.NextSnapshot);
                return StateMutationResult<TTransition>.Completed(
                    transition,
                    true);
            }
        }

        private StateCoordinatorStatus ReadStatus()
        {
            return (StateCoordinatorStatus)Volatile.Read(ref _status);
        }

        private void WriteStatus(StateCoordinatorStatus status)
        {
            Volatile.Write(ref _status, (int)status);
        }
    }
}
