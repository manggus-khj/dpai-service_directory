using System;
using System.Reflection;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class ServiceDefinitionTests
    {
        [TestMethod]
        public void TryCreateUsesCanonicalServiceEndpointIdentityPair()
        {
            ServiceEndpointIdentity identity = CreateIdentity(
                "service-01.internal",
                "10.20.30.40");
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                " Directory ",
                " ab12 ",
                identity,
                21000,
                out definition,
                out error);

            Assert.IsTrue(created);
            Assert.AreEqual(ServiceDefinitionValidationError.None, error);
            Assert.AreEqual("Directory", definition.Name);
            Assert.AreEqual("AB12", definition.ProductCode.Value);
            Assert.AreSame(identity, definition.ServiceEndpointIdentity);
            Assert.AreEqual(
                "service-01.internal",
                definition.ServiceHostName);
            Assert.AreEqual(
                "10.20.30.40",
                definition.ServiceIpv4Address);
        }

        [TestMethod]
        public void TryCreateRejectsMissingServiceEndpointIdentity()
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                "Directory",
                "AB12",
                null,
                21000,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(
                ServiceDefinitionValidationError
                    .ServiceEndpointIdentityRequired,
                error);
        }

        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("169.254.1.1")]
        [DataRow("224.0.0.1")]
        [DataRow("255.255.255.255")]
        [DataRow("10.020.30.40")]
        [DataRow("2001:db8::1")]
        [DataRow("::ffff:10.20.30.40")]
        public void EndpointIdentityRejectsUnsupportedIpv4AndAllIpv6(
            string address)
        {
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError error;

            bool created = ServiceEndpointIdentity.TryCreate(
                "service.internal",
                address,
                out identity,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(identity);
            Assert.AreEqual(
                EndpointIdentityValidationError.ServiceIpv4AddressInvalid,
                error);
        }

        [TestMethod]
        public void ServiceDefinitionApiDoesNotAcceptDirectoryIdentity()
        {
            MethodInfo factory = typeof(ServiceDefinition).GetMethod(
                "TryCreate",
                BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(factory);
            ParameterInfo[] parameters = factory.GetParameters();
            Assert.AreEqual(
                typeof(ServiceEndpointIdentity),
                parameters[2].ParameterType);
            Assert.IsFalse(
                typeof(ServiceEndpointIdentity).IsAssignableFrom(
                    typeof(DirectoryEndpointIdentity)));
        }

        [TestMethod]
        public void ServiceRecordAndSynchronizationSnapshotPreserveWholePair()
        {
            ServiceEndpointIdentity identity = CreateIdentity(
                "service.internal",
                "10.20.30.40");
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;
            Assert.IsTrue(ServiceDefinition.TryCreate(
                "Directory",
                "AB12",
                identity,
                21000,
                out definition,
                out error));
            ServiceRecord record = ServiceRecord.CreateActive(
                definition,
                new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
                1,
                Guid.Parse("11111111-1111-1111-1111-111111111111"));
            var snapshot = new SynchronizationSnapshot(
                new[] { record },
                1);

            ServiceRecord captured = snapshot.Records[definition.ProductCode];
            Assert.AreSame(
                identity,
                captured.Definition.ServiceEndpointIdentity);
            Assert.AreEqual(
                "service.internal",
                captured.Definition.ServiceHostName);
            Assert.AreEqual(
                "10.20.30.40",
                captured.Definition.ServiceIpv4Address);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(65536)]
        public void TryCreateRejectsPortOutsideTcpRange(int port)
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                "Directory",
                "AB12",
                CreateIdentity("service.internal", "10.20.30.40"),
                port,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(
                ServiceDefinitionValidationError.PortOutOfRange,
                error);
        }

        [TestMethod]
        public void TryCreateRejectsControlCharacterInName()
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                "Directory\u0001Service",
                "AB12",
                CreateIdentity("service.internal", "10.20.30.40"),
                21000,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(
                ServiceDefinitionValidationError.NameContainsInvalidCharacter,
                error);
        }

        [TestMethod]
        [DataRow("Directory\ufffeService")]
        [DataRow("Directory\uffffService")]
        public void TryCreateRejectsCharactersThatXmlOneCannotPersist(
            string name)
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                name,
                "AB12",
                CreateIdentity("service.internal", "10.20.30.40"),
                21000,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(
                ServiceDefinitionValidationError.NameContainsInvalidCharacter,
                error);
        }

        private static ServiceEndpointIdentity CreateIdentity(
            string hostName,
            string ipv4Address)
        {
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError error;
            if (!ServiceEndpointIdentity.TryCreate(
                hostName,
                ipv4Address,
                out identity,
                out error))
            {
                Assert.Fail("The test service identity is invalid: " + error);
            }

            return identity;
        }
    }
}
