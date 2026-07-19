using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.Authentication;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class WatchdogHealthHttpAdapterTests
    {
        private static readonly DateTimeOffset LocalNow =
            new DateTimeOffset(
                2026,
                7,
                18,
                23,
                59,
                59,
                999,
                TimeSpan.FromHours(9));

        private static readonly Guid RequestId =
            new Guid("73737373-7373-7373-7373-737373737373");

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void LocalBoundaryFailuresPrecedeRemoteAuthenticationAndBody()
        {
            ExternalHttpRequestData[] requests =
            {
                Request(
                    new TrackingStream(new byte[] { 1 }),
                    omitLocalEndpoint: true,
                    omitRemoteEndpoint: true),
                Request(
                    new TrackingStream(new byte[] { 1 }),
                    localEndpoint: new IPEndPoint(
                        IPAddress.Parse("127.0.0.2"),
                        ServiceDirectoryListenerAddress.Port),
                    omitRemoteEndpoint: true)
            };
            WatchdogHealthNetworkBoundaryFailure[] expectedFailures =
            {
                WatchdogHealthNetworkBoundaryFailure
                    .LocalEndpointUnavailable,
                WatchdogHealthNetworkBoundaryFailure
                    .LocalEndpointMismatch
            };

            for (int index = 0; index < requests.Length; index++)
            {
                var authenticator = new FakeAuthenticator(
                    true,
                    Code("WDOG"));
                FakeWatchdogHealthSecurityAuditWriter audit;
                WatchdogHealthHttpAdapter adapter = CreateAdapter(
                    authenticator,
                    out audit);

                AssertBodyless(adapter.Process(requests[index]), 403);
                Assert.AreEqual(0, authenticator.CallCount);
                Assert.AreEqual(
                    expectedFailures[index],
                    audit.NetworkFailures[0]);
                Assert.AreEqual(
                    0,
                    ((TrackingStream)requests[index].BodyStream)
                        .ReadCallCount);
            }
        }

        [TestMethod]
        public void RemoteEndpointMustBeOsLoopbackBeforeAuthentication()
        {
            ExternalHttpRequestData[] requests =
            {
                Request(
                    new TrackingStream(new byte[] { 1 }),
                    omitRemoteEndpoint: true),
                Request(
                    new TrackingStream(new byte[] { 1 }),
                    remoteEndpoint: new IPEndPoint(
                        IPAddress.Parse("192.0.2.10"),
                        50000))
            };
            WatchdogHealthNetworkBoundaryFailure[] expectedFailures =
            {
                WatchdogHealthNetworkBoundaryFailure
                    .RemoteEndpointUnavailable,
                WatchdogHealthNetworkBoundaryFailure
                    .RemoteEndpointNotLoopback
            };

            for (int index = 0; index < requests.Length; index++)
            {
                var authenticator = new FakeAuthenticator(
                    true,
                    Code("WDOG"));
                FakeWatchdogHealthSecurityAuditWriter audit;
                WatchdogHealthHttpAdapter adapter = CreateAdapter(
                    authenticator,
                    out audit);

                AssertBodyless(adapter.Process(requests[index]), 403);
                Assert.AreEqual(0, authenticator.CallCount);
                Assert.AreEqual(
                    expectedFailures[index],
                    audit.NetworkFailures[0]);
                Assert.AreEqual(
                    0,
                    ((TrackingStream)requests[index].BodyStream)
                        .ReadCallCount);
            }
        }

        [TestMethod]
        public void ExactlyOneValidDailyKeyIsRequiredBeforeBodyWork()
        {
            string validKey = ApiKey("WDOG", LocalNow);
            IEnumerable<string>[] headerCases =
            {
                new string[0],
                new[] { "sensitive-invalid-watchdog-key" },
                new[] { validKey, validKey }
            };

            foreach (IEnumerable<string> headers in headerCases)
            {
                FakeWatchdogHealthSecurityAuditWriter audit;
                WatchdogHealthHttpAdapter adapter = CreateAdapter(
                    new SystemExternalDailyApiKeyAuthenticator(),
                    out audit);
                var body = new TrackingStream(new byte[] { 1 });

                ExternalHttpResponseData response = adapter.Process(
                    Request(body, apiKeyHeaderValues: headers));

                AssertXml(response, 401, 1003);
                Assert.AreEqual(1, audit.ApiKeyFailureCount);
                Assert.AreEqual(0, body.ReadCallCount);
                Assert.IsTrue(
                    BodyText(response).IndexOf(
                        "sensitive-invalid-watchdog-key",
                        StringComparison.Ordinal) < 0);
            }
        }

        [TestMethod]
        public void ValidWdogProductCodeUsesOneCapturedTimeAndSucceeds()
        {
            DateTimeOffset nextLocalDay = LocalNow.AddMilliseconds(1);
            int localNowCallCount = 0;
            Func<DateTimeOffset> localNowProvider = () =>
            {
                localNowCallCount++;
                return localNowCallCount == 1 ? LocalNow : nextLocalDay;
            };
            var authenticator = new RecordingAuthenticator();
            FakeWatchdogHealthSecurityAuditWriter audit;
            WatchdogHealthHttpAdapter adapter = CreateAdapter(
                authenticator,
                out audit,
                localNowProvider: localNowProvider);
            var body = new TrackingStream(new byte[0]);

            ExternalHttpResponseData response = adapter.Process(
                Request(
                    body,
                    apiKeyHeaderValues: new[]
                    {
                        ApiKey("WDOG", LocalNow)
                    }));

            AssertXml(response, 200, 0);
            StringAssert.Contains(
                BodyText(response),
                "<UtcNow>2026-07-18T14:59:59.999Z</UtcNow>");
            Assert.AreEqual(1, localNowCallCount);
            Assert.AreEqual(1, authenticator.CallCount);
            Assert.AreEqual(LocalNow, authenticator.LastLocalNow);
            Assert.AreEqual(1, body.ReadCallCount);
            Assert.AreEqual(0, audit.ApiKeyFailureCount);
        }

        [TestMethod]
        public void ValidNonWdogProductCodeIsAcceptedWithoutValueComparison()
        {
            var authenticator = new RecordingAuthenticator();
            FakeWatchdogHealthSecurityAuditWriter audit;
            WatchdogHealthHttpAdapter adapter = CreateAdapter(
                authenticator,
                out audit);
            var body = new TrackingStream(new byte[0]);

            ExternalHttpResponseData response = adapter.Process(
                Request(
                    body,
                    apiKeyHeaderValues: new[]
                    {
                        ApiKey("AB12", LocalNow)
                    }));

            AssertXml(response, 200, 0);
            Assert.AreEqual(1, authenticator.CallCount);
            Assert.AreEqual(0, audit.ApiKeyFailureCount);
            Assert.AreEqual(1, body.ReadCallCount);
        }

        [TestMethod]
        public void OnlyExactRawGetHealthRouteIsExposedOnLoopback()
        {
            string[] methods = { "GET", "POST", "GET", "POST" };
            string[] paths =
            {
                "/api%2fhealth",
                "/api/health",
                "/api/services",
                "/api/registration"
            };

            for (int index = 0; index < paths.Length; index++)
            {
                var authenticator = new FakeAuthenticator(
                    true,
                    Code("WDOG"));
                FakeWatchdogHealthSecurityAuditWriter audit;
                WatchdogHealthHttpAdapter adapter = CreateAdapter(
                    authenticator,
                    out audit);
                var body = new TrackingStream(new byte[] { 1 });

                ExternalHttpResponseData response = adapter.Process(
                    Request(
                        body,
                        method: methods[index],
                        path: paths[index]));

                AssertBodyless(response, 404);
                Assert.AreEqual(1, authenticator.CallCount);
                Assert.AreEqual(0, body.ReadCallCount);
            }
        }

        [TestMethod]
        public void QueryBodyEncodingAndRawSizeContractsAreEnforced()
        {
            FakeWatchdogHealthSecurityAuditWriter audit;
            WatchdogHealthHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("WDOG")),
                out audit);
            AssertXml(
                adapter.Process(
                    Request(
                        new TrackingStream(new byte[0]),
                        rawQuery: "?unexpected=1")),
                400,
                1000);

            adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("WDOG")),
                out audit);
            AssertXml(
                adapter.Process(
                    Request(new TrackingStream(new byte[] { 1 }))),
                400,
                1000);

            adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("WDOG")),
                out audit);
            var encodedBody = new TrackingStream(new byte[] { 1 });
            AssertBodyless(
                adapter.Process(
                    Request(
                        encodedBody,
                        contentEncodingHeaderValue: "gzip")),
                415);
            Assert.AreEqual(0, encodedBody.ReadCallCount);

            adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("WDOG")),
                out audit);
            var declaredOversize = new TrackingStream(new byte[0]);
            AssertBodyless(
                adapter.Process(
                    Request(
                        declaredOversize,
                        declaredContentLength:
                            ExternalApiContract.MaximumBodyBytes + 1L)),
                413);
            Assert.AreEqual(0, declaredOversize.ReadCallCount);

            adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("WDOG")),
                out audit);
            AssertBodyless(
                adapter.Process(
                    Request(
                        null,
                        declaredContentLength:
                            ExternalApiContract.MaximumBodyBytes + 1L)),
                413);

            adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("WDOG")),
                out audit);
            var actualOversize = new TrackingStream(
                new byte[ExternalApiContract.MaximumBodyBytes + 1]);
            AssertBodyless(
                adapter.Process(
                    Request(
                        actualOversize,
                        declaredContentLength: -1L)),
                413);
            Assert.IsTrue(actualOversize.ReadCallCount > 0);
        }

        [TestMethod]
        public void HealthRateAndSharedConcurrencyHaveDistinctRetryRules()
        {
            long timestamp = 0;
            var limiter = new ExternalRequestConcurrencyLimiter();
            FakeWatchdogHealthSecurityAuditWriter audit;
            WatchdogHealthHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("WDOG")),
                out audit,
                limiter,
                () => timestamp);

            for (int index = 0; index < 5; index++)
            {
                AssertXml(
                    adapter.Process(
                        Request(new TrackingStream(new byte[0]))),
                    200,
                    0);
            }

            var rateDeniedBody = new TrackingStream(new byte[0]);
            ExternalHttpResponseData rateDenied = adapter.Process(
                Request(rateDeniedBody));
            AssertXml(rateDenied, 429, 1004);
            Assert.AreEqual(2, rateDenied.RetryAfterSeconds);
            Assert.AreEqual(0, rateDeniedBody.ReadCallCount);

            timestamp = 2;
            AssertXml(
                adapter.Process(Request(new TrackingStream(new byte[0]))),
                200,
                0);

            limiter = new ExternalRequestConcurrencyLimiter();
            var heldLeases = new List<IDisposable>();
            try
            {
                for (int index = 0;
                    index < ExternalRequestConcurrencyLimiter
                        .MaximumConcurrentRequests;
                    index++)
                {
                    IDisposable lease;
                    Assert.IsTrue(limiter.TryAcquire(out lease));
                    heldLeases.Add(lease);
                }

                adapter = CreateAdapter(
                    new FakeAuthenticator(true, Code("WDOG")),
                    out audit,
                    limiter,
                    () => 0L);
                var concurrencyDeniedBody =
                    new TrackingStream(new byte[0]);
                ExternalHttpResponseData concurrencyDenied =
                    adapter.Process(Request(concurrencyDeniedBody));
                AssertXml(concurrencyDenied, 429, 1004);
                Assert.IsFalse(
                    concurrencyDenied.RetryAfterSeconds.HasValue);
                Assert.AreEqual(
                    0,
                    concurrencyDeniedBody.ReadCallCount);
            }
            finally
            {
                foreach (IDisposable lease in heldLeases)
                {
                    lease.Dispose();
                }
            }
        }

        [TestMethod]
        public void SecurityAuditWriteFailurePropagates()
        {
            FakeWatchdogHealthSecurityAuditWriter audit;
            WatchdogHealthHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(false, default(ProductCode)),
                out audit);
            audit.ExceptionToThrow = new SecurityAuditWriteException(
                new IOException("Event Log unavailable"));

            Assert.ThrowsExactly<SecurityAuditWriteException>(
                () => adapter.Process(
                    Request(new TrackingStream(new byte[0]))));
        }

        private static WatchdogHealthHttpAdapter CreateAdapter(
            IExternalDailyApiKeyAuthenticator authenticator,
            out FakeWatchdogHealthSecurityAuditWriter audit,
            ExternalRequestConcurrencyLimiter concurrencyLimiter = null,
            Func<long> timestampProvider = null,
            Func<DateTimeOffset> localNowProvider = null)
        {
            audit = new FakeWatchdogHealthSecurityAuditWriter();
            var admission = new ExternalRequestAdmissionController(
                concurrencyLimiter ??
                    new ExternalRequestConcurrencyLimiter(),
                timestampProvider ?? (() => 0L),
                1L);
            return new WatchdogHealthHttpAdapter(
                admission,
                audit,
                new BoundedRequestBodyReader(),
                authenticator,
                localNowProvider ?? (() => LocalNow),
                () => RequestId);
        }

        private static ExternalHttpRequestData Request(
            TrackingStream body,
            string method = "GET",
            string path = "/api/health",
            string rawQuery = null,
            IEnumerable<string> apiKeyHeaderValues = null,
            string contentEncodingHeaderValue = null,
            long? declaredContentLength = null,
            IPEndPoint localEndpoint = null,
            IPEndPoint remoteEndpoint = null,
            bool omitLocalEndpoint = false,
            bool omitRemoteEndpoint = false)
        {
            return new ExternalHttpRequestData(
                method,
                path,
                rawQuery,
                apiKeyHeaderValues ?? new[] { ApiKey("WDOG", LocalNow) },
                null,
                contentEncodingHeaderValue,
                declaredContentLength ?? body.Length,
                body,
                omitLocalEndpoint
                    ? null
                    : localEndpoint ?? new IPEndPoint(
                        IPAddress.Loopback,
                        ServiceDirectoryListenerAddress.Port),
                omitRemoteEndpoint
                    ? null
                    : remoteEndpoint ?? new IPEndPoint(
                        IPAddress.Parse("127.0.0.2"),
                        50000));
        }

        private static string ApiKey(
            string rawProductCode,
            DateTimeOffset localNow)
        {
            var initializationVector = new byte[16];
            for (int index = 0; index < initializationVector.Length; index++)
            {
                initializationVector[index] = (byte)(15 - index);
            }

            return DailyApiKeyCodec.Create(
                Code(rawProductCode),
                localNow,
                initializationVector);
        }

        private static ProductCode Code(string rawValue)
        {
            ProductCode productCode;
            Assert.IsTrue(ProductCode.TryCreate(rawValue, out productCode));
            return productCode;
        }

        private static void AssertXml(
            ExternalHttpResponseData response,
            int expectedStatus,
            int expectedCode)
        {
            Assert.AreEqual(expectedStatus, response.StatusCode);
            Assert.IsTrue(response.HasBody);
            Assert.AreEqual(
                ExternalApiContract.XmlContentType,
                response.ContentType);
            StringAssert.Contains(
                BodyText(response),
                "<Code>" + expectedCode + "</Code>");
        }

        private static void AssertBodyless(
            ExternalHttpResponseData response,
            int expectedStatus)
        {
            Assert.AreEqual(expectedStatus, response.StatusCode);
            Assert.IsFalse(response.HasBody);
            Assert.AreEqual(0, response.ContentLength);
            Assert.IsNull(response.ContentType);
            Assert.IsFalse(response.RetryAfterSeconds.HasValue);
        }

        private static string BodyText(ExternalHttpResponseData response)
        {
            return StrictUtf8.GetString(response.GetBody());
        }

        private sealed class FakeAuthenticator
            : IExternalDailyApiKeyAuthenticator
        {
            private readonly bool _result;
            private readonly ProductCode _productCode;

            internal FakeAuthenticator(
                bool result,
                ProductCode productCode)
            {
                _result = result;
                _productCode = productCode;
            }

            internal int CallCount { get; private set; }

            public bool TryAuthenticate(
                IEnumerable<string> headerValues,
                DateTimeOffset localNow,
                out ProductCode authenticatedProductCode)
            {
                CallCount++;
                authenticatedProductCode = _result
                    ? _productCode
                    : default(ProductCode);
                return _result;
            }
        }

        private sealed class RecordingAuthenticator
            : IExternalDailyApiKeyAuthenticator
        {
            internal int CallCount { get; private set; }

            internal DateTimeOffset LastLocalNow { get; private set; }

            public bool TryAuthenticate(
                IEnumerable<string> headerValues,
                DateTimeOffset localNow,
                out ProductCode authenticatedProductCode)
            {
                CallCount++;
                LastLocalNow = localNow;
                return DailyApiKeyAuthenticator.TryAuthenticate(
                    headerValues,
                    localNow,
                    out authenticatedProductCode);
            }
        }

        private sealed class FakeWatchdogHealthSecurityAuditWriter
            : IWatchdogHealthSecurityAuditWriter
        {
            internal FakeWatchdogHealthSecurityAuditWriter()
            {
                NetworkFailures =
                    new List<WatchdogHealthNetworkBoundaryFailure>();
            }

            internal int ApiKeyFailureCount { get; private set; }

            internal List<WatchdogHealthNetworkBoundaryFailure>
                NetworkFailures { get; }

            internal Exception ExceptionToThrow { get; set; }

            public void WriteApiKeyRejected(
                Guid requestId,
                IPAddress remoteAddress)
            {
                ThrowIfConfigured();
                ApiKeyFailureCount++;
            }

            public void WriteNetworkBoundaryRejected(
                Guid requestId,
                WatchdogHealthNetworkBoundaryFailure failure,
                IPAddress remoteAddress)
            {
                ThrowIfConfigured();
                NetworkFailures.Add(failure);
            }

            private void ThrowIfConfigured()
            {
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }
            }
        }

        private sealed class TrackingStream : Stream
        {
            private readonly MemoryStream _inner;

            internal TrackingStream(byte[] contents)
            {
                _inner = new MemoryStream(contents, false);
            }

            internal int ReadCallCount { get; private set; }

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush()
            {
            }

            public override int Read(
                byte[] buffer,
                int offset,
                int count)
            {
                ReadCallCount++;
                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return _inner.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(
                byte[] buffer,
                int offset,
                int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
