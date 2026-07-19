using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.Authentication;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.ExternalProtocol
{
    [TestClass]
    public sealed class ExternalApiHandlerTests
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

        private static readonly Guid PendingId =
            new Guid("55555555-5555-5555-5555-555555555555");

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void RequestModelEnforcesHostRawBodyBoundaryAndCopiesMutableValues()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ExternalApiHandlerRequest(
                    "POST",
                    "/api/registration",
                    null,
                    new[] { "not-used" },
                    new byte[ExternalApiContract.MaximumBodyBytes + 1],
                    IPAddress.Parse("192.0.2.10")));

            byte[] body = { 1, 2, 3 };
            IPAddress address = IPAddress.Parse("2001:db8::10");
            var request = new ExternalApiHandlerRequest(
                "POST",
                "/api/registration",
                null,
                new[] { "not-used" },
                body,
                address);

            body[0] = 99;
            address.ScopeId = 7;

            CollectionAssert.AreEqual(
                new byte[] { 1, 2, 3 },
                request.CopyBody());
            Assert.AreEqual("2001:db8::10", request.RemoteAddress.ToString());
        }

        [TestMethod]
        public void AuthenticationRunsBeforeRoutingAndUsesOneGenericFailure()
        {
            ExternalApiHandler handler = CreateHandler(DirectorySnapshot.Empty());
            string validApiKey = CreateApiKey("AB12");
            ExternalApiHandlerRequest[] invalidRequests =
            {
                Request(
                    "GET",
                    "/undefined",
                    "AB12",
                    apiKeyHeaderValues: new string[0]),
                Request(
                    "GET",
                    "/undefined",
                    "AB12",
                    apiKeyHeaderValues: new[] { validApiKey, validApiKey }),
                Request(
                    "GET",
                    "/undefined",
                    "AB12",
                    apiKeyHeaderValues: new[] { "rejected-sensitive-value" })
            };

            foreach (ExternalApiHandlerRequest request in invalidRequests)
            {
                ExternalApiHandlerResponse response = handler.Handle(request);

                AssertXmlResponse(response, 401, 1003);
                Assert.IsTrue(response.RequiresInvalidApiKeyAudit);
                Assert.IsTrue(
                    BodyText(response).IndexOf(
                        "rejected-sensitive-value",
                        StringComparison.Ordinal) < 0);
            }
        }

        [TestMethod]
        public void UndefinedRouteAndWrongMethodAreBodyless404OnlyAfterAuthentication()
        {
            ExternalApiHandler handler = CreateHandler(DirectorySnapshot.Empty());

            ExternalApiHandlerResponse undefined = handler.Handle(
                Request("GET", "/undefined", "AB12"));
            ExternalApiHandlerResponse wrongMethod = handler.Handle(
                Request("POST", "/api/health", "AB12"));

            AssertBodyless404(undefined);
            AssertBodyless404(wrongMethod);
        }

        [TestMethod]
        public void HealthReturnsUtcWithoutProductOrVersionAndRejectsQueryOrBody()
        {
            ExternalApiHandler handler = CreateHandler(DirectorySnapshot.Empty());

            ExternalApiHandlerResponse success = handler.Handle(
                Request("GET", "/api/health", "WDOG"));

            AssertXmlResponse(success, 200, 0);
            string body = BodyText(success);
            StringAssert.Contains(body, "<UtcNow>2026-07-18T01:20:30Z</UtcNow>");
            Assert.IsTrue(body.IndexOf("ProductCode", StringComparison.Ordinal) < 0);
            Assert.IsTrue(body.IndexOf("Version", StringComparison.Ordinal) < 0);
            Assert.IsTrue(body.IndexOf("Build", StringComparison.Ordinal) < 0);

            AssertXmlResponse(
                handler.Handle(
                    Request(
                        "GET",
                        "/api/health",
                        "AB12",
                        queryParameters: new[]
                        {
                            new ExternalApiQueryParameter("extra", "1")
                        })),
                400,
                1000);
            AssertXmlResponse(
                handler.Handle(
                    Request(
                        "GET",
                        "/api/health",
                        "AB12",
                        body: new byte[] { 1 })),
                400,
                1000);
        }

        [TestMethod]
        public void ServiceQueryMustContainOnlyOneValidProductCode()
        {
            ExternalApiHandler handler = CreateHandler(DirectorySnapshot.Empty());
            ExternalApiQueryParameter[][] invalidQueries =
            {
                new ExternalApiQueryParameter[0],
                new[]
                {
                    new ExternalApiQueryParameter("productCode", "AB12"),
                    new ExternalApiQueryParameter("productCode", "AB12")
                },
                new[]
                {
                    new ExternalApiQueryParameter("ProductCode", "AB12")
                },
                new[]
                {
                    new ExternalApiQueryParameter("extra", "AB12")
                },
                new[]
                {
                    new ExternalApiQueryParameter("productCode", "ABC")
                }
            };

            foreach (ExternalApiQueryParameter[] query in invalidQueries)
            {
                AssertXmlResponse(
                    handler.Handle(
                        Request(
                            "GET",
                            "/api/services",
                            "AB12",
                            queryParameters: query)),
                    400,
                    1000);
            }

            ExternalApiHandlerResponse bodyRejected = handler.Handle(
                Request(
                    "GET",
                    "/api/services",
                    "AB12",
                    body: new byte[] { 1 }));
            AssertXmlResponse(bodyRejected, 400, 1000);
        }

        [TestMethod]
        public void ServiceProductCodeNormalizesBeforeGenericMismatch401()
        {
            ExternalApiHandler handler = CreateHandler(DirectorySnapshot.Empty());

            ExternalApiHandlerResponse normalized = handler.Handle(
                Request(
                    "GET",
                    "/api/services",
                    "AB12",
                    queryParameters: ProductCodeQuery(" ab12 ")));
            ExternalApiHandlerResponse response = handler.Handle(
                Request(
                    "GET",
                    "/api/services",
                    "AB12",
                    queryParameters: ProductCodeQuery("CD34")));

            AssertXmlResponse(normalized, 404, 1001);
            AssertXmlResponse(response, 401, 1003);
            Assert.IsTrue(response.RequiresInvalidApiKeyAudit);
        }

        [TestMethod]
        public void ServiceLookupReturnsApprovedValueDuringPendingModifyAndHidesInternalFields()
        {
            ServiceDefinition approved = TestData.Definition(
                name: "Approved service",
                productCode: "AB12",
                serverAddress: "10.20.30.40",
                port: 21000);
            ServiceRecord record = TestData.ActiveRecord(
                approved,
                7UL,
                TestData.OriginA);
            ServiceDefinition requested = TestData.Definition(
                name: "Pending replacement",
                productCode: "AB12",
                serverAddress: "10.20.30.41",
                port: 22000);
            var pending = new PendingRegistration(
                PendingId,
                PendingRequestType.Modify,
                TestData.Utc(1),
                "192.0.2.10",
                requested,
                DirectoryBaseRevision.Capture(record));
            var snapshot = new DirectorySnapshot(
                new[] { record },
                new[] { pending },
                7UL);
            ExternalApiHandler handler = CreateHandler(snapshot);

            ExternalApiHandlerResponse response = handler.Handle(
                Request("GET", "/api/services", "AB12"));

            AssertXmlResponse(response, 200, 0);
            string body = BodyText(response);
            StringAssert.Contains(body, "<Name>Approved service</Name>");
            StringAssert.Contains(body, "<ProductCode>AB12</ProductCode>");
            StringAssert.Contains(body, "<ServerAddress>10.20.30.40</ServerAddress>");
            StringAssert.Contains(body, "<Port>21000</Port>");
            Assert.IsTrue(body.IndexOf("Pending replacement", StringComparison.Ordinal) < 0);
            Assert.IsTrue(body.IndexOf("PendingId", StringComparison.Ordinal) < 0);
            Assert.IsTrue(body.IndexOf("Deleted", StringComparison.Ordinal) < 0);
            Assert.IsTrue(body.IndexOf("LogicalVersion", StringComparison.Ordinal) < 0);
            Assert.IsTrue(body.IndexOf("OriginInstanceId", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void TombstoneNewPendingAndMissingServiceAreAllSame404Envelope()
        {
            ServiceDefinition deletedDefinition = TestData.Definition(
                productCode: "AB12");
            ServiceRecord tombstone = TestData.ActiveRecord(
                    deletedDefinition,
                    1UL,
                    TestData.OriginA)
                .MarkDeleted(TestData.Utc(2), 2UL, TestData.OriginA);
            ServiceDefinition pendingDefinition = TestData.Definition(
                productCode: "CD34");
            var pending = new PendingRegistration(
                PendingId,
                PendingRequestType.New,
                TestData.Utc(3),
                "192.0.2.10",
                pendingDefinition,
                DirectoryBaseRevision.Capture(null));
            var snapshot = new DirectorySnapshot(
                new[] { tombstone },
                new[] { pending },
                2UL);
            ExternalApiHandler handler = CreateHandler(snapshot);

            foreach (string productCode in new[] { "AB12", "CD34", "EF56" })
            {
                AssertXmlResponse(
                    handler.Handle(
                        Request("GET", "/api/services", productCode)),
                    404,
                    1001);
            }
        }

        [TestMethod]
        public void RegistrationCreatesPendingAndIdenticalRetryReusesPendingId()
        {
            var store = new FakeStateStore(
                StateLoadResult.Success(DirectorySnapshot.Empty()));
            StateMutationCoordinator coordinator = Open(store);
            ExternalApiHandler handler = CreateHandler(coordinator);
            ExternalApiHandlerRequest request = RegistrationRequest(
                "AB12",
                "Directory",
                "service.internal",
                21000,
                IPAddress.Parse("192.0.2.55"));

            ExternalApiHandlerResponse first = handler.Handle(request);
            ExternalApiHandlerResponse repeated = handler.Handle(request);

            AssertXmlResponse(first, 200, 0);
            AssertXmlResponse(repeated, 200, 0);
            StringAssert.Contains(BodyText(first), "<Status>PENDING_NEW</Status>");
            StringAssert.Contains(
                BodyText(first),
                "<PendingId>55555555-5555-5555-5555-555555555555</PendingId>");
            StringAssert.Contains(
                BodyText(repeated),
                "<Status>PENDING_EXISTS</Status>");
            StringAssert.Contains(
                BodyText(repeated),
                "<PendingId>55555555-5555-5555-5555-555555555555</PendingId>");
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreEqual(1, coordinator.CurrentSnapshot.PendingCount);
            PendingRegistration pending;
            Assert.IsTrue(
                coordinator.CurrentSnapshot.TryGetPending(PendingId, out pending));
            Assert.AreEqual("192.0.2.55", pending.SourceIp);
        }

        [TestMethod]
        public void RegistrationMapsConflictAlreadyRegisteredAndPendingModify()
        {
            ServiceDefinition active = TestData.Definition(
                name: "Current",
                productCode: "AB12",
                serverAddress: "service.internal",
                port: 21000);
            ServiceRecord record = TestData.ActiveRecord(
                active,
                1UL,
                TestData.OriginA);
            var store = new FakeStateStore(
                StateLoadResult.Success(
                    new DirectorySnapshot(
                        new[] { record },
                        new PendingRegistration[0],
                        1UL)));
            StateMutationCoordinator coordinator = Open(store);
            ExternalApiHandler handler = CreateHandler(coordinator);

            ExternalApiHandlerResponse alreadyRegistered = handler.Handle(
                RegistrationRequest(
                    "AB12",
                    "Current",
                    "service.internal",
                    21000));
            ExternalApiHandlerResponse pendingModify = handler.Handle(
                RegistrationRequest(
                    "AB12",
                    "Changed",
                    "service.internal",
                    21000));
            ExternalApiHandlerResponse conflict = handler.Handle(
                RegistrationRequest(
                    "AB12",
                    "Different again",
                    "service.internal",
                    21000));

            AssertXmlResponse(alreadyRegistered, 200, 0);
            StringAssert.Contains(
                BodyText(alreadyRegistered),
                "<Status>ALREADY_REGISTERED</Status>");
            Assert.IsTrue(
                BodyText(alreadyRegistered).IndexOf(
                    "PendingId",
                    StringComparison.Ordinal) < 0);
            AssertXmlResponse(pendingModify, 200, 0);
            StringAssert.Contains(
                BodyText(pendingModify),
                "<Status>PENDING_MODIFY</Status>");
            AssertXmlResponse(conflict, 409, 1002);
            Assert.AreEqual(1, store.CommitCallCount);
        }

        [TestMethod]
        public void RegistrationRejectsInvalidXmlAndProductCodeMismatch()
        {
            ExternalApiHandler handler = CreateHandler(DirectorySnapshot.Empty());

            ExternalApiHandlerResponse invalidXml = handler.Handle(
                Request(
                    "POST",
                    "/api/registration",
                    "AB12",
                    body: StrictUtf8.GetBytes("<invalid />")));
            ExternalApiHandlerResponse mismatch = handler.Handle(
                RegistrationRequest(
                    "CD34",
                    "Directory",
                    "service.internal",
                    21000,
                    apiKeyProductCode: "AB12"));

            AssertXmlResponse(invalidXml, 400, 1000);
            AssertXmlResponse(mismatch, 401, 1003);
            Assert.IsTrue(mismatch.RequiresInvalidApiKeyAudit);
        }

        [TestMethod]
        public void PendingCapacityReturns429WithoutRetryAfter()
        {
            var pending = new List<PendingRegistration>(
                DirectorySnapshot.PendingRegistrationLimit);
            for (int index = 0;
                index < DirectorySnapshot.PendingRegistrationLimit;
                index++)
            {
                ServiceDefinition definition = TestData.Definition(
                    name: "Pending " + index,
                    productCode: ProductCodeFor(index));
                pending.Add(
                    new PendingRegistration(
                        GuidFor(index + 1),
                        PendingRequestType.New,
                        TestData.Utc(index % 60),
                        "192.0.2.10",
                        definition,
                        DirectoryBaseRevision.Capture(null)));
            }

            ExternalApiHandler handler = CreateHandler(
                new DirectorySnapshot(
                    new ServiceRecord[0],
                    pending,
                    0UL));

            ExternalApiHandlerResponse response = handler.Handle(
                RegistrationRequest(
                    "ZZZZ",
                    "Over capacity",
                    "service.internal",
                    21000));
            ExternalApiHandlerResponse existing = handler.Handle(
                RegistrationRequest(
                    "0000",
                    "Pending 0",
                    "10.20.30.40",
                    21000));
            ExternalApiHandlerResponse conflict = handler.Handle(
                RegistrationRequest(
                    "0000",
                    "Different pending request",
                    "10.20.30.40",
                    21000));

            AssertXmlResponse(response, 429, 1004);
            Assert.IsFalse(response.RetryAfterSeconds.HasValue);
            AssertXmlResponse(existing, 200, 0);
            StringAssert.Contains(
                BodyText(existing),
                "<Status>PENDING_EXISTS</Status>");
            AssertXmlResponse(conflict, 409, 1002);
        }

        [TestMethod]
        public void PersistenceAndRecoveryFailuresReturnOnlySafe500Envelope()
        {
            var store = new FakeStateStore(
                StateLoadResult.Success(DirectorySnapshot.Empty()))
            {
                CommitResult = StateCommitResult.Failure(
                    StateCommitFailureCode.IoFailure)
            };
            StateMutationCoordinator coordinator = Open(store);
            ExternalApiHandler handler = CreateHandler(coordinator);

            ExternalApiHandlerResponse persistenceFailure = handler.Handle(
                RegistrationRequest(
                    "AB12",
                    "Directory",
                    "service.internal",
                    21000));

            AssertXmlResponse(persistenceFailure, 500, 3000);
            AssertSafeInternalBody(persistenceFailure);

            store.CommitResult = StateCommitResult.Failure(
                StateCommitFailureCode.RecoveryRequired);
            ExternalApiHandlerResponse recoveryFailure = handler.Handle(
                RegistrationRequest(
                    "CD34",
                    "Directory 2",
                    "service2.internal",
                    21001));
            ExternalApiHandlerResponse unavailableLookup = handler.Handle(
                Request("GET", "/api/services", "CD34"));

            AssertXmlResponse(recoveryFailure, 500, 3000);
            AssertXmlResponse(unavailableLookup, 500, 3000);
            AssertSafeInternalBody(recoveryFailure);
            AssertSafeInternalBody(unavailableLookup);
        }

        [TestMethod]
        public void UnexpectedInternalExceptionIsNotExposed()
        {
            StateMutationCoordinator coordinator = Open(
                new FakeStateStore(
                    StateLoadResult.Success(DirectorySnapshot.Empty())));
            var handler = new ExternalApiHandler(
                coordinator,
                () => throw new InvalidOperationException(
                    "C:\\sensitive\\state.xml key=do-not-expose"),
                () => PendingId);

            ExternalApiHandlerResponse response = handler.Handle(
                Request("GET", "/api/health", "AB12"));

            AssertXmlResponse(response, 500, 3000);
            AssertSafeInternalBody(response);
        }

        private static ExternalApiHandler CreateHandler(
            DirectorySnapshot snapshot)
        {
            return CreateHandler(
                Open(
                    new FakeStateStore(
                        StateLoadResult.Success(snapshot))));
        }

        private static ExternalApiHandler CreateHandler(
            StateMutationCoordinator coordinator)
        {
            return new ExternalApiHandler(
                coordinator,
                () => LocalNow,
                () => PendingId);
        }

        private static StateMutationCoordinator Open(FakeStateStore store)
        {
            StateCoordinatorOpenResult result =
                StateMutationCoordinator.Open(store);
            Assert.IsTrue(result.IsSuccess);
            Assert.IsNotNull(result.Coordinator);
            return result.Coordinator;
        }

        private static ExternalApiHandlerRequest Request(
            string method,
            string path,
            string apiKeyProductCode,
            IEnumerable<ExternalApiQueryParameter> queryParameters = null,
            IEnumerable<string> apiKeyHeaderValues = null,
            byte[] body = null,
            IPAddress remoteAddress = null)
        {
            IEnumerable<ExternalApiQueryParameter> effectiveQuery =
                queryParameters;
            if (effectiveQuery == null
                && StringComparer.Ordinal.Equals(path, "/api/services"))
            {
                effectiveQuery = ProductCodeQuery(apiKeyProductCode);
            }

            return new ExternalApiHandlerRequest(
                method,
                path,
                effectiveQuery,
                apiKeyHeaderValues ?? new[]
                {
                    CreateApiKey(apiKeyProductCode)
                },
                body ?? new byte[0],
                remoteAddress ?? IPAddress.Parse("192.0.2.10"));
        }

        private static ExternalApiHandlerRequest RegistrationRequest(
            string productCode,
            string name,
            string serverAddress,
            int port,
            IPAddress remoteAddress = null,
            string apiKeyProductCode = null)
        {
            string xml =
                "<RegistrationRequest xmlns=\""
                + ExternalApiContract.XmlNamespace
                + "\"><Name>"
                + name
                + "</Name><ProductCode>"
                + productCode
                + "</ProductCode><ServerAddress>"
                + serverAddress
                + "</ServerAddress><Port>"
                + port
                + "</Port></RegistrationRequest>";
            return Request(
                "POST",
                "/api/registration",
                apiKeyProductCode ?? productCode,
                body: StrictUtf8.GetBytes(xml),
                remoteAddress: remoteAddress);
        }

        private static ExternalApiQueryParameter[] ProductCodeQuery(
            string productCode)
        {
            return new[]
            {
                new ExternalApiQueryParameter(
                    "productCode",
                    productCode)
            };
        }

        private static string CreateApiKey(string rawProductCode)
        {
            ProductCode productCode;
            Assert.IsTrue(
                ProductCode.TryCreate(rawProductCode, out productCode));
            var initializationVector = new byte[16];
            for (int index = 0; index < initializationVector.Length; index++)
            {
                initializationVector[index] = (byte)index;
            }

            return DailyApiKeyCodec.Create(
                productCode,
                LocalNow,
                initializationVector);
        }

        private static void AssertXmlResponse(
            ExternalApiHandlerResponse response,
            int expectedHttpStatus,
            int expectedCode)
        {
            Assert.IsNotNull(response);
            Assert.AreEqual(expectedHttpStatus, response.StatusCode);
            Assert.IsTrue(response.HasBody);
            Assert.AreEqual(
                ExternalApiContract.XmlContentType,
                response.ContentType);
            StringAssert.Contains(
                BodyText(response),
                "<Code>" + expectedCode + "</Code>");
            if (expectedHttpStatus != 429)
            {
                Assert.IsFalse(response.RetryAfterSeconds.HasValue);
            }

            if (expectedHttpStatus != 401)
            {
                Assert.IsFalse(response.RequiresInvalidApiKeyAudit);
            }
        }

        private static void AssertBodyless404(
            ExternalApiHandlerResponse response)
        {
            Assert.IsNotNull(response);
            Assert.AreEqual(404, response.StatusCode);
            Assert.IsFalse(response.HasBody);
            Assert.IsNull(response.ContentType);
            Assert.AreEqual(0, response.GetBody().Length);
            Assert.IsFalse(response.RetryAfterSeconds.HasValue);
            Assert.IsFalse(response.RequiresInvalidApiKeyAudit);
        }

        private static void AssertSafeInternalBody(
            ExternalApiHandlerResponse response)
        {
            string body = BodyText(response);
            StringAssert.Contains(
                body,
                "The service directory could not process the request.");
            Assert.IsTrue(body.IndexOf("sensitive", StringComparison.OrdinalIgnoreCase) < 0);
            Assert.IsTrue(body.IndexOf("state.xml", StringComparison.OrdinalIgnoreCase) < 0);
            Assert.IsTrue(body.IndexOf("key=", StringComparison.OrdinalIgnoreCase) < 0);
            Assert.IsTrue(body.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) < 0);
            Assert.IsTrue(body.IndexOf("Stack", StringComparison.OrdinalIgnoreCase) < 0);
        }

        private static string BodyText(ExternalApiHandlerResponse response)
        {
            return StrictUtf8.GetString(response.GetBody());
        }

        private static string ProductCodeFor(int value)
        {
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var characters = new char[4];
            for (int index = characters.Length - 1; index >= 0; index--)
            {
                characters[index] = alphabet[value % alphabet.Length];
                value /= alphabet.Length;
            }

            return new string(characters);
        }

        private static Guid GuidFor(int value)
        {
            byte[] bytes = new byte[16];
            byte[] source = BitConverter.GetBytes(value);
            Buffer.BlockCopy(source, 0, bytes, 0, source.Length);
            return new Guid(bytes);
        }

        private sealed class FakeStateStore : IServiceDirectoryStateStore
        {
            internal FakeStateStore(StateLoadResult loadResult)
            {
                LoadResult = loadResult;
                CommitResult = StateCommitResult.Success();
            }

            internal StateLoadResult LoadResult { get; set; }

            internal StateCommitResult CommitResult { get; set; }

            internal int CommitCallCount { get; private set; }

            public StateLoadResult Load()
            {
                return LoadResult;
            }

            public StateCommitResult Commit(
                DirectorySnapshot expectedSnapshot,
                DirectorySnapshot nextSnapshot)
            {
                CommitCallCount++;
                return CommitResult;
            }
        }
    }
}
