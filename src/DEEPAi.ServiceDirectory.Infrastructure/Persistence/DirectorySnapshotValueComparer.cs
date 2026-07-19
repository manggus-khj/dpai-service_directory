using System;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal static class DirectorySnapshotValueComparer
    {
        internal static bool Equals(
            DirectorySnapshot left,
            DirectorySnapshot right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null
                || right == null
                || left.LogicalClock != right.LogicalClock
                || left.Records.Count != right.Records.Count
                || left.PendingById.Count != right.PendingById.Count)
            {
                return false;
            }

            foreach (var pair in left.Records)
            {
                ServiceRecord other;
                if (!right.Records.TryGetValue(pair.Key, out other)
                    || !RecordEquals(pair.Value, other))
                {
                    return false;
                }
            }

            foreach (var pair in left.PendingById)
            {
                PendingRegistration other;
                if (!right.PendingById.TryGetValue(pair.Key, out other)
                    || !PendingEquals(pair.Value, other))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RecordEquals(
            ServiceRecord left,
            ServiceRecord right)
        {
            return left != null
                && right != null
                && left.Equals(right)
                && left.LastModifiedUtc.Kind == right.LastModifiedUtc.Kind
                && (!left.DeletedUtc.HasValue
                    || left.DeletedUtc.Value.Kind
                        == right.DeletedUtc.Value.Kind);
        }

        private static bool PendingEquals(
            PendingRegistration left,
            PendingRegistration right)
        {
            if (left == null
                || right == null
                || left.Id != right.Id
                || left.Type != right.Type
                || left.RequestedUtc != right.RequestedUtc
                || left.RequestedUtc.Kind != right.RequestedUtc.Kind
                || !StringComparer.Ordinal.Equals(
                    left.SourceIp,
                    right.SourceIp)
                || !left.Requested.Equals(right.Requested)
                || !left.BaseRevision.Equals(right.BaseRevision))
            {
                return false;
            }

            ServiceRecord leftBase = left.BaseRevision.Record;
            ServiceRecord rightBase = right.BaseRevision.Record;
            return leftBase == null
                || RecordEquals(leftBase, rightBase);
        }
    }
}
