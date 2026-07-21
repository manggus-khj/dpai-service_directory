using System;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Tests.TestSupport
{
    internal static class TestData
    {
        internal static readonly Guid OriginA =
            new Guid("11111111-1111-1111-1111-111111111111");

        internal static readonly Guid OriginB =
            new Guid("22222222-2222-2222-2222-222222222222");

        internal static DateTime Utc(int minute)
        {
            return new DateTime(2026, 7, 18, 1, minute, 0, DateTimeKind.Utc);
        }

        internal static ProductCode ProductCode(string value)
        {
            ProductCode productCode;
            if (!DEEPAi.ServiceDirectory.Domain.ProductCode.TryCreate(
                value,
                out productCode))
            {
                throw new InvalidOperationException("The test product code is invalid.");
            }

            return productCode;
        }

        internal static ServiceDefinition Definition(
            string name = "Directory Service",
            string productCode = "AB12",
            string serviceHostName = "service.internal",
            string serviceIpv4Address = "10.20.30.40",
            int port = 21000)
        {
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError identityError;
            if (!ServiceEndpointIdentity.TryCreate(
                    serviceHostName,
                    serviceIpv4Address,
                    out identity,
                    out identityError))
            {
                throw new InvalidOperationException(
                    "The test service identity is invalid: "
                    + identityError
                    + ".");
            }

            ServiceDefinition definition;
            ServiceDefinitionValidationError error;
            if (!ServiceDefinition.TryCreate(
                name,
                productCode,
                identity,
                port,
                out definition,
                out error))
            {
                throw new InvalidOperationException(
                    "The test service definition is invalid: " + error + ".");
            }

            return definition;
        }

        internal static DirectoryEndpointIdentity DirectoryIdentity(
            string hostName = "management.internal",
            string ipv4Address = "10.20.30.40")
        {
            DirectoryEndpointIdentity identity;
            EndpointIdentityValidationError error;
            if (!DirectoryEndpointIdentity.TryCreate(
                    hostName,
                    ipv4Address,
                    out identity,
                    out error))
            {
                throw new InvalidOperationException(
                    "The test Directory identity is invalid: "
                    + error
                    + ".");
            }

            return identity;
        }

        internal static ServiceRecord ActiveRecord(
            ServiceDefinition definition,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            return ServiceRecord.CreateActive(
                definition,
                Utc(0),
                logicalVersion,
                originInstanceId);
        }
    }
}
