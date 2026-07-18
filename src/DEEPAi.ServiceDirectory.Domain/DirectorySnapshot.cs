using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.Domain
{
    public sealed class DirectorySnapshot
    {
        public const int ActiveServiceLimit = 1000;
        public const int PendingRegistrationLimit = 1000;
        public const int PendingRegistrationWarningThreshold = 800;

        private readonly IReadOnlyDictionary<ProductCode, ServiceRecord> _records;
        private readonly IReadOnlyDictionary<Guid, PendingRegistration> _pendingById;
        private readonly IReadOnlyDictionary<ProductCode, Guid> _pendingIdByProductCode;

        public DirectorySnapshot(
            IEnumerable<ServiceRecord> records,
            IEnumerable<PendingRegistration> pendingRegistrations,
            ulong logicalClock)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            if (pendingRegistrations == null)
            {
                throw new ArgumentNullException(nameof(pendingRegistrations));
            }

            var recordMap = new Dictionary<ProductCode, ServiceRecord>();
            int activeCount = 0;
            foreach (ServiceRecord record in records)
            {
                if (record == null)
                {
                    throw new ArgumentException("Records cannot contain null entries.", nameof(records));
                }

                ProductCode productCode = record.Definition.ProductCode;
                if (recordMap.ContainsKey(productCode))
                {
                    throw new ArgumentException("Records contain a duplicate product code.", nameof(records));
                }

                recordMap.Add(productCode, record);
                if (record.LogicalVersion > logicalClock)
                {
                    throw new ArgumentException(
                        "A record change version cannot exceed the snapshot logical clock.",
                        nameof(records));
                }

                if (!record.Deleted)
                {
                    activeCount++;
                }
            }

            if (activeCount > ActiveServiceLimit)
            {
                throw new ArgumentException("Active service count exceeds the supported limit.", nameof(records));
            }

            var pendingById = new Dictionary<Guid, PendingRegistration>();
            var pendingIdByProductCode = new Dictionary<ProductCode, Guid>();
            foreach (PendingRegistration pending in pendingRegistrations)
            {
                if (pending == null)
                {
                    throw new ArgumentException("Pending registrations cannot contain null entries.", nameof(pendingRegistrations));
                }

                if (pendingById.ContainsKey(pending.Id))
                {
                    throw new ArgumentException("Pending registrations contain a duplicate ID.", nameof(pendingRegistrations));
                }

                ProductCode productCode = pending.Requested.ProductCode;
                if (pendingIdByProductCode.ContainsKey(productCode))
                {
                    throw new ArgumentException("Only one pending registration is allowed per product code.", nameof(pendingRegistrations));
                }

                ServiceRecord baseRecord = pending.BaseRevision.Record;
                if (baseRecord != null && baseRecord.LogicalVersion > logicalClock)
                {
                    throw new ArgumentException(
                        "A pending base revision cannot exceed the snapshot logical clock.",
                        nameof(pendingRegistrations));
                }

                ServiceRecord currentRecord;
                if (baseRecord != null
                    && recordMap.TryGetValue(productCode, out currentRecord)
                    && baseRecord.LogicalVersion == currentRecord.LogicalVersion
                    && baseRecord.OriginInstanceId == currentRecord.OriginInstanceId
                    && !baseRecord.Equals(currentRecord))
                {
                    throw new ArgumentException(
                        "A pending base revision collides with the current record.",
                        nameof(pendingRegistrations));
                }

                pendingById.Add(pending.Id, pending);
                pendingIdByProductCode.Add(productCode, pending.Id);
            }

            if (pendingById.Count > PendingRegistrationLimit)
            {
                throw new ArgumentException("Pending registration count exceeds the supported limit.", nameof(pendingRegistrations));
            }

            _records = new ReadOnlyDictionary<ProductCode, ServiceRecord>(recordMap);
            _pendingById = new ReadOnlyDictionary<Guid, PendingRegistration>(pendingById);
            _pendingIdByProductCode = new ReadOnlyDictionary<ProductCode, Guid>(pendingIdByProductCode);
            ActiveCount = activeCount;
            LogicalClock = logicalClock;
        }

        public IReadOnlyDictionary<ProductCode, ServiceRecord> Records => _records;

        public IReadOnlyDictionary<Guid, PendingRegistration> PendingById => _pendingById;

        public int ActiveCount { get; }

        public int PendingCount => _pendingById.Count;

        public bool IsPendingWarningThresholdReached =>
            PendingCount >= PendingRegistrationWarningThreshold;

        public ulong LogicalClock { get; }

        public static DirectorySnapshot Empty()
        {
            return new DirectorySnapshot(
                new ServiceRecord[0],
                new PendingRegistration[0],
                0UL);
        }

        public bool TryGetRecord(ProductCode productCode, out ServiceRecord record)
        {
            EnsureValidProductCode(productCode);
            return _records.TryGetValue(productCode, out record);
        }

        public bool TryGetActiveRecord(ProductCode productCode, out ServiceRecord record)
        {
            if (!TryGetRecord(productCode, out record) || record.Deleted)
            {
                record = null;
                return false;
            }

            return true;
        }

        public bool TryGetPending(Guid pendingId, out PendingRegistration pending)
        {
            return _pendingById.TryGetValue(pendingId, out pending);
        }

        public bool TryGetPending(ProductCode productCode, out PendingRegistration pending)
        {
            EnsureValidProductCode(productCode);

            Guid pendingId;
            if (_pendingIdByProductCode.TryGetValue(productCode, out pendingId))
            {
                return _pendingById.TryGetValue(pendingId, out pending);
            }

            pending = null;
            return false;
        }

        private static void EnsureValidProductCode(ProductCode productCode)
        {
            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "Product code must be valid.",
                    nameof(productCode));
            }
        }
    }
}
