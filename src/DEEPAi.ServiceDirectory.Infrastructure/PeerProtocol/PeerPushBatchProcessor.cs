using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Application.Synchronization;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public enum PeerPushBatchProcessingStatus
    {
        AcceptedAwaitingMoreBatches = 0,
        Completed = 1,
        PeerMismatch = 2,
        InvalidBatch = 3,
        StagingRejected = 4,
        DomainRejected = 5,
        PersistenceFailed = 6,
        RecoveryRequired = 7,
        BlockedForRecovery = 8
    }

    public sealed class PeerPushBatchProcessingResult
    {
        private PeerPushBatchProcessingResult(
            PeerPushBatchProcessingStatus status,
            InboundSynchronizationStagingError stagingError,
            DomainErrorCode? domainErrorCode,
            StateCommitFailureCode commitFailureCode,
            bool snapshotPublished,
            Guid? completedRemoteSnapshotId,
            SynchronizationSnapshot completedRemoteSnapshot,
            DirectorySnapshot currentDirectorySnapshot,
            SynchronizationSnapshot currentOutboundSnapshot)
        {
            Validate(
                status,
                stagingError,
                domainErrorCode,
                commitFailureCode,
                snapshotPublished,
                completedRemoteSnapshotId,
                completedRemoteSnapshot,
                currentDirectorySnapshot,
                currentOutboundSnapshot);

            Status = status;
            StagingError = stagingError;
            DomainErrorCode = domainErrorCode;
            CommitFailureCode = commitFailureCode;
            SnapshotPublished = snapshotPublished;
            CompletedRemoteSnapshotId = completedRemoteSnapshotId;
            CompletedRemoteSnapshot = completedRemoteSnapshot;
            CurrentDirectorySnapshot = currentDirectorySnapshot;
            CurrentOutboundSnapshot = currentOutboundSnapshot;
        }

        public PeerPushBatchProcessingStatus Status { get; }

        public bool IsAccepted =>
            Status == PeerPushBatchProcessingStatus
                .AcceptedAwaitingMoreBatches
            || Status == PeerPushBatchProcessingStatus.Completed;

        public bool IsCompleted =>
            Status == PeerPushBatchProcessingStatus.Completed;

        public InboundSynchronizationStagingError StagingError { get; }

        public DomainErrorCode? DomainErrorCode { get; }

        public StateCommitFailureCode CommitFailureCode { get; }

        // This is the signed Peer envelope code after authentication has
        // succeeded. HTTP status, response MAC and body suppression remain
        // responsibilities of the host boundary.
        public PeerSyncResponseCode ResponseCode
        {
            get
            {
                switch (Status)
                {
                    case PeerPushBatchProcessingStatus
                        .AcceptedAwaitingMoreBatches:
                    case PeerPushBatchProcessingStatus.Completed:
                        return PeerSyncResponseCode.Ok;
                    case PeerPushBatchProcessingStatus.PeerMismatch:
                        return PeerSyncResponseCode.NotPeer;
                    case PeerPushBatchProcessingStatus.InvalidBatch:
                        return PeerSyncResponseCode.BadRequest;
                    case PeerPushBatchProcessingStatus.StagingRejected:
                        return StagingError
                            == InboundSynchronizationStagingError
                                .BatchSizeExceeded
                            ? PeerSyncResponseCode.LimitExceeded
                            : PeerSyncResponseCode.BadRequest;
                    case PeerPushBatchProcessingStatus.DomainRejected:
                        return DomainErrorCode.Value
                            == DEEPAi.ServiceDirectory.Domain
                                .DomainErrorCode.RevisionCollision
                            ? PeerSyncResponseCode.RevisionCollision
                            : PeerSyncResponseCode.DirectoryCapacity;
                    case PeerPushBatchProcessingStatus.PersistenceFailed:
                    case PeerPushBatchProcessingStatus.RecoveryRequired:
                    case PeerPushBatchProcessingStatus.BlockedForRecovery:
                        return PeerSyncResponseCode.Internal;
                    default:
                        throw new InvalidOperationException(
                            "The Peer Push processing status is invalid.");
                }
            }
        }

        public bool SnapshotPublished { get; }

        public Guid? CompletedRemoteSnapshotId { get; }

        // The fully validated inbound snapshot is returned only after all batches
        // have completed and its merge has succeeded.
        public SynchronizationSnapshot CompletedRemoteSnapshot { get; }

        // This is the exact immutable directory snapshot produced by the merge.
        // It can contain local pending registrations, which are deliberately not
        // copied into CurrentOutboundSnapshot.
        public DirectorySnapshot CurrentDirectorySnapshot { get; }

        // A host may assign its own snapshot ID and retain this immutable view for
        // subsequent Pull batches. This processor does not invent an ID or storage.
        public SynchronizationSnapshot CurrentOutboundSnapshot { get; }

        internal static PeerPushBatchProcessingResult AwaitingMoreBatches()
        {
            return FailureOrPending(
                PeerPushBatchProcessingStatus.AcceptedAwaitingMoreBatches);
        }

        internal static PeerPushBatchProcessingResult Completed(
            Guid remoteSnapshotId,
            SynchronizationSnapshot remoteSnapshot,
            DirectorySnapshot currentDirectorySnapshot,
            SynchronizationSnapshot currentOutboundSnapshot,
            bool snapshotPublished)
        {
            return new PeerPushBatchProcessingResult(
                PeerPushBatchProcessingStatus.Completed,
                InboundSynchronizationStagingError.None,
                null,
                StateCommitFailureCode.None,
                snapshotPublished,
                remoteSnapshotId,
                remoteSnapshot,
                currentDirectorySnapshot,
                currentOutboundSnapshot);
        }

        internal static PeerPushBatchProcessingResult PeerMismatch()
        {
            return FailureOrPending(
                PeerPushBatchProcessingStatus.PeerMismatch);
        }

        internal static PeerPushBatchProcessingResult InvalidBatch()
        {
            return FailureOrPending(
                PeerPushBatchProcessingStatus.InvalidBatch);
        }

        internal static PeerPushBatchProcessingResult StagingRejected(
            InboundSynchronizationStagingError error)
        {
            return new PeerPushBatchProcessingResult(
                PeerPushBatchProcessingStatus.StagingRejected,
                error,
                null,
                StateCommitFailureCode.None,
                false,
                null,
                null,
                null,
                null);
        }

        internal static PeerPushBatchProcessingResult DomainRejected(
            DomainErrorCode errorCode)
        {
            return new PeerPushBatchProcessingResult(
                PeerPushBatchProcessingStatus.DomainRejected,
                InboundSynchronizationStagingError.None,
                errorCode,
                StateCommitFailureCode.None,
                false,
                null,
                null,
                null,
                null);
        }

        internal static PeerPushBatchProcessingResult PersistenceFailed(
            StateCommitFailureCode failureCode)
        {
            return new PeerPushBatchProcessingResult(
                PeerPushBatchProcessingStatus.PersistenceFailed,
                InboundSynchronizationStagingError.None,
                null,
                failureCode,
                false,
                null,
                null,
                null,
                null);
        }

        internal static PeerPushBatchProcessingResult RecoveryRequired()
        {
            return new PeerPushBatchProcessingResult(
                PeerPushBatchProcessingStatus.RecoveryRequired,
                InboundSynchronizationStagingError.None,
                null,
                StateCommitFailureCode.RecoveryRequired,
                false,
                null,
                null,
                null,
                null);
        }

        internal static PeerPushBatchProcessingResult BlockedForRecovery()
        {
            return FailureOrPending(
                PeerPushBatchProcessingStatus.BlockedForRecovery);
        }

        private static PeerPushBatchProcessingResult FailureOrPending(
            PeerPushBatchProcessingStatus status)
        {
            return new PeerPushBatchProcessingResult(
                status,
                InboundSynchronizationStagingError.None,
                null,
                StateCommitFailureCode.None,
                false,
                null,
                null,
                null,
                null);
        }

        private static void Validate(
            PeerPushBatchProcessingStatus status,
            InboundSynchronizationStagingError stagingError,
            DomainErrorCode? domainErrorCode,
            StateCommitFailureCode commitFailureCode,
            bool snapshotPublished,
            Guid? completedRemoteSnapshotId,
            SynchronizationSnapshot completedRemoteSnapshot,
            DirectorySnapshot currentDirectorySnapshot,
            SynchronizationSnapshot currentOutboundSnapshot)
        {
            if (!Enum.IsDefined(typeof(PeerPushBatchProcessingStatus), status))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            bool completed = status == PeerPushBatchProcessingStatus.Completed;
            bool hasAllCompletedValues = completedRemoteSnapshotId.HasValue
                && completedRemoteSnapshotId.Value != Guid.Empty
                && completedRemoteSnapshot != null
                && currentDirectorySnapshot != null
                && currentOutboundSnapshot != null;
            bool hasAnyCompletedValue = completedRemoteSnapshotId.HasValue
                || completedRemoteSnapshot != null
                || currentDirectorySnapshot != null
                || currentOutboundSnapshot != null;
            if ((completed && !hasAllCompletedValues)
                || (!completed && hasAnyCompletedValue)
                || (!completed && snapshotPublished))
            {
                throw new ArgumentException(
                    "Only a completed result may contain committed snapshot values.");
            }

            bool stagingRejected =
                status == PeerPushBatchProcessingStatus.StagingRejected;
            if (stagingRejected
                != (stagingError != InboundSynchronizationStagingError.None)
                || (stagingRejected
                    && !Enum.IsDefined(
                        typeof(InboundSynchronizationStagingError),
                        stagingError)))
            {
                throw new ArgumentException(
                    "The staging status and error are inconsistent.");
            }

            bool domainRejected =
                status == PeerPushBatchProcessingStatus.DomainRejected;
            if (domainRejected != domainErrorCode.HasValue
                || (domainErrorCode.HasValue
                    && domainErrorCode.Value
                        != DEEPAi.ServiceDirectory.Domain.DomainErrorCode
                            .RevisionCollision
                    && domainErrorCode.Value
                        != DEEPAi.ServiceDirectory.Domain.DomainErrorCode
                            .DirectoryCapacity))
            {
                throw new ArgumentException(
                    "The domain status and error are inconsistent.");
            }

            bool persistenceFailed =
                status == PeerPushBatchProcessingStatus.PersistenceFailed;
            bool recoveryRequired =
                status == PeerPushBatchProcessingStatus.RecoveryRequired;
            bool validCommitFailure = persistenceFailed
                && commitFailureCode != StateCommitFailureCode.None
                && commitFailureCode != StateCommitFailureCode.RecoveryRequired
                && Enum.IsDefined(
                    typeof(StateCommitFailureCode),
                    commitFailureCode);
            bool validRecoveryFailure = recoveryRequired
                && commitFailureCode == StateCommitFailureCode.RecoveryRequired;
            bool validNoCommitFailure = !persistenceFailed
                && !recoveryRequired
                && commitFailureCode == StateCommitFailureCode.None;
            if (!validCommitFailure
                && !validRecoveryFailure
                && !validNoCommitFailure)
            {
                throw new ArgumentException(
                    "The persistence status and error are inconsistent.");
            }
        }
    }

    // One processor belongs to one authenticated Peer Push snapshot exchange.
    // The caller must complete endpoint, HMAC, freshness, replay, session and XML
    // validation before supplying the typed request. HTTP and ACK generation are
    // intentionally outside this source-only orchestration boundary.
    public sealed class PeerPushBatchProcessor
    {
        private readonly Guid _expectedPeerInstanceId;
        private readonly StateMutationCoordinator _coordinator;
        private readonly InboundSynchronizationStagingAccumulator _staging;
        private readonly object _gate = new object();

        public PeerPushBatchProcessor(
            Guid expectedPeerInstanceId,
            StateMutationCoordinator coordinator)
        {
            if (expectedPeerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The expected Peer instance ID must not be empty.",
                    nameof(expectedPeerInstanceId));
            }

            _expectedPeerInstanceId = expectedPeerInstanceId;
            _coordinator = coordinator
                ?? throw new ArgumentNullException(nameof(coordinator));
            _staging = new InboundSynchronizationStagingAccumulator();
        }

        public Guid ExpectedPeerInstanceId => _expectedPeerInstanceId;

        public InboundSynchronizationStagingState StagingState
        {
            get
            {
                lock (_gate)
                {
                    return _staging.State;
                }
            }
        }

        public PeerPushBatchProcessingResult Process(
            PeerPushExchangeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            lock (_gate)
            {
                if (request.InstanceId != _expectedPeerInstanceId)
                {
                    _staging.Discard();
                    return PeerPushBatchProcessingResult.PeerMismatch();
                }

                if (!_coordinator.IsReady)
                {
                    _staging.Discard();
                    return PeerPushBatchProcessingResult
                        .BlockedForRecovery();
                }

                List<ServiceRecord> records;
                if (!TryConvertRecords(request, out records))
                {
                    _staging.Discard();
                    return PeerPushBatchProcessingResult.InvalidBatch();
                }

                var batch = new InboundSynchronizationBatch(
                    request.SnapshotId,
                    request.LogicalClock,
                    request.BatchIndex,
                    request.TotalCount,
                    request.IsLastBatch,
                    records);
                InboundSynchronizationStagingResult stagingResult =
                    _staging.Append(batch);
                if (!stagingResult.IsAccepted)
                {
                    return PeerPushBatchProcessingResult.StagingRejected(
                        stagingResult.Error);
                }

                if (!stagingResult.IsCompleted)
                {
                    return PeerPushBatchProcessingResult
                        .AwaitingMoreBatches();
                }

                Guid completedSnapshotId;
                SynchronizationSnapshot completedSnapshot;
                if (!_staging.TryGetCompletedSnapshot(
                        out completedSnapshotId,
                        out completedSnapshot)
                    || completedSnapshotId != request.SnapshotId
                    || completedSnapshot == null)
                {
                    _staging.Discard();
                    throw new InvalidOperationException(
                        "Completed Peer Push staging did not expose its validated snapshot.");
                }

                StateMutationResult<SynchronizationMergeResult> mutation;
                try
                {
                    mutation = _coordinator
                        .MergeVerifiedSynchronizationSnapshot(
                            completedSnapshot);
                }
                catch
                {
                    _staging.Discard();
                    throw;
                }

                return MapMutation(
                    mutation,
                    completedSnapshotId,
                    completedSnapshot);
            }
        }

        private PeerPushBatchProcessingResult MapMutation(
            StateMutationResult<SynchronizationMergeResult> mutation,
            Guid completedSnapshotId,
            SynchronizationSnapshot completedSnapshot)
        {
            if (mutation == null)
            {
                _staging.Discard();
                throw new InvalidOperationException(
                    "The state coordinator returned no Peer Push merge result.");
            }

            switch (mutation.Status)
            {
                case StateMutationStatus.Completed:
                    if (!mutation.HasDomainTransition)
                    {
                        _staging.Discard();
                        throw new InvalidOperationException(
                            "A completed Peer Push merge has no domain transition.");
                    }

                    if (!mutation.IsSuccessful)
                    {
                        DomainErrorCode? errorCode =
                            mutation.DomainTransition.ErrorCode;
                        _staging.Discard();
                        if (!errorCode.HasValue)
                        {
                            throw new InvalidOperationException(
                                "A rejected Peer Push merge has no domain error.");
                        }

                        return PeerPushBatchProcessingResult.DomainRejected(
                            errorCode.Value);
                    }

                    DirectorySnapshot currentSnapshot =
                        mutation.DomainTransition.NextSnapshot;
                    var outboundSnapshot = new SynchronizationSnapshot(
                        currentSnapshot.Records.Values,
                        currentSnapshot.LogicalClock);
                    return PeerPushBatchProcessingResult.Completed(
                        completedSnapshotId,
                        completedSnapshot,
                        currentSnapshot,
                        outboundSnapshot,
                        mutation.SnapshotPublished);

                case StateMutationStatus.PersistenceFailed:
                    _staging.Discard();
                    return PeerPushBatchProcessingResult.PersistenceFailed(
                        mutation.CommitFailureCode);

                case StateMutationStatus.RecoveryRequired:
                    _staging.Discard();
                    return PeerPushBatchProcessingResult.RecoveryRequired();

                case StateMutationStatus.BlockedForRecovery:
                    _staging.Discard();
                    return PeerPushBatchProcessingResult
                        .BlockedForRecovery();

                default:
                    _staging.Discard();
                    throw new InvalidOperationException(
                        "The state coordinator returned an unknown Peer Push merge status.");
            }
        }

        private static bool TryConvertRecords(
            PeerPushExchangeRequest request,
            out List<ServiceRecord> records)
        {
            records = new List<ServiceRecord>(request.Items.Count);
            for (int index = 0; index < request.Items.Count; index++)
            {
                PeerSyncServiceItem item = request.Items[index];
                if (item == null
                    || item.LastModifiedUtc.Kind != DateTimeKind.Utc
                    || item.Deleted != item.DeletedUtc.HasValue
                    || (item.DeletedUtc.HasValue
                        && item.DeletedUtc.Value.Kind != DateTimeKind.Utc)
                    || item.LogicalVersion == 0
                    || item.LogicalVersion > request.LogicalClock
                    || item.OriginInstanceId == Guid.Empty)
                {
                    records.Clear();
                    return false;
                }

                ServiceDefinition definition;
                ServiceDefinitionValidationError validationError;
                ServiceEndpointIdentity identity;
                EndpointIdentityValidationError identityError;
                if (!ServiceEndpointIdentity.TryCreate(
                        item.ServiceHostName,
                        item.ServiceIpv4Address,
                        out identity,
                        out identityError)
                    || !ServiceDefinition.TryCreate(
                        item.Name,
                        item.ProductCode,
                        identity,
                        item.Port,
                        out definition,
                        out validationError)
                    || !StringComparer.Ordinal.Equals(
                        definition.Name,
                        item.Name)
                    || !StringComparer.Ordinal.Equals(
                        definition.ProductCode.Value,
                        item.ProductCode)
                    || !StringComparer.Ordinal.Equals(
                        definition.ServiceHostName,
                        item.ServiceHostName)
                    || !StringComparer.Ordinal.Equals(
                        definition.ServiceIpv4Address,
                        item.ServiceIpv4Address))
                {
                    records.Clear();
                    return false;
                }

                records.Add(new ServiceRecord(
                    definition,
                    item.LastModifiedUtc,
                    item.Deleted,
                    item.DeletedUtc,
                    item.LogicalVersion,
                    item.OriginInstanceId));
            }

            return true;
        }
    }
}
