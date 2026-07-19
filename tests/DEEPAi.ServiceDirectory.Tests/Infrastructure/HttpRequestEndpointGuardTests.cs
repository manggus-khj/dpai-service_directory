using System.Net;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class HttpRequestEndpointGuardTests
    {
        [TestMethod]
        public void ConfiguredIpv4EndpointRequiresExactAddressAndPort()
        {
            ServiceDirectoryListenerAddress configured =
                CreateAddress("10.20.30.40");

            Assert.IsTrue(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("10.20.30.40", 21000),
                    configured));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("10.20.30.41", 21000),
                    configured));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("10.20.30.40", 21001),
                    configured));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    (IPEndPoint)null,
                    configured));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("10.20.30.40", 21000),
                    null));
        }

        [TestMethod]
        public void ConfiguredIpv6EndpointRequiresExactFamilyAddressAndPort()
        {
            ServiceDirectoryListenerAddress configured =
                CreateAddress("2001:db8::10");

            Assert.IsTrue(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("2001:db8::10", 21000),
                    configured));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("2001:db8::11", 21000),
                    configured));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("10.0.0.10", 21000),
                    configured));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    Endpoint("2001:db8::10", 80),
                    configured));
        }

        [TestMethod]
        public void LoopbackScopeRequiresExactIpv4LocalEndpoint()
        {
            Assert.IsTrue(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    Endpoint("127.0.0.1", 21000),
                    Endpoint("127.0.0.1", 50000)));
            Assert.IsTrue(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    Endpoint("127.0.0.1", 21000),
                    Endpoint("::1", 50000)));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    Endpoint("::1", 21000),
                    Endpoint("::1", 50000)));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    Endpoint("127.0.0.1", 21001),
                    Endpoint("127.0.0.1", 50000)));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    Endpoint("127.0.0.2", 21000),
                    Endpoint("127.0.0.1", 50000)));
        }

        [TestMethod]
        public void LoopbackScopeFailsClosedWithoutLoopbackRemoteEndpoint()
        {
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    Endpoint("127.0.0.1", 21000),
                    Endpoint("10.0.0.5", 50000)));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    Endpoint("127.0.0.1", 21000),
                    null));
            Assert.IsFalse(
                HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    null,
                    Endpoint("127.0.0.1", 50000)));
        }

        private static ServiceDirectoryListenerAddress CreateAddress(
            string value)
        {
            ServiceDirectoryListenerAddress result;
            Assert.IsTrue(
                ServiceDirectoryListenerAddress.TryCreate(
                    value,
                    out result));
            Assert.IsNotNull(result);
            return result;
        }

        private static IPEndPoint Endpoint(string address, int port)
        {
            return new IPEndPoint(IPAddress.Parse(address), port);
        }
    }
}
