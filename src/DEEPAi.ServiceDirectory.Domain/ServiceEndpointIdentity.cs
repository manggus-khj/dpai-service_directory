using System;
using DEEPAi.ServiceDirectory.Domain.Validation;

namespace DEEPAi.ServiceDirectory.Domain
{
    public enum EndpointIdentityValidationError
    {
        None = 0,
        ServiceHostNameRequired,
        ServiceHostNameInvalid,
        ServiceIpv4AddressRequired,
        ServiceIpv4AddressInvalid
    }

    public sealed class ServiceEndpointIdentity : IEquatable<ServiceEndpointIdentity>
    {
        internal ServiceEndpointIdentity(
            string serviceHostName,
            string serviceIpv4Address)
        {
            ServiceHostName = serviceHostName;
            ServiceIpv4Address = serviceIpv4Address;
        }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public static bool TryCreate(
            string serviceHostName,
            string serviceIpv4Address,
            out ServiceEndpointIdentity identity,
            out EndpointIdentityValidationError error)
        {
            return ServiceEndpointIdentityValidator.TryCreate(
                serviceHostName,
                serviceIpv4Address,
                out identity,
                out error);
        }

        public bool Equals(ServiceEndpointIdentity other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return StringComparer.Ordinal.Equals(
                    ServiceHostName,
                    other.ServiceHostName)
                && StringComparer.Ordinal.Equals(
                    ServiceIpv4Address,
                    other.ServiceIpv4Address);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ServiceEndpointIdentity);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = StringComparer.Ordinal.GetHashCode(ServiceHostName);
                hashCode = (hashCode * 397)
                    ^ StringComparer.Ordinal.GetHashCode(ServiceIpv4Address);
                return hashCode;
            }
        }
    }
}
