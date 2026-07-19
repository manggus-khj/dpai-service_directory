using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.State;
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
    public sealed class ExternalHttpAdapterTests
    {
        private static readonly DateTimeOffset LocalNow =
            new DateTimeOffset(
                2026,
                7,
                18,
                10,
                20,
                30,
                TimeSpan.FromHours(9));

        private static readonly Guid RequestId =
            new Guid("11111111-1111-1111-1111-111111111111");

        private static readonly Guid PendingId =
            new Guid("55555555-5555-5555-5555-555555555555");

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void MissingLocalEndpointIsBodyless403BeforeRemoteInspection()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            var authenticator = new FakeAuthenticator(true, Code("AB12"));
            ExternalHttpAdapter adapter = CreateAdapter(
                authenticator,
                out store,
                out audit);
            var body = new TrackingStream(RegistrationBody("AB12"));

            ExternalHttpResponseData response = adapter.Process(
                Request(
                    body,
                    omitLocalEndpoint: true,
                    omitRemoteEndpoint: true));

            AssertBodyless(response, 403);
            Assert.AreEqual(0, authenticator.CallCount);
            Assert.AreEqual(0, body.ReadCallCount);
            Assert.AreEqual(
                ExternalNetworkBoundaryFailure.LocalEndpointUnavailable,
                audit.NetworkFailures[0]);
            Assert.AreEqual(0, store.CommitCallCount);
        }

        [TestMethod]
        public void LocalEndpointFailureRunsBeforeRemoteAuthenticationAndBody()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            var authenticator = new FakeAuthenticator(true, Code("AB12"));
            ExternalHttpAdapter adapter = CreateAdapter(
                authenticator,
                out store,
                out audit);
            var body = new TrackingStream(RegistrationBody("AB12"));
            ExternalHttpRequestData request = Request(
                body,
                localEndpoint: new IPEndPoint(
                    IPAddress.Parse("10.20.30.41"),
                    ServiceDirectoryListenerAddress.Port),
                omitRemoteEndpoint: true);

            ExternalHttpResponseData response = adapter.Process(request);

            AssertBodyless(response, 403);
            Assert.AreEqual(0, authenticator.CallCount);
            Assert.AreEqual(0, body.ReadCallCount);
            Assert.AreEqual(1, audit.NetworkFailures.Count);
            Assert.AreEqual(
                ExternalNetworkBoundaryFailure.LocalEndpointMismatch,
                audit.NetworkFailures[0]);
            Assert.AreEqual(0, store.CommitCallCount);
        }

        [TestMethod]
        public void MissingRemoteEndpointIsBodyless403BeforeAuthentication()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            var authenticator = new FakeAuthenticator(true, Code("AB12"));
            ExternalHttpAdapter adapter = CreateAdapter(
                authenticator,
                out store,
                out audit);
            var body = new TrackingStream(RegistrationBody("AB12"));

            ExternalHttpResponseData response = adapter.Process(
                Request(body, omitRemoteEndpoint: true));

            AssertBodyless(response, 403);
            Assert.AreEqual(0, authenticator.CallCount);
            Assert.AreEqual(0, body.ReadCallCount);
            Assert.AreEqual(
                ExternalNetworkBoundaryFailure.RemoteEndpointUnavailable,
                audit.NetworkFailures[0]);
        }

        [TestMethod]
        public void BoundaryAuditUsesEndpointOrExplicitUnknownOperation()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter healthAudit;
            ExternalHttpAdapter healthAdapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out healthAudit);

            AssertBodyless(
                healthAdapter.Process(
                    Request(
                        new TrackingStream(new byte[0]),
                        method: "GET",
                        path: "/api/health",
                        omitLocalEndpoint: true)),
                403);
            Assert.AreEqual(
                SecurityAuditOperation.ExternalHealth,
                healthAudit.NetworkOperations[0]);

            FakeSecurityAuditWriter unknownAudit;
            ExternalHttpAdapter unknownAdapter = CreateAdapter(
                new FakeAuthenticator(false, default(ProductCode)),
                out store,
                out unknownAudit);
            AssertXml(
                unknownAdapter.Process(
                    Request(
                        new TrackingStream(new byte[0]),
                        method: "GET",
                        path: "/undefined")),
                401,
                1003);
            Assert.AreEqual(
                SecurityAuditOperation.ExternalUnknown,
                unknownAudit.ApiKeyOperations[0]);
        }

        [TestMethod]
        public void InvalidOrDuplicateApiKeyReturnsSafe401BeforeBodyRead()
        {
            foreach (IEnumerable<string> headerValues in new[]
            {
                new string[0],
                new[] { "sensitive-rejected-key" },
                new[] { ValidApiKey("AB12"), ValidApiKey("AB12") }
            })
            {
                FakeStateStore store;
                FakeSecurityAuditWriter audit;
                ExternalHttpAdapter adapter = CreateAdapter(
                    new SystemExternalDailyApiKeyAuthenticator(),
                    out store,
                    out audit);
                var body = new TrackingStream(RegistrationBody("AB12"));

                ExternalHttpResponseData response = adapter.Process(
                    Request(body, apiKeyHeaderValues: headerValues));

                AssertXml(response, 401, 1003);
                Assert.AreEqual(0, body.ReadCallCount);
                Assert.AreEqual(1, audit.ApiKeyFailureCount);
                Assert.IsTrue(
                    BodyText(response).IndexOf(
                        "sensitive-rejected-key",
                        StringComparison.Ordinal) < 0);
                Assert.AreEqual(0, store.CommitCallCount);
            }
        }

        [TestMethod]
        public void RegistrationAdmissionRunsBeforeMediaTypeAndBodyWork()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            var authenticator = new FakeAuthenticator(true, Code("AB12"));
            var limiter = new ExternalRequestConcurrencyLimiter();
            var admission = new ExternalRequestAdmissionController(
                limiter,
                () => 0L,
                1L);
            ExternalHttpAdapter adapter = CreateAdapter(
                authenticator,
                admission,
                out store,
                out audit);
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.10");
            for (int index = 0; index < 2; index++)
            {
                ExternalRequestAdmissionResult result =
                    admission.TryAcquire(
                        ExternalHttpEndpoint.Registration,
                        Code("AB12"),
                        remoteAddress);
                Assert.IsTrue(result.IsGranted);
                result.Lease.Dispose();
            }

            var body = new TrackingStream(RegistrationBody("AB12"));
            ExternalHttpResponseData response = adapter.Process(
                Request(
                    body,
                    contentType: "text/plain"));

            AssertXml(response, 429, 1004);
            Assert.AreEqual(20, response.RetryAfterSeconds);
            Assert.AreEqual(0, body.ReadCallCount);
            Assert.AreEqual(0, store.CommitCallCount);
        }

        [TestMethod]
        public void ConcurrencyAdmission429OmitsRetryAfterAndBodyRead()
        {
            var limiter = new ExternalRequestConcurrencyLimiter();
            var heldLeases = new List<IDisposable>();
            try
            {
                for (int index = 0;
                    index < ExternalRequestConcurrencyLimiter
                        .MaximumConcurrentRequests;
                    index++)
                {
                    IDisposable heldLease;
                    Assert.IsTrue(limiter.TryAcquire(out heldLease));
                    heldLeases.Add(heldLease);
                }

                var admission = new ExternalRequestAdmissionController(
                    limiter,
                    () => 0L,
                    1L);
                FakeStateStore store;
                FakeSecurityAuditWriter audit;
                ExternalHttpAdapter adapter = CreateAdapter(
                    new FakeAuthenticator(true, Code("AB12")),
                    admission,
                    out store,
                    out audit);
                var body = new TrackingStream(RegistrationBody("AB12"));

                ExternalHttpResponseData response = adapter.Process(
                    Request(body));

                AssertXml(response, 429, 1004);
                Assert.IsFalse(response.RetryAfterSeconds.HasValue);
                Assert.AreEqual(0, body.ReadCallCount);
                Assert.AreEqual(0, store.CommitCallCount);
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
        public void MissingMethodOrPathIsBodyless404AfterAdmission()
        {
            ExternalHttpRequestData[] requests =
            {
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    method: null),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    path: null)
            };

            foreach (ExternalHttpRequestData request in requests)
            {
                FakeStateStore store;
                FakeSecurityAuditWriter audit;
                var authenticator = new FakeAuthenticator(
                    true,
                    Code("AB12"));
                ExternalHttpAdapter adapter = CreateAdapter(
                    authenticator,
                    out store,
                    out audit);

                AssertBodyless(adapter.Process(request), 404);
                Assert.AreEqual(1, authenticator.CallCount);
                Assert.AreEqual(
                    0,
                    ((TrackingStream)request.BodyStream).ReadCallCount);
                Assert.AreEqual(0, store.CommitCallCount);
            }
        }

        [TestMethod]
        public void EncodedRawPathDoesNotMatchAFixedRoute()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            ExternalHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out audit);
            var body = new TrackingStream(new byte[0]);

            ExternalHttpResponseData response = adapter.Process(
                Request(
                    body,
                    method: "GET",
                    path: "/api%2fhealth",
                    contentType: null));

            AssertBodyless(response, 404);
            Assert.AreEqual(0, body.ReadCallCount);
            Assert.AreEqual(0, store.CommitCallCount);
        }

        [TestMethod]
        public void HealthUsesCapturedTimeAndCombinationRateLimit()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            ExternalHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out audit);

            for (int index = 0; index < 5; index++)
            {
                ExternalHttpResponseData success = adapter.Process(
                    Request(
                        new TrackingStream(new byte[0]),
                        method: "GET",
                        path: "/api/health",
                        contentType: null));
                AssertXml(success, 200, 0);
                StringAssert.Contains(
                    BodyText(success),
                    "<UtcNow>2026-07-18T01:20:30Z</UtcNow>");
            }

            var deniedBody = new TrackingStream(new byte[0]);
            ExternalHttpResponseData denied = adapter.Process(
                Request(
                    deniedBody,
                    method: "GET",
                    path: "/api/health",
                    contentType: null));
            AssertXml(denied, 429, 1004);
            Assert.AreEqual(2, denied.RetryAfterSeconds);
            Assert.AreEqual(0, deniedBody.ReadCallCount);
            Assert.AreEqual(0, store.CommitCallCount);
        }

        [TestMethod]
        public void ServiceLookupUsesStrictQueryAndAuthenticatedProductCode()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            ExternalHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out audit);
            var validBody = new TrackingStream(new byte[0]);

            AssertXml(
                adapter.Process(
                    Request(
                        validBody,
                        method: "GET",
                        path: "/api/services",
                        rawQuery: "?productCode=AB12",
                        contentType: null)),
                404,
                1001);
            Assert.IsTrue(validBody.ReadCallCount > 0);

            adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out audit);
            AssertXml(
                adapter.Process(
                    Request(
                        new TrackingStream(new byte[0]),
                        method: "GET",
                        path: "/api/services",
                        rawQuery:
                            "?productCode=AB12&productCode=AB12",
                        contentType: null)),
                400,
                1000);

            adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out audit);
            AssertXml(
                adapter.Process(
                    Request(
                        new TrackingStream(new byte[0]),
                        method: "GET",
                        path: "/api/services",
                        rawQuery: "?productCode=CD34",
                        contentType: null)),
                401,
                1003);
            Assert.AreEqual(1, audit.ApiKeyFailureCount);
            Assert.AreEqual(
                SecurityAuditOperation.ExternalServiceLookup,
                audit.ApiKeyOperations[0]);
        }

        [TestMethod]
        public void UndefinedRouteAndUnsupportedMediaAreBodylessWithoutReading()
        {
            foreach (ExternalHttpRequestData request in new[]
            {
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    method: "GET",
                    path: "/undefined"),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    contentType: "application/xml"),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    contentType: "application/xml; charset=\"utf-8\""),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    contentType:
                        "application/xml; charset=utf-8; extra=value"),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    contentType: "application/xml;\r\n charset=utf-8"),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    contentEncodingHeaderValue: "gzip")
            })
            {
                FakeStateStore store;
                FakeSecurityAuditWriter audit;
                ExternalHttpAdapter adapter = CreateAdapter(
                    new FakeAuthenticator(true, Code("AB12")),
                    out store,
                    out audit);

                ExternalHttpResponseData response = adapter.Process(request);
                int expectedStatus = StringComparer.Ordinal.Equals(
                        request.AbsolutePath,
                        "/undefined")
                    ? 404
                    : 415;

                AssertBodyless(response, expectedStatus);
                Assert.AreEqual(
                    0,
                    ((TrackingStream)request.BodyStream).ReadCallCount);
            }
        }

        [TestMethod]
        public void ContentEncodingIsBodyless415ForEveryDefinedEndpoint()
        {
            ExternalHttpRequestData[] requests =
            {
                Request(
                    new TrackingStream(new byte[] { 1 }),
                    method: "GET",
                    path: "/api/health",
                    contentEncodingHeaderValue: "gzip"),
                Request(
                    new TrackingStream(new byte[] { 1 }),
                    method: "GET",
                    path: "/api/services",
                    rawQuery: "?productCode=AB12",
                    contentEncodingHeaderValue: "gzip"),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    contentEncodingHeaderValue: "gzip")
            };

            foreach (ExternalHttpRequestData request in requests)
            {
                FakeStateStore store;
                FakeSecurityAuditWriter audit;
                ExternalHttpAdapter adapter = CreateAdapter(
                    new FakeAuthenticator(true, Code("AB12")),
                    out store,
                    out audit);

                AssertBodyless(adapter.Process(request), 415);
                Assert.AreEqual(
                    0,
                    ((TrackingStream)request.BodyStream).ReadCallCount);
                Assert.AreEqual(0, store.CommitCallCount);
            }
        }

        [TestMethod]
        public void QueryAndDeclaredLengthMismatchReturnSafe400()
        {
            ExternalHttpRequestData[] requests =
            {
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    rawQuery: "?unexpected=1"),
                Request(
                    new TrackingStream(RegistrationBody("AB12")),
                    declaredContentLength: 1L)
            };

            foreach (ExternalHttpRequestData request in requests)
            {
                FakeStateStore store;
                FakeSecurityAuditWriter audit;
                ExternalHttpAdapter adapter = CreateAdapter(
                    new FakeAuthenticator(true, Code("AB12")),
                    out store,
                    out audit);

                AssertXml(adapter.Process(request), 400, 1000);
                Assert.AreEqual(0, store.CommitCallCount);
            }
        }

        [TestMethod]
        public void DeclaredAndActualOversizeBodiesReturnBodyless413()
        {
            var declaredBody = new TrackingStream(new byte[0]);
            var actualBody = new TrackingStream(
                new byte[ExternalApiContract.MaximumBodyBytes + 1]);
            ExternalHttpRequestData[] requests =
            {
                Request(
                    declaredBody,
                    declaredContentLength:
                        ExternalApiContract.MaximumBodyBytes + 1L),
                Request(
                    null,
                    declaredContentLength:
                        ExternalApiContract.MaximumBodyBytes + 1L),
                Request(actualBody, declaredContentLength: -1L)
            };

            foreach (ExternalHttpRequestData request in requests)
            {
                FakeStateStore store;
                FakeSecurityAuditWriter audit;
                ExternalHttpAdapter adapter = CreateAdapter(
                    new FakeAuthenticator(true, Code("AB12")),
                    out store,
                    out audit);

                AssertBodyless(adapter.Process(request), 413);
            }

            Assert.AreEqual(0, declaredBody.ReadCallCount);
            Assert.IsTrue(actualBody.ReadCallCount > 0);
        }

        [TestMethod]
        public void ValidRequestCreatesPendingThroughExistingCoreHandler()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            ExternalHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out audit);
            byte[] body = RegistrationBody("AB12");

            ExternalHttpResponseData response = adapter.Process(
                Request(
                    new TrackingStream(body),
                    contentType: " Application/XML ; CHARSET = UTF-8 "));

            AssertXml(response, 200, 0);
            StringAssert.Contains(
                BodyText(response),
                "<Status>PENDING_NEW</Status>");
            StringAssert.Contains(
                BodyText(response),
                "<PendingId>55555555-5555-5555-5555-555555555555</PendingId>");
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreEqual(0, audit.ApiKeyFailureCount);
            Assert.AreEqual(0, audit.NetworkFailures.Count);
        }

        [TestMethod]
        public void BodyProductCodeMismatchReturns401AndWritesFixedAudit()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            ExternalHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(true, Code("AB12")),
                out store,
                out audit);
            byte[] body = RegistrationBody("CD34");

            ExternalHttpResponseData response = adapter.Process(
                Request(new TrackingStream(body)));

            AssertXml(response, 401, 1003);
            Assert.AreEqual(1, audit.ApiKeyFailureCount);
            Assert.AreEqual(0, store.CommitCallCount);
        }

        [TestMethod]
        public void MidnightBoundaryUsesOneCapturedLocalTimeForAuthAndRequestedUtc()
        {
            var capturedLocalNow = new DateTimeOffset(
                2026,
                7,
                18,
                23,
                59,
                59,
                999,
                TimeSpan.FromHours(9));
            DateTimeOffset nextLocalDay = capturedLocalNow.AddMilliseconds(1);
            int adapterNowCallCount = 0;
            int coreNowCallCount = 0;
            Func<DateTimeOffset> adapterNowProvider = () =>
            {
                adapterNowCallCount++;
                return adapterNowCallCount == 1
                    ? capturedLocalNow
                    : nextLocalDay;
            };
            Func<DateTimeOffset> coreNowProvider = () =>
            {
                coreNowCallCount++;
                return nextLocalDay;
            };
            var authenticator = new RecordingAuthenticator();
            var admission = new ExternalRequestAdmissionController(
                new ExternalRequestConcurrencyLimiter(),
                () => 0L,
                1L);
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            ExternalHttpAdapter adapter = CreateAdapter(
                authenticator,
                admission,
                out store,
                out audit,
                adapterNowProvider,
                coreNowProvider);
            var request = Request(
                new TrackingStream(RegistrationBody("AB12")),
                apiKeyHeaderValues: new[]
                {
                    ApiKey("AB12", capturedLocalNow)
                });

            ExternalHttpResponseData response = adapter.Process(request);

            AssertXml(response, 200, 0);
            Assert.AreEqual(1, adapterNowCallCount);
            Assert.AreEqual(0, coreNowCallCount);
            Assert.AreEqual(1, authenticator.CallCount);
            Assert.AreEqual(capturedLocalNow, authenticator.LastLocalNow);
            Assert.IsNotNull(store.LastCommittedSnapshot);
            PendingRegistration pending;
            Assert.IsTrue(
                store.LastCommittedSnapshot.TryGetPending(
                    PendingId,
                    out pending));
            Assert.AreEqual(
                capturedLocalNow.UtcDateTime,
                pending.RequestedUtc);
        }

        [TestMethod]
        public void SecurityAuditWriteFailurePropagatesInsteadOfBecoming500()
        {
            FakeStateStore store;
            FakeSecurityAuditWriter audit;
            ExternalHttpAdapter adapter = CreateAdapter(
                new FakeAuthenticator(false, default(ProductCode)),
                out store,
                out audit);
            audit.ExceptionToThrow = new SecurityAuditWriteException(
                new IOException("Event Log unavailable"));

            Assert.ThrowsExactly<SecurityAuditWriteException>(
                () => adapter.Process(
                    Request(
                        new TrackingStream(RegistrationBody("AB12")))));
        }

        private static ExternalHttpAdapter CreateAdapter(
            IExternalDailyApiKeyAuthenticator authenticator,
            out FakeStateStore store,
            out FakeSecurityAuditWriter audit)
        {
            var admission = new ExternalRequestAdmissionController(
                new ExternalRequestConcurrencyLimiter(),
                () => 0L,
                1L);
            return CreateAdapter(
                authenticator,
                admission,
                out store,
                out audit);
        }

        private static ExternalHttpAdapter CreateAdapter(
            IExternalDailyApiKeyAuthenticator authenticator,
            ExternalRequestAdmissionController admission,
            out FakeStateStore store,
            out FakeSecurityAuditWriter audit,
            Func<DateTimeOffset> adapterLocalNowProvider = null,
            Func<DateTimeOffset> coreLocalNowProvider = null)
        {
            store = new FakeStateStore(
                StateLoadResult.Success(DirectorySnapshot.Empty()));
            StateCoordinatorOpenResult openResult =
                StateMutationCoordinator.Open(store);
            Assert.IsTrue(openResult.IsSuccess);
            var coreHandler = new ExternalApiHandler(
                openResult.Coordinator,
                coreLocalNowProvider ?? (() => LocalNow),
                () => PendingId);
            ServiceDirectoryListenerAddress configuredAddress;
            Assert.IsTrue(
                ServiceDirectoryListenerAddress.TryCreate(
                    "10.20.30.40",
                    out configuredAddress));
            audit = new FakeSecurityAuditWriter();
            return new ExternalHttpAdapter(
                coreHandler,
                configuredAddress,
                admission,
                audit,
                new BoundedRequestBodyReader(),
                authenticator,
                adapterLocalNowProvider ?? (() => LocalNow),
                () => RequestId);
        }

        private static ExternalHttpRequestData Request(
            TrackingStream body,
            string method = "POST",
            string path = "/api/registration",
            string rawQuery = null,
            IEnumerable<string> apiKeyHeaderValues = null,
            string contentType = ExternalApiContract.XmlContentType,
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
                apiKeyHeaderValues ?? new[] { ValidApiKey("AB12") },
                contentType,
                contentEncodingHeaderValue,
                declaredContentLength ?? body.Length,
                body,
                omitLocalEndpoint
                    ? null
                    : localEndpoint ?? new IPEndPoint(
                        IPAddress.Parse("10.20.30.40"),
                        ServiceDirectoryListenerAddress.Port),
                omitRemoteEndpoint
                    ? null
                    : remoteEndpoint ?? new IPEndPoint(
                        IPAddress.Parse("192.0.2.10"),
                        50000));
        }

        private static byte[] RegistrationBody(string productCode)
        {
            string xml =
                "<RegistrationRequest xmlns=\""
                + ExternalApiContract.XmlNamespace
                + "\"><Name>Directory</Name><ProductCode>"
                + productCode
                + "</ProductCode><ServerAddress>service.internal"
                + "</ServerAddress><Port>21000</Port>"
                + "</RegistrationRequest>";
            return StrictUtf8.GetBytes(xml);
        }

        private static string ValidApiKey(string rawProductCode)
        {
            return ApiKey(rawProductCode, LocalNow);
        }

        private static string ApiKey(
            string rawProductCode,
            DateTimeOffset localNow)
        {
            ProductCode productCode = Code(rawProductCode);
            var initializationVector = new byte[16];
            for (int index = 0; index < initializationVector.Length; index++)
            {
                initializationVector[index] = (byte)index;
            }

            return DailyApiKeyCodec.Create(
                productCode,
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

        private sealed class FakeSecurityAuditWriter
            : IExternalSecurityAuditWriter
        {
            internal FakeSecurityAuditWriter()
            {
                NetworkFailures =
                    new List<ExternalNetworkBoundaryFailure>();
                ApiKeyOperations =
                    new List<SecurityAuditOperation>();
                NetworkOperations =
                    new List<SecurityAuditOperation>();
            }

            internal int ApiKeyFailureCount { get; private set; }

            internal List<ExternalNetworkBoundaryFailure>
                NetworkFailures { get; }

            internal List<SecurityAuditOperation> ApiKeyOperations { get; }

            internal List<SecurityAuditOperation> NetworkOperations { get; }

            internal Exception ExceptionToThrow { get; set; }

            public void WriteApiKeyRejected(
                Guid requestId,
                SecurityAuditOperation operation,
                IPAddress remoteAddress)
            {
                ThrowIfConfigured();
                ApiKeyFailureCount++;
                ApiKeyOperations.Add(operation);
            }

            public void WriteNetworkBoundaryRejected(
                Guid requestId,
                SecurityAuditOperation operation,
                ExternalNetworkBoundaryFailure failure,
                IPAddress remoteAddress)
            {
                ThrowIfConfigured();
                NetworkFailures.Add(failure);
                NetworkOperations.Add(operation);
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

            public override int Read(byte[] buffer, int offset, int count)
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

        private sealed class FakeStateStore : IServiceDirectoryStateStore
        {
            internal FakeStateStore(StateLoadResult loadResult)
            {
                LoadResult = loadResult;
            }

            internal StateLoadResult LoadResult { get; }

            internal int CommitCallCount { get; private set; }

            internal DirectorySnapshot LastCommittedSnapshot
            {
                get;
                private set;
            }

            public StateLoadResult Load()
            {
                return LoadResult;
            }

            public StateCommitResult Commit(
                DirectorySnapshot expectedSnapshot,
                DirectorySnapshot nextSnapshot)
            {
                CommitCallCount++;
                LastCommittedSnapshot = nextSnapshot;
                return StateCommitResult.Success();
            }
        }
    }
}
