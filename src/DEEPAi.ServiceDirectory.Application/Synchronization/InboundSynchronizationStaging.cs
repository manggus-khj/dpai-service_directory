using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;

namespace DEEPAi.ServiceDirectory.Application.Synchronization
{
    // Authentication, raw-body HMAC verification, safe XML parsing, and XSD
    // validation are protocol-boundary responsibilities. This type represents
    // one typed SyncData batch after those checks have succeeded.
    public sealed class InboundSynchronizationBatch
    {
        private readonly IReadOnlyList<ServiceRecord> _records;

        public InboundSynchronizationBatch(
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            bool isLastBatch,
            IEnumerable<ServiceRecord> records)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            var copiedRecords = new List<ServiceRecord>(records);
            _records = new ReadOnlyCollection<ServiceRecord>(copiedRecords);
            SnapshotId = snapshotId;
            LogicalClock = logicalClock;
            BatchIndex = batchIndex;
            TotalCount = totalCount;
            IsLastBatch = isLastBatch;
        }

        public Guid SnapshotId { get; }

        public ulong LogicalClock { get; }

        public uint BatchIndex { get; }

        public ulong TotalCount { get; }

        public bool IsLastBatch { get; }

        public IReadOnlyList<ServiceRecord> Records => _records;
    }

    public enum InboundSynchronizationStagingState
    {
        Collecting = 0,
        Completed = 1,
        Discarded = 2
    }

    public enum InboundSynchronizationStagingError
    {
        None = 0,
        StagingNotCollecting = 1,
        InvalidSnapshotId = 2,
        BatchIndexMismatch = 3,
        SnapshotIdMismatch = 4,
        LogicalClockMismatch = 5,
        TotalCountMismatch = 6,
        BatchSizeExceeded = 7,
        NullRecord = 8,
        RecordVersionExceedsLogicalClock = 9,
        DuplicateProductCode = 10,
        RecordCountExceedsTotal = 11,
        LastBatchMismatch = 12,
        BatchIndexExhausted = 13,
        ProductCodeOrderMismatch = 14
    }

    public sealed class InboundSynchronizationStagingResult
    {
        private InboundSynchronizationStagingResult(
            bool isAccepted,
            bool isCompleted,
            InboundSynchronizationStagingError error)
        {
            bool validAccepted = isAccepted
                && error == InboundSynchronizationStagingError.None;
            bool validRejected = !isAccepted
                && !isCompleted
                && error != InboundSynchronizationStagingError.None
                && Enum.IsDefined(
                    typeof(InboundSynchronizationStagingError),
                    error);
            if ((!validAccepted && !validRejected)
                || (isCompleted && !isAccepted))
            {
                throw new ArgumentException(
                    "The inbound synchronization staging result is inconsistent.");
            }

            IsAccepted = isAccepted;
            IsCompleted = isCompleted;
            Error = error;
        }

        public bool IsAccepted { get; }

        public bool IsCompleted { get; }

        public InboundSynchronizationStagingError Error { get; }

        internal static InboundSynchronizationStagingResult Accepted(
            bool isCompleted)
        {
            return new InboundSynchronizationStagingResult(
                true,
                isCompleted,
                InboundSynchronizationStagingError.None);
        }

        internal static InboundSynchronizationStagingResult Rejected(
            InboundSynchronizationStagingError error)
        {
            return new InboundSynchronizationStagingResult(
                false,
                false,
                error);
        }
    }

    // One accumulator belongs to one authenticated sync session and one inbound
    // Push or Pull snapshot. It never publishes partial records or a partial
    // logical clock; the completed domain snapshot appears only after the exact
    // final batch has been accepted.
    public sealed class InboundSynchronizationStagingAccumulator
    {
        public const int MaximumRecordsPerBatch = 1000;

        private readonly object _gate = new object();
        private readonly List<ServiceRecord> _records =
            new List<ServiceRecord>();
        private readonly HashSet<ProductCode> _productCodes =
            new HashSet<ProductCode>();

        private InboundSynchronizationStagingState _state =
            InboundSynchronizationStagingState.Collecting;
        private bool _metadataInitialized;
        private Guid _snapshotId;
        private ulong _logicalClock;
        private ulong _totalCount;
        private ulong _receivedCount;
        private uint _nextBatchIndex;
        private bool _hasLastProductCode;
        private ProductCode _lastProductCode;
        private SynchronizationSnapshot _completedSnapshot;

        public InboundSynchronizationStagingState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public InboundSynchronizationStagingResult Append(
            InboundSynchronizationBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            lock (_gate)
            {
                if (_state != InboundSynchronizationStagingState.Collecting)
                {
                    DiscardCore();
                    return InboundSynchronizationStagingResult.Rejected(
                        InboundSynchronizationStagingError.StagingNotCollecting);
                }

                InboundSynchronizationStagingError validationError =
                    ValidateBatch(batch);
                if (validationError != InboundSynchronizationStagingError.None)
                {
                    return RejectAndDiscard(validationError);
                }

                if (!_metadataInitialized)
                {
                    _snapshotId = batch.SnapshotId;
                    _logicalClock = batch.LogicalClock;
                    _totalCount = batch.TotalCount;
                    _metadataInitialized = true;
                }

                foreach (ServiceRecord record in batch.Records)
                {
                    _records.Add(record);
                    _productCodes.Add(record.Definition.ProductCode);
                }

                if (batch.Records.Count != 0)
                {
                    _lastProductCode = batch.Records[
                        batch.Records.Count - 1].Definition.ProductCode;
                    _hasLastProductCode = true;
                }

                _receivedCount += (ulong)batch.Records.Count;
                bool isCompleted = _receivedCount == _totalCount;
                if (isCompleted)
                {
                    _completedSnapshot = new SynchronizationSnapshot(
                        _records,
                        _logicalClock);
                    _state = InboundSynchronizationStagingState.Completed;
                    _records.Clear();
                    _productCodes.Clear();
                }
                else
                {
                    _nextBatchIndex = batch.BatchIndex + 1U;
                }

                return InboundSynchronizationStagingResult.Accepted(isCompleted);
            }
        }

        public bool TryGetCompletedSnapshot(
            out Guid snapshotId,
            out SynchronizationSnapshot snapshot)
        {
            lock (_gate)
            {
                if (_state != InboundSynchronizationStagingState.Completed)
                {
                    snapshotId = Guid.Empty;
                    snapshot = null;
                    return false;
                }

                snapshotId = _snapshotId;
                snapshot = _completedSnapshot;
                return true;
            }
        }

        public void Discard()
        {
            lock (_gate)
            {
                DiscardCore();
            }
        }

        private InboundSynchronizationStagingError ValidateBatch(
            InboundSynchronizationBatch batch)
        {
            if (batch.SnapshotId == Guid.Empty)
            {
                return InboundSynchronizationStagingError.InvalidSnapshotId;
            }

            if (batch.Records.Count > MaximumRecordsPerBatch)
            {
                return InboundSynchronizationStagingError.BatchSizeExceeded;
            }

            if (!_metadataInitialized)
            {
                if (batch.BatchIndex != 0U)
                {
                    return InboundSynchronizationStagingError.BatchIndexMismatch;
                }
            }
            else
            {
                if (batch.BatchIndex != _nextBatchIndex)
                {
                    return InboundSynchronizationStagingError.BatchIndexMismatch;
                }

                if (batch.SnapshotId != _snapshotId)
                {
                    return InboundSynchronizationStagingError.SnapshotIdMismatch;
                }

                if (batch.LogicalClock != _logicalClock)
                {
                    return InboundSynchronizationStagingError.LogicalClockMismatch;
                }

                if (batch.TotalCount != _totalCount)
                {
                    return InboundSynchronizationStagingError.TotalCountMismatch;
                }
            }

            var batchProductCodes = new HashSet<ProductCode>();
            bool hasPreviousProductCode = _hasLastProductCode;
            ProductCode previousProductCode = _lastProductCode;
            foreach (ServiceRecord record in batch.Records)
            {
                if (record == null)
                {
                    return InboundSynchronizationStagingError.NullRecord;
                }

                if (record.LogicalVersion > batch.LogicalClock)
                {
                    return InboundSynchronizationStagingError
                        .RecordVersionExceedsLogicalClock;
                }

                ProductCode productCode = record.Definition.ProductCode;
                if (_productCodes.Contains(productCode)
                    || !batchProductCodes.Add(productCode))
                {
                    return InboundSynchronizationStagingError
                        .DuplicateProductCode;
                }

                if (hasPreviousProductCode
                    && StringComparer.Ordinal.Compare(
                        previousProductCode.Value,
                        productCode.Value) >= 0)
                {
                    return InboundSynchronizationStagingError
                        .ProductCodeOrderMismatch;
                }

                previousProductCode = productCode;
                hasPreviousProductCode = true;
            }

            ulong batchCount = (ulong)batch.Records.Count;
            if (_receivedCount > batch.TotalCount
                || batchCount > batch.TotalCount - _receivedCount)
            {
                return InboundSynchronizationStagingError
                    .RecordCountExceedsTotal;
            }

            ulong nextReceivedCount = _receivedCount + batchCount;
            bool expectedLastBatch = nextReceivedCount == batch.TotalCount;
            if (batch.IsLastBatch != expectedLastBatch)
            {
                return InboundSynchronizationStagingError.LastBatchMismatch;
            }

            if (!expectedLastBatch && batch.BatchIndex == uint.MaxValue)
            {
                return InboundSynchronizationStagingError.BatchIndexExhausted;
            }

            return InboundSynchronizationStagingError.None;
        }

        private InboundSynchronizationStagingResult RejectAndDiscard(
            InboundSynchronizationStagingError error)
        {
            DiscardCore();
            return InboundSynchronizationStagingResult.Rejected(error);
        }

        private void DiscardCore()
        {
            _state = InboundSynchronizationStagingState.Discarded;
            _metadataInitialized = false;
            _snapshotId = Guid.Empty;
            _logicalClock = 0UL;
            _totalCount = 0UL;
            _receivedCount = 0UL;
            _nextBatchIndex = 0U;
            _hasLastProductCode = false;
            _lastProductCode = default(ProductCode);
            _completedSnapshot = null;
            _records.Clear();
            _productCodes.Clear();
        }
    }
}
