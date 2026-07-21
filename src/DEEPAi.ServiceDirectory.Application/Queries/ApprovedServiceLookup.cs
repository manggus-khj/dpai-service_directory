using System;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Application.Queries
{
    public enum ApprovedServiceLookupStatus
    {
        Found = 0,
        NotFound = 1,
        Unavailable = 2
    }

    public sealed class ApprovedServiceView
    {
        internal ApprovedServiceView(ServiceRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (record.Deleted)
            {
                throw new ArgumentException(
                    "An approved service view cannot expose a tombstone.",
                    nameof(record));
            }

            Name = record.Definition.Name;
            ProductCode = record.Definition.ProductCode.Value;
            ServiceHostName = record.Definition.ServiceHostName;
            ServiceIpv4Address = record.Definition.ServiceIpv4Address;
            Port = record.Definition.Port;
            LastModifiedUtc = record.LastModifiedUtc;
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public int Port { get; }

        public DateTime LastModifiedUtc { get; }
    }

    public sealed class ApprovedServiceLookupResult
    {
        private ApprovedServiceLookupResult(
            ApprovedServiceLookupStatus status,
            ApprovedServiceView service)
        {
            bool serviceRequired = status == ApprovedServiceLookupStatus.Found;
            if (!Enum.IsDefined(typeof(ApprovedServiceLookupStatus), status)
                || serviceRequired != (service != null))
            {
                throw new ArgumentException(
                    "The approved service lookup result contains an inconsistent state.");
            }

            Status = status;
            Service = service;
        }

        public ApprovedServiceLookupStatus Status { get; }

        public ApprovedServiceView Service { get; }

        public bool IsFound => Status == ApprovedServiceLookupStatus.Found;

        internal static ApprovedServiceLookupResult Found(ServiceRecord record)
        {
            return new ApprovedServiceLookupResult(
                ApprovedServiceLookupStatus.Found,
                new ApprovedServiceView(record));
        }

        internal static ApprovedServiceLookupResult NotFound()
        {
            return new ApprovedServiceLookupResult(
                ApprovedServiceLookupStatus.NotFound,
                null);
        }

        internal static ApprovedServiceLookupResult Unavailable()
        {
            return new ApprovedServiceLookupResult(
                ApprovedServiceLookupStatus.Unavailable,
                null);
        }
    }

    public sealed class ApprovedServiceLookup
    {
        private readonly StateMutationCoordinator _coordinator;

        public ApprovedServiceLookup(StateMutationCoordinator coordinator)
        {
            _coordinator = coordinator
                ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public ApprovedServiceLookupResult Find(ProductCode productCode)
        {
            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "Product code must be valid.",
                    nameof(productCode));
            }

            DirectorySnapshot snapshot;
            if (!_coordinator.TryGetReadySnapshot(out snapshot))
            {
                return ApprovedServiceLookupResult.Unavailable();
            }

            ServiceRecord record;
            return snapshot.TryGetActiveRecord(productCode, out record)
                ? ApprovedServiceLookupResult.Found(record)
                : ApprovedServiceLookupResult.NotFound();
        }
    }
}
