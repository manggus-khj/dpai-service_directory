using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class HttpListenerRequestRoutingTests
    {
        private static readonly IPEndPoint LoopbackEndpoint =
            new IPEndPoint(IPAddress.Loopback, 21000);
        private static readonly IPEndPoint LoopbackRemoteEndpoint =
            new IPEndPoint(IPAddress.Loopback, 49152);
        private static readonly IPEndPoint ConfiguredEndpoint =
            new IPEndPoint(IPAddress.Parse("10.20.30.40"), 21000);
        private static readonly IPEndPoint RemoteEndpoint =
            new IPEndPoint(IPAddress.Parse("10.20.30.50"), 49152);

        [TestMethod]
        public void NegotiateIsSelectedOnlyForExactLoopbackAdminScope()
        {
            var request = new StubRequest
            {
                RawUrl = "/admin/services?pageSize=100",
                LocalEndPoint = LoopbackEndpoint,
                RemoteEndPoint = LoopbackRemoteEndpoint
            };

            Assert.AreEqual(
                AuthenticationSchemes.Negotiate,
                HttpListenerRequestRouting.SelectAuthentication(request));

            request.LocalEndPoint = ConfiguredEndpoint;
            request.RemoteEndPoint = RemoteEndpoint;
            Assert.AreEqual(
                AuthenticationSchemes.Anonymous,
                HttpListenerRequestRouting.SelectAuthentication(request));

            request.LocalEndPoint = LoopbackEndpoint;
            request.RemoteEndPoint = RemoteEndpoint;
            Assert.AreEqual(
                AuthenticationSchemes.Anonymous,
                HttpListenerRequestRouting.SelectAuthentication(request));
        }

        [TestMethod]
        public void NonAdminEncodedAndInvalidTargetsRemainAnonymous()
        {
            var request = new StubRequest
            {
                LocalEndPoint = LoopbackEndpoint,
                RemoteEndPoint = LoopbackRemoteEndpoint
            };

            string[] rawUrls =
            {
                "/api/health",
                "/admin%2fservices",
                "/Admin/services",
                "/admin",
                "http://127.0.0.1:21000/admin/services",
                "/admin/services#fragment"
            };

            foreach (string rawUrl in rawUrls)
            {
                request.RawUrl = rawUrl;
                Assert.AreEqual(
                    AuthenticationSchemes.Anonymous,
                    HttpListenerRequestRouting.SelectAuthentication(request),
                    rawUrl);
            }

            Assert.AreEqual(
                AuthenticationSchemes.Anonymous,
                HttpListenerRequestRouting.SelectAuthentication(null));
        }

        [TestMethod]
        public void ConfiguredListenerRoutesOnlyExternalCandidates()
        {
            ServiceDirectoryListenerAddress configuredAddress =
                CreateConfiguredAddress();

            AssertRoute(
                configuredAddress,
                ConfiguredEndpoint,
                "/api/health",
                ServiceDirectoryHttpRoute.External);
            AssertRoute(
                configuredAddress,
                ConfiguredEndpoint,
                "/api%2fhealth",
                ServiceDirectoryHttpRoute.External);
            AssertRoute(
                configuredAddress,
                ConfiguredEndpoint,
                "/api/sync",
                ServiceDirectoryHttpRoute.NotFound);
            AssertRoute(
                configuredAddress,
                ConfiguredEndpoint,
                "/api/sync/exchange",
                ServiceDirectoryHttpRoute.NotFound);
            AssertRoute(
                configuredAddress,
                ConfiguredEndpoint,
                "/admin/services",
                ServiceDirectoryHttpRoute.NotFound);
            AssertRoute(
                configuredAddress,
                ConfiguredEndpoint,
                "/not-an-api",
                ServiceDirectoryHttpRoute.NotFound);
        }

        [TestMethod]
        public void LoopbackListenerSeparatesAdminAndWatchdogCandidates()
        {
            ServiceDirectoryListenerAddress configuredAddress =
                CreateConfiguredAddress();

            AssertRoute(
                configuredAddress,
                LoopbackEndpoint,
                "/admin/services",
                ServiceDirectoryHttpRoute.Admin);
            AssertRoute(
                configuredAddress,
                LoopbackEndpoint,
                "/api/health",
                ServiceDirectoryHttpRoute.WatchdogHealth);
            AssertRoute(
                configuredAddress,
                LoopbackEndpoint,
                "/api/services",
                ServiceDirectoryHttpRoute.WatchdogHealth);
            AssertRoute(
                configuredAddress,
                LoopbackEndpoint,
                "/api%2fhealth",
                ServiceDirectoryHttpRoute.WatchdogHealth);
            AssertRoute(
                configuredAddress,
                LoopbackEndpoint,
                "/api/sync/handshake",
                ServiceDirectoryHttpRoute.NotFound);
            AssertRoute(
                configuredAddress,
                LoopbackEndpoint,
                "/not-an-api",
                ServiceDirectoryHttpRoute.NotFound);

            AssertRoute(
                configuredAddress,
                new IPEndPoint(IPAddress.Loopback, 21001),
                "/admin/services",
                ServiceDirectoryHttpRoute.NotFound);
        }

        [TestMethod]
        public void MapperPreservesRawTargetHeadersEndpointsAndPrincipal()
        {
            var body = new MemoryStream(new byte[] { 1, 2, 3 });
            var principal = new GenericPrincipal(
                new GenericIdentity("operator", "Negotiate"),
                new string[0]);
            var apiKeys = new[] { "first", "second" };
            var contentEncodings = new[] { "identity", "gzip" };
            var request = new StubRequest
            {
                RawUrl = "/api/services?productCode=AB12",
                HttpMethod = "GET",
                ContentType = "application/xml; charset=utf-8",
                ContentLength64 = 3,
                InputStream = body,
                LocalEndPoint = ConfiguredEndpoint,
                RemoteEndPoint = RemoteEndpoint
            };
            request.SetHeaderValues(
                ExternalApiContract.ApiKeyHeaderName,
                apiKeys);
            request.SetHeaderValues("Content-Encoding", contentEncodings);
            var context = new StubContext(request, principal);
            RawHttpRequestTarget target = Parse(request.RawUrl);

            ExternalHttpRequestData external =
                HttpListenerRequestMapper.ToExternal(context, target);
            AdminHttpRequestData admin =
                HttpListenerRequestMapper.ToAdmin(context, target);

            Assert.AreEqual("GET", external.Method);
            Assert.AreEqual("/api/services", external.AbsolutePath);
            Assert.AreEqual("?productCode=AB12", external.RawQuery);
            Assert.AreEqual(2, external.ApiKeyHeaderValues.Count);
            Assert.AreEqual("first", external.ApiKeyHeaderValues[0]);
            Assert.AreEqual("identity,gzip",
                external.ContentEncodingHeaderValue);
            Assert.AreSame(body, external.BodyStream);
            Assert.AreEqual(ConfiguredEndpoint, external.LocalEndpoint);
            Assert.AreEqual(RemoteEndpoint, external.RemoteEndpoint);

            Assert.AreEqual("/api/services", admin.AbsolutePath);
            Assert.AreEqual("?productCode=AB12", admin.RawQuery);
            Assert.AreEqual("identity,gzip",
                admin.ContentEncodingHeaderValue);
            Assert.AreSame(body, admin.BodyStream);
            Assert.AreSame(principal, admin.Principal);

            apiKeys[0] = "changed";
            Assert.AreEqual("first", external.ApiKeyHeaderValues[0]);
        }

        [TestMethod]
        public void DeadlinePolicyUsesExactMethodAndRawPath()
        {
            Assert.AreEqual(
                TimeSpan.FromSeconds(10),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.External,
                    "POST",
                    "/api/registration"));
            Assert.AreEqual(
                TimeSpan.FromSeconds(5),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.External,
                    "post",
                    "/api/registration"));
            Assert.AreEqual(
                TimeSpan.FromSeconds(5),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.External,
                    "POST",
                    "/api%2fregistration"));
            Assert.AreEqual(
                TimeSpan.FromSeconds(10),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.Admin,
                    "GET",
                    "/admin/services"));
            Assert.AreEqual(
                TimeSpan.FromSeconds(5),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.WatchdogHealth,
                    "GET",
                    "/api/health"));
        }

        [TestMethod]
        public void DeadlineWaiterRejectsNonPositiveTimeout()
        {
            var waiter = new SystemHttpDeadlineWaiter();

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => waiter.WaitAsync(
                    TimeSpan.Zero,
                    CancellationToken.None));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => waiter.WaitAsync(
                    TimeSpan.FromMilliseconds(-1),
                    CancellationToken.None));
        }

        private static ServiceDirectoryListenerAddress
            CreateConfiguredAddress()
        {
            ServiceDirectoryListenerAddress configuredAddress;
            Assert.IsTrue(
                ServiceDirectoryListenerAddress.TryCreate(
                    "10.20.30.40",
                    out configuredAddress));
            return configuredAddress;
        }

        private static void AssertRoute(
            ServiceDirectoryListenerAddress configuredAddress,
            IPEndPoint localEndpoint,
            string rawUrl,
            ServiceDirectoryHttpRoute expected)
        {
            RawHttpRequestTarget target = Parse(rawUrl);
            Assert.AreEqual(
                expected,
                HttpListenerRequestRouting.ResolveRoute(
                    localEndpoint,
                    IPAddress.IsLoopback(localEndpoint.Address)
                        ? LoopbackRemoteEndpoint
                        : RemoteEndpoint,
                    target,
                    configuredAddress),
                rawUrl);
        }

        private static RawHttpRequestTarget Parse(string rawUrl)
        {
            RawHttpRequestTarget target;
            Assert.IsTrue(
                RawHttpRequestTargetParser.TryParse(rawUrl, out target));
            Assert.IsNotNull(target);
            return target;
        }

        private sealed class StubRequest : IHttpServerRequest
        {
            private readonly Dictionary<string, string[]> _headers =
                new Dictionary<string, string[]>(
                    StringComparer.OrdinalIgnoreCase);

            public string RawUrl { get; set; }

            public string HttpMethod { get; set; }

            public string ContentType { get; set; }

            public long ContentLength64 { get; set; }

            public Stream InputStream { get; set; }

            public IPEndPoint LocalEndPoint { get; set; }

            public IPEndPoint RemoteEndPoint { get; set; }

            public IReadOnlyList<string> GetHeaderValues(string name)
            {
                string[] values;
                return _headers.TryGetValue(name, out values)
                    ? (IReadOnlyList<string>)values
                    : new string[0];
            }

            public void SetBodyReadTimeout(TimeSpan timeout)
            {
            }

            internal void SetHeaderValues(string name, string[] values)
            {
                _headers[name] = values;
            }
        }

        private sealed class StubContext : IHttpServerContext
        {
            internal StubContext(
                IHttpServerRequest request,
                IPrincipal principal)
            {
                Request = request;
                Principal = principal;
            }

            public IHttpServerRequest Request { get; }

            public IPrincipal Principal { get; }

            public Task WriteResponseAsync(
                HttpTransportResponseData response,
                CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public void Abort()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
