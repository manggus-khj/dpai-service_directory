using DEEPAi.ServiceDirectory.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class EndpointIdentityTests
    {
        [TestMethod]
        public void ServiceIdentityNormalizesHostNameAndPreservesCanonicalIpv4()
        {
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError error;

            bool created = ServiceEndpointIdentity.TryCreate(
                "  VMS-Bridge.Example.Local  ",
                " 10.20.30.40 ",
                out identity,
                out error);

            Assert.IsTrue(created);
            Assert.AreEqual(EndpointIdentityValidationError.None, error);
            Assert.AreEqual("vms-bridge.example.local", identity.ServiceHostName);
            Assert.AreEqual("10.20.30.40", identity.ServiceIpv4Address);
        }

        [TestMethod]
        public void DirectoryAndServiceIdentitiesRemainDistinctTypes()
        {
            DirectoryEndpointIdentity directoryIdentity;
            ServiceEndpointIdentity serviceIdentity;
            EndpointIdentityValidationError error;

            Assert.IsTrue(DirectoryEndpointIdentity.TryCreate(
                "management.example.local",
                "10.0.0.10",
                out directoryIdentity,
                out error));
            Assert.IsTrue(ServiceEndpointIdentity.TryCreate(
                "bridge.example.local",
                "10.0.0.20",
                out serviceIdentity,
                out error));

            Assert.AreEqual("management.example.local", directoryIdentity.DirectoryHostName);
            Assert.AreEqual("10.0.0.10", directoryIdentity.DirectoryIpv4Address);
            Assert.AreEqual("bridge.example.local", serviceIdentity.ServiceHostName);
            Assert.AreEqual("10.0.0.20", serviceIdentity.ServiceIpv4Address);
            Assert.IsFalse(directoryIdentity.Equals(serviceIdentity));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("service.example.local.")]
        [DataRow("*.example.local")]
        [DataRow("service_name.example.local")]
        [DataRow("10.20.30.40")]
        [DataRow("12345")]
        [DataRow("http://service.example.local")]
        [DataRow("service.example.local:21500")]
        [DataRow("한글.example.local")]
        public void ServiceIdentityRejectsInvalidHostNames(string hostName)
        {
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError error;

            Assert.IsFalse(ServiceEndpointIdentity.TryCreate(
                hostName,
                "10.20.30.40",
                out identity,
                out error));
            Assert.IsNull(identity);
            Assert.AreNotEqual(EndpointIdentityValidationError.None, error);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("0.0.0.0")]
        [DataRow("127.0.0.1")]
        [DataRow("169.254.1.10")]
        [DataRow("224.0.0.1")]
        [DataRow("239.255.255.250")]
        [DataRow("255.255.255.255")]
        [DataRow("010.20.30.40")]
        [DataRow("10.20.30")]
        [DataRow("10.20.30.256")]
        [DataRow("::ffff:10.20.30.40")]
        [DataRow("2001:db8::1")]
        public void ServiceIdentityRejectsUnsupportedIpv4Values(string ipv4Address)
        {
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError error;

            Assert.IsFalse(ServiceEndpointIdentity.TryCreate(
                "service.example.local",
                ipv4Address,
                out identity,
                out error));
            Assert.IsNull(identity);
            Assert.AreNotEqual(EndpointIdentityValidationError.None, error);
        }
    }
}
