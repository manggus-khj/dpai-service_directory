using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public enum PeerOutboundBatchReadStatus
    {
        Served = 0,
        ServedReplay = 1,
        SnapshotMismatch = 2,
        UnexpectedBatchIndex = 3
    }

    public sealed class PeerOutboundBatchReadResult
    {
        private PeerOutboundBatchReadResult(
            PeerOutboundBatchReadStatus status,
            PeerPullExchangeBatch batch)
        {
            if (!Enum.IsDefined(typeof(PeerOutboundBatchReadStatus), status))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            bool served = status == PeerOutboundBatchReadStatus.Served
                || status == PeerOutboundBatchReadStatus.ServedReplay;
            if (served != (batch != null))
            {
                throw new ArgumentException(
                    "Only a served outbound batch result may contain a batch.",
                    nameof(batch));
            }

            Status = status;
            Batch = batch;
        }

        public PeerOutboundBatchReadStatus Status { get; }

        public bool IsServed => Batch != null;

        public PeerPullExchangeBatch Batch { get; }

        internal static PeerOutboundBatchReadResult Success(
            PeerPullExchangeBatch batch,
            bool replay)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            return new PeerOutboundBatchReadResult(
                replay
                    ? PeerOutboundBatchReadStatus.ServedReplay
                    : PeerOutboundBatchReadStatus.Served,
                batch);
        }

        internal static PeerOutboundBatchReadResult Failure(
            PeerOutboundBatchReadStatus status)
        {
            if (status == PeerOutboundBatchReadStatus.Served
                || status == PeerOutboundBatchReadStatus.ServedReplay)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            return new PeerOutboundBatchReadResult(status, null);
        }
    }

    // One lease is bound by its owner to one authenticated Peer session. It
    // retains the exact post-Push immutable snapshot until that session expires,
    // so retries can return the same batch data without observing later local
    // mutations. Authentication and session lifetime checks remain host duties.
    public sealed class PeerOutboundSnapshotLease
    {
        private readonly IReadOnlyList<PeerPullExchangeBatch> _batches;
        private readonly object _gate = new object();
        private uint _nextBatchIndex;

        public PeerOutboundSnapshotLease(
            Guid localInstanceId,
            Guid snapshotId,
            SynchronizationSnapshot snapshot)
        {
            if (localInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The local instance ID must not be empty.",
                    nameof(localInstanceId));
            }

            if (snapshotId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The outbound snapshot ID must not be empty.",
                    nameof(snapshotId));
            }

            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            LocalInstanceId = localInstanceId;
            SnapshotId = snapshotId;
            LogicalClock = snapshot.LogicalClock;
            _batches = BuildBatches(
                localInstanceId,
                snapshotId,
                snapshot);
        }

        public Guid LocalInstanceId { get; }

        public Guid SnapshotId { get; }

        public ulong LogicalClock { get; }

        public int BatchCount => _batches.Count;

        public uint NextBatchIndex
        {
            get
            {
                lock (_gate)
                {
                    return _nextBatchIndex;
                }
            }
        }

        public bool IsComplete
        {
            get
            {
                lock (_gate)
                {
                    return (ulong)_nextBatchIndex
                        == (ulong)_batches.Count;
                }
            }
        }

        public PeerOutboundBatchReadResult Read(
            PeerPullExchangeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            lock (_gate)
            {
                if (request.SnapshotId != SnapshotId)
                {
                    return PeerOutboundBatchReadResult.Failure(
                        PeerOutboundBatchReadStatus.SnapshotMismatch);
                }

                ulong requestedIndex = request.BatchIndex;
                if (requestedIndex >= (ulong)_batches.Count
                    || requestedIndex > _nextBatchIndex)
                {
                    return PeerOutboundBatchReadResult.Failure(
                        PeerOutboundBatchReadStatus.UnexpectedBatchIndex);
                }

                bool replay = requestedIndex < _nextBatchIndex;
                PeerPullExchangeBatch batch =
                    _batches[checked((int)request.BatchIndex)];
                if (!replay)
                {
                    _nextBatchIndex = checked(_nextBatchIndex + 1U);
                }

                return PeerOutboundBatchReadResult.Success(batch, replay);
            }
        }

        private static IReadOnlyList<PeerPullExchangeBatch> BuildBatches(
            Guid localInstanceId,
            Guid snapshotId,
            SynchronizationSnapshot snapshot)
        {
            var sortedRecords = new List<ServiceRecord>(
                snapshot.Records.Values);
            sortedRecords.Sort(
                (left, right) => string.CompareOrdinal(
                    left.Definition.ProductCode.Value,
                    right.Definition.ProductCode.Value));

            var items = new List<PeerSyncServiceItem>(sortedRecords.Count);
            for (int index = 0; index < sortedRecords.Count; index++)
            {
                ServiceRecord record = sortedRecords[index];
                items.Add(new PeerSyncServiceItem(
                    record.Definition.Name,
                    record.Definition.ProductCode.Value,
                    record.Definition.ServerAddress,
                    record.Definition.Port,
                    record.LastModifiedUtc,
                    record.Deleted,
                    record.DeletedUtc,
                    record.LogicalVersion,
                    record.OriginInstanceId));
            }

            ulong totalCount = checked((ulong)items.Count);
            var batches = new List<PeerPullExchangeBatch>();
            if (items.Count == 0)
            {
                batches.Add(CreateValidatedBatch(
                    localInstanceId,
                    snapshotId,
                    snapshot.LogicalClock,
                    0U,
                    totalCount,
                    true,
                    new List<PeerSyncServiceItem>()));
                return batches.AsReadOnly();
            }

            int offset = 0;
            uint batchIndex = 0;
            while (offset < items.Count)
            {
                int maximumCandidateCount = Math.Min(
                    PeerSyncContract.MaximumBatchItemCount,
                    items.Count - offset);
                PeerPullExchangeBatch batch = FindLargestFittingBatch(
                    localInstanceId,
                    snapshotId,
                    snapshot.LogicalClock,
                    batchIndex,
                    totalCount,
                    items,
                    offset,
                    maximumCandidateCount);
                batches.Add(batch);
                offset = checked(offset + batch.Items.Count);
                batchIndex = checked(batchIndex + 1U);
            }

            return batches.AsReadOnly();
        }

        private static PeerPullExchangeBatch FindLargestFittingBatch(
            Guid localInstanceId,
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            IReadOnlyList<PeerSyncServiceItem> allItems,
            int offset,
            int maximumCandidateCount)
        {
            PeerPullExchangeBatch largest = TryCreateValidatedBatch(
                localInstanceId,
                snapshotId,
                logicalClock,
                batchIndex,
                totalCount,
                allItems,
                offset,
                maximumCandidateCount);
            if (largest != null)
            {
                return largest;
            }

            int low = 1;
            int high = maximumCandidateCount - 1;
            int bestCount = 0;
            while (low <= high)
            {
                int candidateCount = low + ((high - low) / 2);
                PeerPullExchangeBatch candidate = TryCreateValidatedBatch(
                    localInstanceId,
                    snapshotId,
                    logicalClock,
                    batchIndex,
                    totalCount,
                    allItems,
                    offset,
                    candidateCount);
                if (candidate == null)
                {
                    high = candidateCount - 1;
                }
                else
                {
                    largest = candidate;
                    bestCount = candidateCount;
                    low = candidateCount + 1;
                }
            }

            if (bestCount == 0 || largest == null)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.BodyTooLarge,
                    "One Peer synchronization item cannot fit in the 4 MiB response envelope.");
            }

            return largest;
        }

        private static PeerPullExchangeBatch TryCreateValidatedBatch(
            Guid localInstanceId,
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            IReadOnlyList<PeerSyncServiceItem> allItems,
            int offset,
            int count)
        {
            var batchItems = new List<PeerSyncServiceItem>(count);
            for (int index = 0; index < count; index++)
            {
                batchItems.Add(allItems[offset + index]);
            }

            bool isLastBatch = offset + count == allItems.Count;
            try
            {
                return CreateValidatedBatch(
                    localInstanceId,
                    snapshotId,
                    logicalClock,
                    batchIndex,
                    totalCount,
                    isLastBatch,
                    batchItems);
            }
            catch (PeerSyncProtocolException exception)
                when (exception.Failure
                    == PeerSyncProtocolFailure.BodyTooLarge)
            {
                return null;
            }
        }

        private static PeerPullExchangeBatch CreateValidatedBatch(
            Guid localInstanceId,
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            bool isLastBatch,
            IReadOnlyList<PeerSyncServiceItem> items)
        {
            var batch = new PeerPullExchangeBatch(
                localInstanceId,
                snapshotId,
                logicalClock,
                batchIndex,
                totalCount,
                isLastBatch,
                items);
            PeerSyncXmlCodec.SerializeExchangeResponse(
                PeerExchangeResponse.CreatePullSuccess(batch));
            return batch;
        }
    }
}
