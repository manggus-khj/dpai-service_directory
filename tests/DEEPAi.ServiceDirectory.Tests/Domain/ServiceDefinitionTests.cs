using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class ServiceDefinitionTests
    {
        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("2001:db8::1")]
        [DataRow("service-01.internal")]
        public void TryCreateAcceptsSupportedServerAddressForms(string address)
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                " Directory ",
                " ab12 ",
                " " + address + " ",
                21000,
                out definition,
                out error);

            Assert.IsTrue(created);
            Assert.AreEqual(ServiceDefinitionValidationError.None, error);
            Assert.AreEqual("Directory", definition.Name);
            Assert.AreEqual("AB12", definition.ProductCode.Value);
            Assert.AreEqual(address, definition.ServerAddress);
        }

        [TestMethod]
        [DataRow("http://service.internal")]
        [DataRow("service.internal:21000")]
        [DataRow("[2001:db8::1]")]
        [DataRow("fe80::1%12")]
        [DataRow("010.20.30.40")]
        [DataRow("12345")]
        [DataRow("service..internal")]
        [DataRow("서비스.internal")]
        public void TryCreateRejectsAmbiguousOrUnsupportedServerAddresses(string address)
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                "Directory",
                "AB12",
                address,
                21000,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(ServiceDefinitionValidationError.ServerAddressInvalid, error);
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
                "service.internal",
                port,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(ServiceDefinitionValidationError.PortOutOfRange, error);
        }

        [TestMethod]
        public void TryCreateRejectsControlCharacterInName()
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;

            bool created = ServiceDefinition.TryCreate(
                "Directory\u0001Service",
                "AB12",
                "service.internal",
                21000,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(ServiceDefinitionValidationError.NameContainsInvalidCharacter, error);
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
                "service.internal",
                21000,
                out definition,
                out error);

            Assert.IsFalse(created);
            Assert.IsNull(definition);
            Assert.AreEqual(
                ServiceDefinitionValidationError.NameContainsInvalidCharacter,
                error);
        }
    }
}
