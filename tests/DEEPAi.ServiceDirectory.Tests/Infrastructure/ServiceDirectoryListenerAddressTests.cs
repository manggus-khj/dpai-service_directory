using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class ServiceDirectoryListenerAddressTests
    {
        [TestMethod]
        public void CanonicalUnicastIpv4CreatesExactHttpsPrefix()
        {
            ServiceDirectoryListenerAddress address;
            Assert.IsTrue(
                ServiceDirectoryListenerAddress.TryCreate(
                    "10.20.30.40",
                    out address));

            Assert.IsNotNull(address);
            Assert.AreEqual("10.20.30.40", address.CanonicalAddress);
            Assert.AreEqual(
                "https://10.20.30.40:21000/",
                address.HttpsPrefix);
        }

        [TestMethod]
        public void Ipv6IsRejectedByTargetV1Listener()
        {
            AssertRejected("2001:db8::10");
            AssertRejected("2001:db8::192.0.2.10");
        }

        [TestMethod]
        public void AddressParserRejectsWhitespaceHostnameAndUriSyntax()
        {
            AssertRejected(null);
            AssertRejected(string.Empty);
            AssertRejected(" 10.20.30.40");
            AssertRejected("10.20.30.40 ");
            AssertRejected("service.internal");
            AssertRejected("http://10.20.30.40");
            AssertRejected("10.20.30.40:21000");
            AssertRejected("[2001:db8::10]");
            AssertRejected("fe80::1%12");
        }

        [TestMethod]
        public void AddressParserRejectsAmbiguousOrInvalidIpv4()
        {
            AssertRejected("010.20.30.40");
            AssertRejected("10.020.30.40");
            AssertRejected("10.20.030.40");
            AssertRejected("10.20.30.040");
            AssertRejected("10.20.30");
            AssertRejected("10.20.30.40.50");
            AssertRejected("10.20.30.256");
            AssertRejected("10.20.30.-1");
        }

        [TestMethod]
        public void AddressParserRejectsWildcardLoopbackAndMulticast()
        {
            AssertRejected("0.0.0.0");
            AssertRejected("0.1.2.3");
            AssertRejected("127.0.0.1");
            AssertRejected("127.0.0.2");
            AssertRejected("224.0.0.1");
            AssertRejected("255.255.255.255");
            AssertRejected("::");
            AssertRejected("::1");
            AssertRejected("ff02::1");
        }

        [TestMethod]
        public void AddressParserRejectsLinkLocalAndIpv4MappedIpv6()
        {
            AssertRejected("fe80::1");
            AssertRejected("::ffff:192.0.2.10");
        }

        private static void AssertRejected(string value)
        {
            ServiceDirectoryListenerAddress address;
            Assert.IsFalse(
                ServiceDirectoryListenerAddress.TryCreate(
                    value,
                    out address),
                "Address should have been rejected: "
                    + (value ?? "<null>"));
            Assert.IsNull(address);
        }
    }
}
