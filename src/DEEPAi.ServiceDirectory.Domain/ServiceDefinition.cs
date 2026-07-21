using System;
using DEEPAi.ServiceDirectory.Domain.Validation;

namespace DEEPAi.ServiceDirectory.Domain
{
    public enum ServiceDefinitionValidationError
    {
        None = 0,
        NameRequired,
        NameTooLong,
        NameContainsInvalidCharacter,
        ProductCodeInvalid,
        ServiceEndpointIdentityRequired,
        PortOutOfRange
    }

    public sealed class ServiceDefinition : IEquatable<ServiceDefinition>
    {
        internal ServiceDefinition(
            string name,
            ProductCode productCode,
            ServiceEndpointIdentity serviceEndpointIdentity,
            int port)
        {
            Name = name;
            ProductCode = productCode;
            ServiceEndpointIdentity = serviceEndpointIdentity;
            Port = port;
        }

        public string Name { get; }

        public ProductCode ProductCode { get; }

        public ServiceEndpointIdentity ServiceEndpointIdentity { get; }

        public string ServiceHostName =>
            ServiceEndpointIdentity.ServiceHostName;

        public string ServiceIpv4Address =>
            ServiceEndpointIdentity.ServiceIpv4Address;

        public int Port { get; }

        public static bool TryCreate(
            string name,
            string productCode,
            ServiceEndpointIdentity serviceEndpointIdentity,
            int port,
            out ServiceDefinition definition,
            out ServiceDefinitionValidationError error)
        {
            return ServiceDefinitionValidator.TryCreate(
                name,
                productCode,
                serviceEndpointIdentity,
                port,
                out definition,
                out error);
        }

        public bool Equals(ServiceDefinition other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return StringComparer.Ordinal.Equals(Name, other.Name)
                && ProductCode.Equals(other.ProductCode)
                && ServiceEndpointIdentity.Equals(
                    other.ServiceEndpointIdentity)
                && Port == other.Port;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ServiceDefinition);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = StringComparer.Ordinal.GetHashCode(Name);
                hashCode = (hashCode * 397) ^ ProductCode.GetHashCode();
                hashCode = (hashCode * 397)
                    ^ ServiceEndpointIdentity.GetHashCode();
                hashCode = (hashCode * 397) ^ Port;
                return hashCode;
            }
        }
    }
}
