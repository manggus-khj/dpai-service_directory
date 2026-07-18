using System;
using System.Collections.Generic;

namespace DEEPAi.ServiceDirectory.Domain
{
    public sealed class RevisionCollisionException : InvalidOperationException
    {
        internal RevisionCollisionException()
            : base("Records with the same logical revision identity contain different payloads.")
        {
        }
    }

    public sealed class ServiceRecordRevisionComparer : IComparer<ServiceRecord>
    {
        public static readonly ServiceRecordRevisionComparer Instance =
            new ServiceRecordRevisionComparer();

        private ServiceRecordRevisionComparer()
        {
        }

        public int Compare(ServiceRecord x, ServiceRecord y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (ReferenceEquals(x, null))
            {
                return -1;
            }

            if (ReferenceEquals(y, null))
            {
                return 1;
            }

            int versionComparison = x.LogicalVersion.CompareTo(y.LogicalVersion);
            if (versionComparison != 0)
            {
                return versionComparison;
            }

            int originComparison = string.CompareOrdinal(
                x.OriginInstanceId.ToString("D"),
                y.OriginInstanceId.ToString("D"));
            if (originComparison != 0)
            {
                return originComparison;
            }

            if (!x.Equals(y))
            {
                throw new RevisionCollisionException();
            }

            return 0;
        }
    }
}
