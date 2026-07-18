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
        ServerAddressRequired,
        ServerAddressInvalid,
        PortOutOfRange
    }

    public sealed class ServiceDefinition : IEquatable<ServiceDefinition>
    {
        internal ServiceDefinition(string name, ProductCode productCode, string serverAddress, int port)
        {
            Name = name;
            ProductCode = productCode;
            ServerAddress = serverAddress;
            Port = port;
        }

        public string Name { get; }

        public ProductCode ProductCode { get; }

        public string ServerAddress { get; }

        public int Port { get; }

        public static bool TryCreate(
            string name,
            string productCode,
            string serverAddress,
            int port,
            out ServiceDefinition definition,
            out ServiceDefinitionValidationError error)
        {
            return ServiceDefinitionValidator.TryCreate(
                name,
                productCode,
                serverAddress,
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
                && StringComparer.OrdinalIgnoreCase.Equals(ServerAddress, other.ServerAddress)
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
                hashCode = (hashCode * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(ServerAddress);
                hashCode = (hashCode * 397) ^ Port;
                return hashCode;
            }
        }
    }
}
