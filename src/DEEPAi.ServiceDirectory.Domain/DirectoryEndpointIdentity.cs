using System;

namespace DEEPAi.ServiceDirectory.Domain
{
    public sealed class DirectoryEndpointIdentity : IEquatable<DirectoryEndpointIdentity>
    {
        private DirectoryEndpointIdentity(
            string directoryHostName,
            string directoryIpv4Address)
        {
            DirectoryHostName = directoryHostName;
            DirectoryIpv4Address = directoryIpv4Address;
        }

        public string DirectoryHostName { get; }

        public string DirectoryIpv4Address { get; }

        public static bool TryCreate(
            string directoryHostName,
            string directoryIpv4Address,
            out DirectoryEndpointIdentity identity,
            out EndpointIdentityValidationError error)
        {
            identity = null;

            ServiceEndpointIdentity validatedValues;
            if (!ServiceEndpointIdentity.TryCreate(
                    directoryHostName,
                    directoryIpv4Address,
                    out validatedValues,
                    out error))
            {
                return false;
            }

            identity = new DirectoryEndpointIdentity(
                validatedValues.ServiceHostName,
                validatedValues.ServiceIpv4Address);
            return true;
        }

        public bool Equals(DirectoryEndpointIdentity other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return StringComparer.Ordinal.Equals(
                    DirectoryHostName,
                    other.DirectoryHostName)
                && StringComparer.Ordinal.Equals(
                    DirectoryIpv4Address,
                    other.DirectoryIpv4Address);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DirectoryEndpointIdentity);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = StringComparer.Ordinal.GetHashCode(DirectoryHostName);
                hashCode = (hashCode * 397)
                    ^ StringComparer.Ordinal.GetHashCode(DirectoryIpv4Address);
                return hashCode;
            }
        }
    }
}
