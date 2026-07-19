using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.Domain.Synchronization
{
    public sealed class SynchronizationSnapshot
    {
        private readonly IReadOnlyDictionary<ProductCode, ServiceRecord> _records;

        public SynchronizationSnapshot(
            IEnumerable<ServiceRecord> records,
            ulong logicalClock)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            var recordMap = new Dictionary<ProductCode, ServiceRecord>();
            foreach (ServiceRecord record in records)
            {
                if (record == null)
                {
                    throw new ArgumentException(
                        "Synchronization records cannot contain null entries.",
                        nameof(records));
                }

                ProductCode productCode = record.Definition.ProductCode;
                if (recordMap.ContainsKey(productCode))
                {
                    throw new ArgumentException(
                        "Synchronization records contain a duplicate product code.",
                        nameof(records));
                }

                if (record.LogicalVersion > logicalClock)
                {
                    throw new ArgumentException(
                        "A synchronization record version cannot exceed the snapshot logical clock.",
                        nameof(records));
                }

                recordMap.Add(productCode, record);
            }

            _records = new ReadOnlyDictionary<ProductCode, ServiceRecord>(recordMap);
            LogicalClock = logicalClock;
        }

        public IReadOnlyDictionary<ProductCode, ServiceRecord> Records => _records;

        public ulong LogicalClock { get; }
    }
}
