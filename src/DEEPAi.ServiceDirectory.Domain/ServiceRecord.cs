using System;

namespace DEEPAi.ServiceDirectory.Domain
{
    public sealed class ServiceRecord : IEquatable<ServiceRecord>
    {
        public ServiceRecord(
            ServiceDefinition definition,
            DateTime lastModifiedUtc,
            bool deleted,
            DateTime? deletedUtc,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            EnsureUtc(lastModifiedUtc, nameof(lastModifiedUtc));
            if (logicalVersion == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalVersion),
                    logicalVersion,
                    "Logical version must be greater than zero.");
            }

            if (originInstanceId == Guid.Empty)
            {
                throw new ArgumentException("Origin instance ID must not be empty.", nameof(originInstanceId));
            }

            if (deleted != deletedUtc.HasValue)
            {
                throw new ArgumentException("Deleted and DeletedUtc must describe the same state.", nameof(deletedUtc));
            }

            if (deletedUtc.HasValue)
            {
                EnsureUtc(deletedUtc.Value, nameof(deletedUtc));
            }

            Definition = definition;
            LastModifiedUtc = lastModifiedUtc;
            Deleted = deleted;
            DeletedUtc = deletedUtc;
            LogicalVersion = logicalVersion;
            OriginInstanceId = originInstanceId;
        }

        public ServiceDefinition Definition { get; }

        public DateTime LastModifiedUtc { get; }

        public bool Deleted { get; }

        public DateTime? DeletedUtc { get; }

        public ulong LogicalVersion { get; }

        public Guid OriginInstanceId { get; }

        public static ServiceRecord CreateActive(
            ServiceDefinition definition,
            DateTime lastModifiedUtc,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            return new ServiceRecord(
                definition,
                lastModifiedUtc,
                false,
                null,
                logicalVersion,
                originInstanceId);
        }

        public ServiceRecord MarkDeleted(
            DateTime deletedUtc,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            if (Deleted)
            {
                throw new InvalidOperationException("A tombstone cannot be deleted again.");
            }

            return new ServiceRecord(
                Definition,
                LastModifiedUtc,
                true,
                deletedUtc,
                logicalVersion,
                originInstanceId);
        }

        public bool Equals(ServiceRecord other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Definition.Equals(other.Definition)
                && LastModifiedUtc == other.LastModifiedUtc
                && Deleted == other.Deleted
                && DeletedUtc == other.DeletedUtc
                && LogicalVersion == other.LogicalVersion
                && OriginInstanceId == other.OriginInstanceId;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ServiceRecord);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = Definition.GetHashCode();
                hashCode = (hashCode * 397) ^ LastModifiedUtc.GetHashCode();
                hashCode = (hashCode * 397) ^ Deleted.GetHashCode();
                hashCode = (hashCode * 397) ^ DeletedUtc.GetHashCode();
                hashCode = (hashCode * 397) ^ LogicalVersion.GetHashCode();
                hashCode = (hashCode * 397) ^ OriginInstanceId.GetHashCode();
                return hashCode;
            }
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Domain timestamps must use DateTimeKind.Utc.", parameterName);
            }
        }
    }
}
