using System;

namespace DEEPAi.ServiceDirectory.Domain
{
    public enum BaseRevisionKind
    {
        Missing,
        Active,
        Tombstone
    }

    public sealed class DirectoryBaseRevision : IEquatable<DirectoryBaseRevision>
    {
        private DirectoryBaseRevision(BaseRevisionKind kind, ServiceRecord record)
        {
            if (kind == BaseRevisionKind.Missing && record != null)
            {
                throw new ArgumentException("A missing revision cannot contain a record.", nameof(record));
            }

            if (kind == BaseRevisionKind.Active && (record == null || record.Deleted))
            {
                throw new ArgumentException("An active revision requires an active record.", nameof(record));
            }

            if (kind == BaseRevisionKind.Tombstone && (record == null || !record.Deleted))
            {
                throw new ArgumentException("A tombstone revision requires a deleted record.", nameof(record));
            }

            Kind = kind;
            Record = record;
        }

        public BaseRevisionKind Kind { get; }

        public ServiceRecord Record { get; }

        public static DirectoryBaseRevision Capture(ServiceRecord record)
        {
            if (record == null)
            {
                return new DirectoryBaseRevision(BaseRevisionKind.Missing, null);
            }

            return new DirectoryBaseRevision(
                record.Deleted ? BaseRevisionKind.Tombstone : BaseRevisionKind.Active,
                record);
        }

        public bool Matches(ServiceRecord current)
        {
            if (Kind == BaseRevisionKind.Missing)
            {
                return current == null;
            }

            return Record.Equals(current);
        }

        public bool Equals(DirectoryBaseRevision other)
        {
            if (ReferenceEquals(other, null) || Kind != other.Kind)
            {
                return false;
            }

            return ReferenceEquals(Record, null)
                ? ReferenceEquals(other.Record, null)
                : Record.Equals(other.Record);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DirectoryBaseRevision);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Kind * 397) ^ (Record == null ? 0 : Record.GetHashCode());
            }
        }
    }
}
