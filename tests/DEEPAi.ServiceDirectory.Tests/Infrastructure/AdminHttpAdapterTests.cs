using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Protocol;
using DEEPAi.ServiceDirectory.Infrastructure.Security;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class AdminHttpAdapterTests
    {
        private static readonly SecurityIdentifier ActorSid =
            new SecurityIdentifier("S-1-5-32-544");

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void MissingLocalEndpointRejectsBeforeAuthorizationOrBody()
        {
            var handler = new FakeHandler();
            var authorization = new FakeAuthorizationEvaluator(
                AdminAuthorizationEvaluation.Authorized(ActorSid));
            var audit = new FakeAuditWriter();
            AdminHttpAdapter adapter = CreateAdapter(
                handler,
                authorization,
                audit);
            var body = new TrackingStream(new byte[] { 1 });

            AdminHttpResponseData response = adapter.Process(
                Request(
                    "GET",
                    "/admin/services",
                    body: body,
                    omitLocalEndpoint: true));

            AssertBodyless(response, 403);
            Assert.AreEqual(0, authorization.CallCount);
            Assert.AreEqual(0, body.ReadCallCount);
            Assert.AreEqual(
                AdminNetworkBoundaryFailure.LocalEndpointUnavailable,
                audit.BoundaryFailure.Value);
            Assert.AreEqual(0, handler.TotalCalls);
        }

        [TestMethod]
        public void NonExactLocalOrNonLoopbackRemoteIsAuditedAndRejected()
        {
            var wrongLocalAuthorization = new FakeAuthorizationEvaluator(
                AdminAuthorizationEvaluation.Authorized(ActorSid));
            var wrongLocalAudit = new FakeAuditWriter();
            AdminHttpResponseData wrongLocal = CreateAdapter(
                new FakeHandler(),
                wrongLocalAuthorization,
                wrongLocalAudit).Process(
                    Request(
                        "GET",
                        "/admin/services",
                        localEndpoint: new IPEndPoint(
                            IPAddress.IPv6Loopback,
                            ServiceDirectoryListenerAddress.Port)));
            AssertBodyless(wrongLocal, 403);
            Assert.AreEqual(0, wrongLocalAuthorization.CallCount);
            Assert.AreEqual(
                AdminNetworkBoundaryFailure.LocalEndpointMismatch,
                wrongLocalAudit.BoundaryFailure.Value);

            var handler = new FakeHandler();
            var authorization = new FakeAuthorizationEvaluator(
                AdminAuthorizationEvaluation.Authorized(ActorSid));
            var audit = new FakeAuditWriter();
            AdminHttpAdapter adapter = CreateAdapter(
                handler,
                authorization,
                audit);

            AdminHttpResponseData response = adapter.Process(
                Request(
                    "GET",
                    "/admin/services",
                    remoteEndpoint: new IPEndPoint(
                        IPAddress.Parse("192.0.2.10"),
                        50000)));

            AssertBodyless(response, 403);
            Assert.AreEqual(0, authorization.CallCount);
            Assert.AreEqual(
                AdminNetworkBoundaryFailure.RemoteEndpointNotLoopback,
                audit.BoundaryFailure.Value);
        }

        [TestMethod]
        public void AuthenticationAndAuthorizationFailuresAreBodyless()
        {
            var unauthenticatedHandler = new FakeHandler();
            var unauthenticatedBody = new TrackingStream(
                new byte[] { 1, 2, 3 });
            var unauthenticatedAudit = new FakeAuditWriter();
            AdminHttpResponseData unauthenticated = CreateAdapter(
                unauthenticatedHandler,
                new FakeAuthorizationEvaluator(
                    AdminAuthorizationEvaluation.Unauthenticated(
                        SecurityAuditReason.InvalidWindowsIdentity)),
                unauthenticatedAudit).Process(
                    Request(
                        "GET",
                        "/admin/%73ervices",
                        body: unauthenticatedBody));

            AssertBodyless(unauthenticated, 401);
            Assert.AreEqual(0, unauthenticatedBody.ReadCallCount);
            Assert.AreEqual(0, unauthenticatedHandler.TotalCalls);
            Assert.AreEqual(
                SecurityAuditReason.InvalidWindowsIdentity,
                unauthenticatedAudit.AuthenticationReason.Value);

            var forbiddenAudit = new FakeAuditWriter();
            AdminHttpResponseData forbidden = CreateAdapter(
                new FakeHandler(),
                new FakeAuthorizationEvaluator(
                    AdminAuthorizationEvaluation.Forbidden(ActorSid)),
                forbiddenAudit).Process(
                    Request("GET", "/admin/services"));

            AssertBodyless(forbidden, 403);
            Assert.AreEqual(
                SecurityAuditReason.NotInOperatorsGroup,
                forbiddenAudit.AuthorizationReason.Value);
            Assert.AreEqual(ActorSid, forbiddenAudit.ActorSid);
        }

        [TestMethod]
        public void SystemEvaluatorRejectsAuthenticatedNonWindowsIdentity()
        {
            var evaluator = new SystemAdminAuthorizationEvaluator(
                new AdminRequestAuthorizer());
            var principal = new GenericPrincipal(
                new GenericIdentity("tester", "Negotiate"),
                new string[0]);

            AdminAuthorizationEvaluation result = evaluator.Evaluate(
                principal);

            Assert.AreEqual(
                AdminAuthorizationStatus.Unauthenticated,
                result.Status);
            Assert.AreEqual(
                SecurityAuditReason.InvalidWindowsIdentity,
                result.FailureReason.Value);
        }

        [TestMethod]
        public void UndefinedRouteReturnsBodyless404WithoutReadingBody()
        {
            var body = new TrackingStream(new byte[] { 1, 2, 3 });
            var handler = new FakeHandler();
            AdminHttpAdapter adapter = AuthorizedAdapter(handler);

            AdminHttpResponseData response = adapter.Process(
                Request("POST", "/admin/undefined", body: body));

            AssertBodyless(response, 404);
            Assert.AreEqual(0, body.ReadCallCount);
            Assert.AreEqual(0, handler.TotalCalls);
            for (int index = 0; index < 15; index++)
            {
                Assert.AreEqual(
                    200,
                    adapter.Process(
                        Request("GET", "/admin/services")).StatusCode);
            }
        }

        [TestMethod]
        public void ServicesQueryIsStrictAndUsesDocumentedDefaults()
        {
            var defaultsHandler = new FakeHandler();
            AdminHttpResponseData defaults = AuthorizedAdapter(
                defaultsHandler).Process(
                    Request("GET", "/admin/services"));
            Assert.AreEqual(200, defaults.StatusCode);
            Assert.IsFalse(defaultsHandler.LastServicesQuery.IncludeDeleted);
            Assert.AreEqual(100, defaultsHandler.LastServicesQuery.PageSize);
            Assert.IsNull(defaultsHandler.LastServicesQuery.Cursor);

            var handler = new FakeHandler();
            AdminHttpAdapter adapter = AuthorizedAdapter(handler);

            AdminHttpResponseData success = adapter.Process(
                Request(
                    "GET",
                    "/admin/services",
                    "?includeDeleted=true&pageSize=250&cursor=abc%2Fdef"));

            Assert.AreEqual(200, success.StatusCode);
            Assert.IsTrue(success.HasBody);
            Assert.AreEqual(1, handler.ServicesCalls);
            Assert.IsTrue(handler.LastServicesQuery.IncludeDeleted);
            Assert.AreEqual(250, handler.LastServicesQuery.PageSize);
            Assert.AreEqual("abc/def", handler.LastServicesQuery.Cursor);

            AdminHttpResponseData duplicate = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request(
                        "GET",
                        "/admin/services",
                        "?pageSize=100&pageSize=100"));

            AssertError(duplicate, 400, 1000);
        }

        [TestMethod]
        public void RawPathAndQueryAreNeverFormDecodedOrNormalized()
        {
            AdminHttpResponseData encodedPath = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request("GET", "/admin/%73ervices"));
            AssertBodyless(encodedPath, 404);

            AdminHttpResponseData wrongMethod = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request("POST", "/admin/services"));
            AssertBodyless(wrongMethod, 404);

            var cursorHandler = new FakeHandler();
            AdminHttpResponseData literalPlus = AuthorizedAdapter(
                cursorHandler).Process(
                    Request(
                        "GET",
                        "/admin/services",
                        "?cursor=a+b"));
            Assert.AreEqual(200, literalPlus.StatusCode);
            Assert.AreEqual("a+b", cursorHandler.LastServicesQuery.Cursor);

            AdminHttpResponseData encodedDuplicate = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request(
                        "GET",
                        "/admin/services",
                        "?pageSize=100&page%53ize=100"));
            AssertError(encodedDuplicate, 400, 1000);
        }

        [TestMethod]
        public void BodylessEndpointRejectsEntityBody()
        {
            var handler = new FakeHandler();
            AdminHttpAdapter adapter = AuthorizedAdapter(handler);
            var body = new TrackingStream(StrictUtf8.GetBytes("unexpected"));

            AdminHttpResponseData response = adapter.Process(
                Request("GET", "/admin/settings/logging", body: body));

            AssertError(response, 400, 1000);
            Assert.IsTrue(body.ReadCallCount > 0);
            Assert.AreEqual(0, handler.TotalCalls);
        }

        [TestMethod]
        public void RegistrationModeRoutesRejectBodyAndQuery()
        {
            var bodyHandler = new FakeHandler();
            AdminHttpResponseData bodyResponse = AuthorizedAdapter(
                bodyHandler).Process(
                    Request(
                        "POST",
                        AdminApiContract.OpenRegistrationModePath,
                        body: new TrackingStream(new byte[] { 1 })));
            AssertError(bodyResponse, 400, 1000);
            Assert.AreEqual(0, bodyHandler.TotalCalls);

            var queryHandler = new FakeHandler();
            AdminHttpResponseData queryResponse = AuthorizedAdapter(
                queryHandler).Process(
                    Request(
                        "GET",
                        AdminApiContract.RegistrationModePath,
                        "?productCode=AB12"));
            AssertError(queryResponse, 400, 1000);
            Assert.AreEqual(0, queryHandler.TotalCalls);
        }

        [TestMethod]
        public void LoggingPutValidatesMediaTypeXmlAndTypedRequest()
        {
            var handler = new FakeHandler();
            AdminHttpAdapter adapter = AuthorizedAdapter(handler);
            byte[] body = StrictUtf8.GetBytes(
                "<LoggingSettings xmlns=\"urn:deepai:service-directory:admin\">"
                + "<LogRetentionDays>30</LogRetentionDays>"
                + "</LoggingSettings>");

            AdminHttpResponseData response = adapter.Process(
                Request(
                    "PUT",
                    "/admin/settings/logging",
                    body: new TrackingStream(body),
                    contentType: "application/xml; charset=utf-8"));

            Assert.AreEqual(200, response.StatusCode);
            AdminResponse<AdminLoggingSettings> parsed =
                AdminXmlCodec.ParseLoggingResponse(response.GetBody());
            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(30, parsed.Payload.LogRetentionDays);
            Assert.AreEqual(30, handler.LastLoggingRequest.LogRetentionDays);

            var unsupportedBody = new TrackingStream(body);
            AdminHttpResponseData unsupported = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request(
                        "PUT",
                        "/admin/settings/logging",
                        body: unsupportedBody,
                        contentType: "text/xml; charset=utf-8"));
            AssertBodyless(unsupported, 415);
            Assert.AreEqual(0, unsupportedBody.ReadCallCount);

            byte[] malformed = StrictUtf8.GetBytes("<LoggingSettings>");
            AdminHttpResponseData badXml = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request(
                        "PUT",
                        "/admin/settings/logging",
                        body: new TrackingStream(malformed),
                        contentType: "application/xml; charset=utf-8"));
            AssertError(badXml, 400, 1000);
        }

        [TestMethod]
        public void RemovedPendingRoutesAreUndefinedAndDynamicValuesAreCanonical()
        {
            Guid id = new Guid("abcdefab-cdef-4abc-8def-abcdefabcdef");
            var pendingHandler = new FakeHandler();
            AdminHttpResponseData pendingList = AuthorizedAdapter(
                pendingHandler).Process(
                    Request("GET", "/admin/pending"));
            AssertBodyless(pendingList, 404);

            AdminHttpResponseData pendingAction = AuthorizedAdapter(
                pendingHandler).Process(
                    Request(
                        "POST",
                        "/admin/pending/" + id.ToString("D") + "/approve"));
            AssertBodyless(pendingAction, 404);
            Assert.AreEqual(0, pendingHandler.TotalCalls);

            AdminHttpResponseData nonCanonicalCode = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request("DELETE", "/admin/services/ab12"));
            AssertError(nonCanonicalCode, 400, 1000);
        }

        [TestMethod]
        public void RemainingAdminRoutesDispatchToTypedHandlerMethods()
        {
            Guid id = new Guid("abcdefab-cdef-4abc-8def-abcdefabcdef");
            AssertRoute(
                "GET",
                AdminApiContract.RegistrationModePath,
                null,
                "GetRegistrationMode");
            AssertRoute(
                "POST",
                AdminApiContract.OpenRegistrationModePath,
                null,
                "OpenRegistrationMode");
            AssertRoute(
                "POST",
                AdminApiContract.CloseRegistrationModePath,
                null,
                "CloseRegistrationMode");
            AssertRoute(
                "DELETE",
                "/admin/services/AB12",
                null,
                "DeleteService",
                handler => Assert.AreEqual(
                    "AB12",
                    handler.LastDeletedProductCode));
            AssertRoute("GET", "/admin/sync", null, "GetSyncStatus");
            AssertRoute(
                "POST",
                "/admin/sync/enable",
                AdminXmlCodec.SerializeEnableSync(
                    "https://10.0.0.2:21000",
                    false),
                "EnableSync",
                handler =>
                {
                    Assert.AreEqual(
                        "https://10.0.0.2:21000",
                        handler.LastEnableRequest.PeerEndpoint);
                    Assert.IsFalse(handler.LastEnableRequest.RePair);
                });
            AssertRoute(
                "POST",
                "/admin/sync/pairing/confirm",
                AdminXmlCodec.SerializePairingConfirmation(id),
                "ConfirmPairing",
                handler =>
                {
                    Assert.AreEqual(
                        id,
                        handler.LastPairingConfirmationRequest.PairingId);
                    Assert.IsTrue(
                        handler.LastPairingConfirmationRequest.Confirmed);
                });
            AssertRoute(
                "POST",
                "/admin/sync/pairing/cancel",
                AdminXmlCodec.SerializePairingCancellation(id),
                "CancelPairing",
                handler => Assert.AreEqual(
                    id,
                    handler.LastPairingCancellationRequest.PairingId));
            AssertRoute(
                "POST",
                "/admin/sync/disable",
                AdminXmlCodec.SerializeDisableSync(true),
                "DisableSync",
                handler => Assert.IsTrue(
                    handler.LastDisableRequest.ForgetPeer));
            AssertRoute(
                "POST",
                "/admin/sync/now",
                null,
                "SynchronizeNow");
            AssertRoute(
                "GET",
                "/admin/settings/logging",
                null,
                "GetLoggingSettings");
        }

        [TestMethod]
        public void DeclaredOversizeIsBodyless413()
        {
            AdminHttpResponseData response = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request(
                        "GET",
                        "/admin/services",
                        body: new TrackingStream(new byte[0]),
                        declaredLength:
                            AdminApiContract.MaximumBodyBytes + 1L));

            AssertBodyless(response, 413);
        }

        [TestMethod]
        public void ActualBodyLimitAndIdentityRateLimitMapSafely()
        {
            AdminHttpResponseData exactLimit = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request(
                        "GET",
                        "/admin/services",
                        body: new TrackingStream(
                            new byte[AdminApiContract.MaximumBodyBytes]),
                        declaredLength: -1));
            AssertError(exactLimit, 400, 1000);

            AdminHttpResponseData overLimit = AuthorizedAdapter(
                new FakeHandler()).Process(
                    Request(
                        "GET",
                        "/admin/services",
                        body: new TrackingStream(
                            new byte[
                                AdminApiContract.MaximumBodyBytes + 1]),
                        declaredLength: -1));
            AssertBodyless(overLimit, 413);

            AdminHttpAdapter adapter = AuthorizedAdapter(new FakeHandler());
            for (int index = 0; index < 15; index++)
            {
                Assert.AreEqual(
                    200,
                    adapter.Process(
                        Request("GET", "/admin/services")).StatusCode);
            }

            var unreadBody = new TrackingStream(new byte[] { 1 });
            AdminHttpResponseData limited = adapter.Process(
                Request(
                    "GET",
                    "/admin/services",
                    body: unreadBody));
            AssertError(limited, 429, 1004);
            Assert.AreEqual(1, limited.RetryAfterSeconds.Value);
            Assert.AreEqual(0, unreadBody.ReadCallCount);
        }

        [TestMethod]
        public void HandlerErrorsUseSafeAdminHttpMapping()
        {
            AssertHandlerError(
                AdminServerErrorCode.NotFound,
                404,
                1001);
            AssertHandlerError(
                AdminServerErrorCode.Conflict,
                409,
                1002);
            AssertHandlerError(
                AdminServerErrorCode.LimitExceeded,
                429,
                1004);
            AssertHandlerError(
                AdminServerErrorCode.NotPeer,
                500,
                3000);
        }

        [TestMethod]
        public void UnexpectedFailuresDoNotLeakButAuditFailureRemainsFatal()
        {
            var handler = new FakeHandler
            {
                LoggingException = new ApplicationException(
                    "sensitive implementation detail")
            };
            AdminHttpResponseData safeError = AuthorizedAdapter(
                handler).Process(
                    Request("GET", "/admin/settings/logging"));
            AssertError(safeError, 500, 3000);
            Assert.IsFalse(
                StrictUtf8.GetString(safeError.GetBody()).Contains(
                    "sensitive implementation detail"));

            var audit = new FakeAuditWriter
            {
                ExceptionToThrow = new SecurityAuditWriteException(
                    new InvalidOperationException("write failed"))
            };
            Assert.ThrowsExactly<SecurityAuditWriteException>(
                () => CreateAdapter(
                    new FakeHandler(),
                    new FakeAuthorizationEvaluator(
                        AdminAuthorizationEvaluation.Authorized(ActorSid)),
                    audit).Process(
                        Request(
                            "GET",
                            "/admin/services",
                            omitLocalEndpoint: true)));
        }

        private static void AssertHandlerError(
            AdminServerErrorCode code,
            int expectedStatus,
            int expectedCode)
        {
            var handler = new FakeHandler
            {
                LoggingError = code
            };
            AdminHttpResponseData response = AuthorizedAdapter(
                handler).Process(
                    Request("GET", "/admin/settings/logging"));

            AssertError(response, expectedStatus, expectedCode);
            if (code == AdminServerErrorCode.LimitExceeded)
            {
                Assert.IsFalse(response.RetryAfterSeconds.HasValue);
            }
        }

        private static void AssertRoute(
            string method,
            string path,
            byte[] body,
            string expectedOperation,
            Action<FakeHandler> assertHandler = null)
        {
            var handler = new FakeHandler();
            AdminHttpResponseData response = AuthorizedAdapter(
                handler).Process(
                    Request(
                        method,
                        path,
                        body: body == null
                            ? null
                            : new TrackingStream(body),
                        contentType: body == null
                            ? null
                            : "application/xml; charset=utf-8"));

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual(expectedOperation, handler.LastOperation);
            assertHandler?.Invoke(handler);
        }

        private static AdminHttpAdapter AuthorizedAdapter(
            FakeHandler handler)
        {
            return CreateAdapter(
                handler,
                new FakeAuthorizationEvaluator(
                    AdminAuthorizationEvaluation.Authorized(ActorSid)),
                new FakeAuditWriter());
        }

        private static AdminHttpAdapter CreateAdapter(
            FakeHandler handler,
            IAdminAuthorizationEvaluator evaluator,
            IAdminSecurityAuditWriter auditWriter)
        {
            return new AdminHttpAdapter(
                handler,
                evaluator,
                auditWriter,
                new AdminRequestAdmissionController(() => 0L, 1L),
                new BoundedRequestBodyReader());
        }

        private static AdminHttpRequestData Request(
            string method,
            string path,
            string query = null,
            TrackingStream body = null,
            string contentType = null,
            long? declaredLength = null,
            IPEndPoint localEndpoint = null,
            IPEndPoint remoteEndpoint = null,
            bool omitLocalEndpoint = false,
            bool omitRemoteEndpoint = false)
        {
            return new AdminHttpRequestData(
                method,
                path,
                query,
                contentType,
                null,
                declaredLength ?? (body == null ? 0L : body.Length),
                body,
                omitLocalEndpoint
                    ? null
                    : localEndpoint ?? new IPEndPoint(
                        IPAddress.Loopback,
                        ServiceDirectoryListenerAddress.Port),
                omitRemoteEndpoint
                    ? null
                    : remoteEndpoint ?? new IPEndPoint(
                        IPAddress.IPv6Loopback,
                        50000),
                new GenericPrincipal(
                    new GenericIdentity("tester", "Negotiate"),
                    new string[0]));
        }

        private static void AssertBodyless(
            AdminHttpResponseData response,
            int statusCode)
        {
            Assert.AreEqual(statusCode, response.StatusCode);
            Assert.IsFalse(response.HasBody);
            Assert.AreEqual(0, response.ContentLength);
            Assert.IsNull(response.ContentType);
        }

        private static void AssertError(
            AdminHttpResponseData response,
            int statusCode,
            int code)
        {
            Assert.AreEqual(statusCode, response.StatusCode);
            AdminResponse<AdminUnit> parsed = AdminXmlCodec.ParseUnitResponse(
                response.GetBody());
            Assert.IsFalse(parsed.IsSuccess);
            Assert.AreEqual(code, parsed.Code);
        }

        private sealed class FakeAuthorizationEvaluator
            : IAdminAuthorizationEvaluator
        {
            private readonly AdminAuthorizationEvaluation _result;

            public FakeAuthorizationEvaluator(
                AdminAuthorizationEvaluation result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public AdminAuthorizationEvaluation Evaluate(
                IPrincipal principal)
            {
                CallCount++;
                return _result;
            }
        }

        private sealed class FakeAuditWriter : IAdminSecurityAuditWriter
        {
            public Exception ExceptionToThrow { get; set; }

            public AdminNetworkBoundaryFailure? BoundaryFailure
            {
                get;
                private set;
            }

            public SecurityAuditReason? AuthenticationReason
            {
                get;
                private set;
            }

            public SecurityAuditReason? AuthorizationReason
            {
                get;
                private set;
            }

            public SecurityIdentifier ActorSid { get; private set; }

            public void WriteNetworkBoundaryRejected(
                Guid requestId,
                AdminNetworkBoundaryFailure failure,
                IPAddress remoteAddress)
            {
                ThrowIfConfigured();
                BoundaryFailure = failure;
            }

            public void WriteAuthenticationRejected(
                Guid requestId,
                SecurityAuditReason reason,
                IPAddress remoteAddress)
            {
                ThrowIfConfigured();
                AuthenticationReason = reason;
            }

            public void WriteAuthorizationRejected(
                Guid requestId,
                SecurityAuditReason reason,
                SecurityIdentifier actorSid,
                IPAddress remoteAddress)
            {
                ThrowIfConfigured();
                AuthorizationReason = reason;
                ActorSid = actorSid;
            }

            private void ThrowIfConfigured()
            {
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }
            }
        }

        private sealed class FakeHandler : IAdminHttpRequestHandler
        {
            private static readonly AdminServerUnitResponse Unit =
                AdminServerUnitResponse.Value;

            public int TotalCalls { get; private set; }

            public int ServicesCalls { get; private set; }

            public AdminServicesQuery LastServicesQuery { get; private set; }

            public string LastDeletedProductCode { get; private set; }

            public AdminEnableSyncRequest LastEnableRequest { get; private set; }

            public AdminPairingConfirmationRequest
                LastPairingConfirmationRequest { get; private set; }

            public AdminPairingCancellationRequest
                LastPairingCancellationRequest { get; private set; }

            public AdminDisableSyncRequest LastDisableRequest { get; private set; }

            public AdminLoggingSettingsRequest LastLoggingRequest
            {
                get;
                private set;
            }

            public AdminServerErrorCode? LoggingError { get; set; }

            public Exception LoggingException { get; set; }

            public string LastOperation { get; private set; }

            public AdminHandlerResult<AdminServerServicesResponse> GetServices(
                AdminServicesQuery query)
            {
                TotalCalls++;
                ServicesCalls++;
                LastOperation = "GetServices";
                LastServicesQuery = query;
                return AdminHandlerResult<AdminServerServicesResponse>.Success(
                    new AdminServerServicesResponse(
                        new List<AdminServerServiceItem>().AsReadOnly(),
                        0,
                        null));
            }

            public AdminHandlerResult<AdminServerRegistrationModeResponse>
                GetRegistrationMode()
            {
                TotalCalls++;
                LastOperation = "GetRegistrationMode";
                return RegistrationModeSuccess();
            }

            public AdminHandlerResult<AdminServerRegistrationModeResponse>
                OpenRegistrationMode()
            {
                TotalCalls++;
                LastOperation = "OpenRegistrationMode";
                return RegistrationModeSuccess();
            }

            public AdminHandlerResult<AdminServerRegistrationModeResponse>
                CloseRegistrationMode()
            {
                TotalCalls++;
                LastOperation = "CloseRegistrationMode";
                return RegistrationModeSuccess();
            }

            public AdminHandlerResult<AdminServerUnitResponse> DeleteService(
                string productCode)
            {
                TotalCalls++;
                LastDeletedProductCode = productCode;
                LastOperation = "DeleteService";
                return UnitSuccess();
            }

            public AdminHandlerResult<AdminServerSyncStatusResponse>
                GetSyncStatus()
            {
                TotalCalls++;
                LastOperation = "GetSyncStatus";
                return AdminHandlerResult<AdminServerSyncStatusResponse>
                    .Success(
                        new AdminServerSyncStatusResponse(
                            false,
                            AdminPairingState.Unpaired,
                            null,
                            null,
                            null,
                            null,
                            "NOT_RUN",
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            AdminPeerNotificationOperation.None,
                            AdminPeerNotificationResult.NotRun,
                            null));
            }

            public AdminHandlerResult<AdminServerUnitResponse> EnableSync(
                AdminEnableSyncRequest request)
            {
                TotalCalls++;
                LastEnableRequest = request;
                LastOperation = "EnableSync";
                return UnitSuccess();
            }

            public AdminHandlerResult<AdminServerUnitResponse> ConfirmPairing(
                AdminPairingConfirmationRequest request)
            {
                TotalCalls++;
                LastPairingConfirmationRequest = request;
                LastOperation = "ConfirmPairing";
                return UnitSuccess();
            }

            public AdminHandlerResult<AdminServerUnitResponse> CancelPairing(
                AdminPairingCancellationRequest request)
            {
                TotalCalls++;
                LastPairingCancellationRequest = request;
                LastOperation = "CancelPairing";
                return UnitSuccess();
            }

            public AdminHandlerResult<AdminServerSyncDisableResponse>
                DisableSync(AdminDisableSyncRequest request)
            {
                TotalCalls++;
                LastDisableRequest = request;
                LastOperation = "DisableSync";
                return AdminHandlerResult<AdminServerSyncDisableResponse>
                    .Success(
                        new AdminServerSyncDisableResponse(
                            AdminPairingState.Unpaired,
                            AdminPeerNotificationOperation.Revoke,
                            AdminPeerNotificationResult.Unconfirmed,
                            new DateTime(
                                2026,
                                7,
                                18,
                                0,
                                0,
                                0,
                                DateTimeKind.Utc)));
            }

            public AdminHandlerResult<AdminServerUnitResponse> SynchronizeNow()
            {
                TotalCalls++;
                LastOperation = "SynchronizeNow";
                return UnitSuccess();
            }

            public AdminHandlerResult<AdminServerLoggingResponse>
                GetLoggingSettings()
            {
                TotalCalls++;
                LastOperation = "GetLoggingSettings";
                if (LoggingException != null)
                {
                    throw LoggingException;
                }

                return LoggingError.HasValue
                    ? AdminHandlerResult<AdminServerLoggingResponse>.Failure(
                        LoggingError.Value)
                    : AdminHandlerResult<AdminServerLoggingResponse>.Success(
                        new AdminServerLoggingResponse(30));
            }

            public AdminHandlerResult<AdminServerLoggingResponse>
                PutLoggingSettings(AdminLoggingSettingsRequest request)
            {
                TotalCalls++;
                LastLoggingRequest = request;
                LastOperation = "PutLoggingSettings";
                return AdminHandlerResult<AdminServerLoggingResponse>.Success(
                    new AdminServerLoggingResponse(
                        request.LogRetentionDays));
            }

            public AdminHandlerResult<AdminServerCaStatusResponse>
                GetCaStatus()
            {
                TotalCalls++;
                LastOperation = "GetCaStatus";
                return AdminHandlerResult<AdminServerCaStatusResponse>
                    .Success(new AdminServerCaStatusResponse(
                        AdminCaState.NotProvisioned));
            }

            public AdminHandlerResult<AdminServerCaBackupResponse>
                CreateCaBackup(AdminCreateCaBackupRequest request)
            {
                TotalCalls++;
                LastOperation = "CreateCaBackup";
                return AdminHandlerResult<AdminServerCaBackupResponse>.Success(
                    new AdminServerCaBackupResponse(
                        "site-ca-00000000-0000-0000-0000-000000000001-20260719T000000000Z.dpca",
                        new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc),
                        new string('A', 43) + "="));
            }

            public AdminHandlerResult<AdminServerCertificatesResponse>
                GetCertificates(AdminCertificatesQuery query)
            {
                TotalCalls++;
                LastOperation = "GetCertificates";
                return AdminHandlerResult<AdminServerCertificatesResponse>
                    .Success(new AdminServerCertificatesResponse(
                        new List<AdminServerCertificateItem>().AsReadOnly(),
                        0,
                        null));
            }

            public AdminHandlerResult<
                AdminServerCertificateRevocationResponse> RevokeCertificate(
                    string serialNumber,
                    AdminRevokeCertificateRequest request)
            {
                TotalCalls++;
                LastOperation = "RevokeCertificate";
                return AdminHandlerResult<
                    AdminServerCertificateRevocationResponse>.Success(
                        new AdminServerCertificateRevocationResponse(
                            serialNumber,
                            new DateTime(
                                2026,
                                7,
                                19,
                                0,
                                0,
                                0,
                                DateTimeKind.Utc),
                            request.Reason,
                            2,
                            2,
                            false));
            }

            private static AdminHandlerResult<AdminServerUnitResponse>
                UnitSuccess()
            {
                return AdminHandlerResult<AdminServerUnitResponse>.Success(
                    Unit);
            }

            private static AdminHandlerResult<
                AdminServerRegistrationModeResponse>
                RegistrationModeSuccess()
            {
                return AdminHandlerResult<
                    AdminServerRegistrationModeResponse>.Success(
                        new AdminServerRegistrationModeResponse(
                            new AdminRegistrationModeStatus(
                                AdminRegistrationModeState.Closed,
                                null,
                                null,
                                null),
                            null));
            }
        }

        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream(byte[] contents)
                : base(contents, false)
            {
            }

            public int ReadCallCount { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCallCount++;
                return base.Read(buffer, offset, count);
            }
        }
    }
}
