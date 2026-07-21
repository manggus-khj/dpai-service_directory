using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class AdminApplicationHttpRequestHandlerTests
    {
        private static readonly DateTime MutationUtc =
            new DateTime(2026, 7, 19, 1, 2, 3, DateTimeKind.Utc);

        [TestMethod]
        public void ServicesPageIsOrdinalAndCursorPreservesRevisionAndFilter()
        {
            ServiceRecord cd34 = Active("CD34", "Charlie", 1UL);
            ServiceRecord ab12 = Active("AB12", "Alpha", 2UL);
            ServiceRecord ef56 = Active("EF56", "Deleted", 3UL)
                .MarkDeleted(MutationUtc, 4UL, TestData.OriginA);
            var snapshot = new DirectorySnapshot(
                new[] { cd34, ef56, ab12 },
                new PendingRegistration[0],
                4UL);
            HandlerFixture fixture = CreateFixture(snapshot);
            using (fixture.Handler)
            {
                AdminHandlerResult<AdminServerServicesResponse> first =
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(false, 1, null));

                Assert.IsTrue(first.IsSuccess);
                Assert.AreEqual(2, first.Value.TotalCount);
                Assert.AreEqual(1, first.Value.Items.Count);
                Assert.AreEqual(
                    "AB12",
                    first.Value.Items[0].Definition.ProductCode);
                Assert.IsNotNull(first.Value.NextCursor);
                Assert.AreEqual(96, first.Value.NextCursor.Length);
                Assert.IsFalse(first.Value.NextCursor.Contains("AB12"));

                AdminHandlerResult<AdminServerServicesResponse> second =
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(
                            false,
                            1,
                            first.Value.NextCursor));
                Assert.IsTrue(second.IsSuccess);
                Assert.AreEqual(
                    "CD34",
                    second.Value.Items[0].Definition.ProductCode);
                Assert.IsNull(second.Value.NextCursor);

                AssertError(
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(
                            true,
                            1,
                            first.Value.NextCursor)),
                    AdminServerErrorCode.Conflict);

                string tampered = Tamper(first.Value.NextCursor);
                AssertError(
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(false, 1, tampered)),
                    AdminServerErrorCode.Conflict);

                AdminHandlerResult<AdminServerServicesResponse> all =
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(true, 250, null));
                Assert.IsTrue(all.IsSuccess);
                CollectionAssert.AreEqual(
                    new[] { "AB12", "CD34", "EF56" },
                    all.Value.Items
                        .Select(item => item.Definition.ProductCode)
                        .ToArray());
                Assert.IsTrue(all.Value.Items[2].Deleted);
                Assert.AreEqual(
                    MutationUtc,
                    all.Value.Items[2].DeletedUtc.Value);
            }
        }

        [TestMethod]
        public void ServicesPagesStayWithinCanonicalBodyLimitWithoutSkipping()
        {
            const int requestedPageSize = 40;
            string maximumName = string.Concat(
                Enumerable.Repeat("\U0001F600", 128));
            ServiceRecord[] records = Enumerable
                .Range(0, requestedPageSize)
                .Select(index => Active(
                    "S" + index.ToString("D3"),
                    maximumName,
                    (ulong)index + 1UL))
                .ToArray();
            var snapshot = new DirectorySnapshot(
                records,
                new PendingRegistration[0],
                (ulong)requestedPageSize);
            HandlerFixture fixture = CreateFixture(snapshot);
            using (fixture.Handler)
            {
                var observed = new List<string>();
                string cursor = null;
                int pageCount = 0;
                do
                {
                    AdminHandlerResult<AdminServerServicesResponse> page =
                        fixture.Handler.GetServices(
                            new AdminServicesQuery(
                                false,
                                requestedPageSize,
                                cursor));

                    Assert.IsTrue(page.IsSuccess);
                    Assert.IsTrue(page.Value.Items.Count > 0);
                    Assert.IsTrue(
                        page.Value.Items.Count <= requestedPageSize);
                    Assert.IsTrue(
                        AdminServerResponseXmlCodec
                            .SerializeServicesResponse(page.Value)
                            .Length
                        <= AdminApiContract.MaximumBodyBytes);
                    observed.AddRange(
                        page.Value.Items.Select(
                            item => item.Definition.ProductCode));
                    cursor = page.Value.NextCursor;
                    pageCount++;
                    Assert.IsTrue(pageCount <= requestedPageSize);
                }
                while (cursor != null);

                Assert.IsTrue(pageCount > 1);
                CollectionAssert.AreEqual(
                    records
                        .Select(record =>
                            record.Definition.ProductCode.Value)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray(),
                    observed.ToArray());
            }
        }

        [TestMethod]
        public void SingleMaximumItemsCompleteWithoutContinuationCursor()
        {
            string maximumName = string.Concat(
                Enumerable.Repeat("\U0001F600", 128));
            ServiceRecord current = Active("S000", maximumName, 1UL);
            var snapshot = new DirectorySnapshot(
                new[] { current },
                new PendingRegistration[0],
                1UL);
            HandlerFixture fixture = CreateFixture(snapshot);
            using (fixture.Handler)
            {
                AdminHandlerResult<AdminServerServicesResponse> services =
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(false, 250, null));
                Assert.IsTrue(services.IsSuccess);
                Assert.AreEqual(1, services.Value.Items.Count);
                Assert.IsNull(services.Value.NextCursor);
                Assert.IsTrue(
                    AdminServerResponseXmlCodec
                        .SerializeServicesResponse(services.Value)
                        .Length
                    <= AdminApiContract.MaximumBodyBytes);

            }
        }

        [TestMethod]
        public void ServiceCursorConflictsAfterPersistedDirectoryChange()
        {
            var snapshot = new DirectorySnapshot(
                new[]
                {
                    Active("AB12", "Alpha", 1UL),
                    Active("CD34", "Charlie", 2UL)
                },
                new PendingRegistration[0],
                2UL);
            HandlerFixture fixture = CreateFixture(snapshot);
            using (fixture.Handler)
            {
                AdminHandlerResult<AdminServerServicesResponse> first =
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(false, 1, null));

                Assert.IsTrue(
                    fixture.Handler.DeleteService("CD34").IsSuccess);
                AssertError(
                    fixture.Handler.GetServices(
                        new AdminServicesQuery(
                            false,
                            1,
                            first.Value.NextCursor)),
                    AdminServerErrorCode.Conflict);
            }
        }

        [TestMethod]
        public void DeleteWritesTombstoneLogAndSchedulesButNotFoundDoesNeither()
        {
            var snapshot = new DirectorySnapshot(
                new[] { Active("AB12", "Service", 1UL) },
                new PendingRegistration[0],
                1UL);
            HandlerFixture fixture = CreateFixture(snapshot);
            using (fixture.Handler)
            {
                Assert.IsTrue(
                    fixture.Handler.DeleteService("AB12").IsSuccess);
                CollectionAssert.AreEqual(
                    new[] { "deleted:AB12:30" },
                    fixture.Log.Events.ToArray());
                Assert.AreEqual(1, fixture.Sync.ScheduleCallCount);

                AssertError(
                    fixture.Handler.DeleteService("AB12"),
                    AdminServerErrorCode.NotFound);
                AssertError(
                    fixture.Handler.DeleteService("ab12"),
                    AdminServerErrorCode.BadRequest);
                Assert.AreEqual(1, fixture.Log.Events.Count);
                Assert.AreEqual(1, fixture.Sync.ScheduleCallCount);
            }
        }

        [TestMethod]
        public void LoggingSettingCommitsBeforeCleanupAndCleanupCanBeRetried()
        {
            var order = new List<string>();
            HandlerFixture fixture = CreateFixture(
                DirectorySnapshot.Empty(),
                order);
            fixture.Log.ThrowOnRetention = true;
            AdminLoggingSettingsRequest request = LoggingRequest(7);
            using (fixture.Handler)
            {
                AssertError(
                    fixture.Handler.PutLoggingSettings(request),
                    AdminServerErrorCode.Internal);
                Assert.AreEqual(7, fixture.Configuration.Current.LogRetentionDays);
                CollectionAssert.AreEqual(
                    new[] { "config:7", "retention:7" },
                    order);

                fixture.Log.ThrowOnRetention = false;
                AdminHandlerResult<AdminServerLoggingResponse> retried =
                    fixture.Handler.PutLoggingSettings(request);
                Assert.IsTrue(retried.IsSuccess);
                Assert.AreEqual(7, retried.Value.LogRetentionDays);
                Assert.AreEqual(2, fixture.Configuration.SetCallCount);
                Assert.AreEqual(
                    7,
                    fixture.Handler.GetLoggingSettings()
                        .Value.LogRetentionDays);
            }
        }

        [TestMethod]
        public void LoggingPersistenceFailureDoesNotRunCleanup()
        {
            HandlerFixture fixture = CreateFixture(DirectorySnapshot.Empty());
            fixture.Configuration.UpdateStatus =
                AdminConfigurationUpdateStatus.PersistenceFailed;
            using (fixture.Handler)
            {
                AssertError(
                    fixture.Handler.PutLoggingSettings(LoggingRequest(7)),
                    AdminServerErrorCode.Internal);
                Assert.AreEqual(30, fixture.Configuration.Current.LogRetentionDays);
                Assert.AreEqual(0, fixture.Log.Events.Count);
            }
        }

        [TestMethod]
        public void RegistrationModeOpenRequiresReadyActiveIssuer()
        {
            CertificateAuthorityStatus[] unavailableStatuses =
            {
                new CertificateAuthorityStatus(
                    CertificateAuthorityOperationalState.NotProvisioned),
                CreateCaStatus(
                    CertificateAuthorityOperationalState.BackupRequired,
                    CertificateAuthorityIssuerRole.ActiveIssuer),
                CreateCaStatus(
                    CertificateAuthorityOperationalState.Ready,
                    CertificateAuthorityIssuerRole.Standby)
            };

            foreach (CertificateAuthorityStatus caStatus in unavailableStatuses)
            {
                HandlerFixture fixture = CreateFixture(
                    DirectorySnapshot.Empty(),
                    caStatus: caStatus);
                using (fixture.Handler)
                {
                    AssertError(
                        fixture.Handler.OpenRegistrationMode(),
                        AdminServerErrorCode.Conflict);
                    Assert.AreEqual(
                        AdminRegistrationModeState.Closed,
                        fixture.Handler.GetRegistrationMode()
                            .Value.RegistrationMode.State);
                }
            }
        }

        [TestMethod]
        public void RegistrationModeOpenAndCloseUseTheInjectedOwner()
        {
            var clock = new FakeRegistrationModeClock(MutationUtc);
            var owner = new RegistrationModeOwner(
                new StateMutationGate(),
                clock);
            HandlerFixture fixture = CreateFixture(
                DirectorySnapshot.Empty(),
                caStatus: CreateCaStatus(
                    CertificateAuthorityOperationalState.Ready,
                    CertificateAuthorityIssuerRole.ActiveIssuer),
                registrationModeOwner: owner);
            using (fixture.Handler)
            {
                AdminServerRegistrationModeResponse opened = fixture.Handler
                    .OpenRegistrationMode().Value;
                Assert.AreEqual(
                    AdminRegistrationModeState.Open,
                    opened.RegistrationMode.State);
                Assert.AreEqual(
                    MutationUtc,
                    opened.RegistrationMode.OpenedUtc.Value);
                Assert.AreEqual(
                    RegistrationModeOwner.DurationSeconds,
                    opened.RegistrationMode.RemainingSeconds.Value);
                Assert.IsNull(opened.LastRegistration);

                clock.Advance(TimeSpan.FromMinutes(5));
                AdminServerRegistrationModeResponse reopened = fixture.Handler
                    .OpenRegistrationMode().Value;
                Assert.AreEqual(
                    MutationUtc,
                    reopened.RegistrationMode.OpenedUtc.Value);
                Assert.AreEqual(
                    RegistrationModeOwner.DurationSeconds - 300,
                    reopened.RegistrationMode.RemainingSeconds.Value);

                Assert.AreEqual(
                    AdminRegistrationModeState.Closed,
                    fixture.Handler.CloseRegistrationMode()
                        .Value.RegistrationMode.State);
                Assert.AreEqual(
                    AdminRegistrationModeState.Closed,
                    fixture.Handler.CloseRegistrationMode()
                        .Value.RegistrationMode.State);
            }
        }

        [TestMethod]
        public void ClaimedRegistrationModeRejectsOpenAndClose()
        {
            var owner = new RegistrationModeOwner(
                new StateMutationGate(),
                new FakeRegistrationModeClock(MutationUtc));
            owner.Open();
            RegistrationModeClaimResult claimed =
                owner.TryClaimValidatedRequest(true);
            HandlerFixture fixture = CreateFixture(
                DirectorySnapshot.Empty(),
                caStatus: CreateCaStatus(
                    CertificateAuthorityOperationalState.Ready,
                    CertificateAuthorityIssuerRole.ActiveIssuer),
                registrationModeOwner: owner);
            using (claimed.Claim)
            using (fixture.Handler)
            {
                AssertError(
                    fixture.Handler.OpenRegistrationMode(),
                    AdminServerErrorCode.Conflict);
                AssertError(
                    fixture.Handler.CloseRegistrationMode(),
                    AdminServerErrorCode.Conflict);
                Assert.AreEqual(
                    AdminRegistrationModeState.Claimed,
                    fixture.Handler.GetRegistrationMode()
                        .Value.RegistrationMode.State);
            }
        }

        [TestMethod]
        public void SynchronizationOperationsDelegateWithoutOwningPeerState()
        {
            HandlerFixture fixture = CreateFixture(DirectorySnapshot.Empty());
            AdminEnableSyncRequest enable = ParseEnable();
            AdminPairingConfirmationRequest confirm = ParseConfirm(true);
            AdminPairingCancellationRequest cancel = ParseCancel();
            AdminDisableSyncRequest disable = ParseDisable();
            using (fixture.Handler)
            {
                Assert.IsTrue(fixture.Handler.GetSyncStatus().IsSuccess);
                Assert.IsTrue(fixture.Handler.EnableSync(enable).IsSuccess);
                Assert.IsTrue(fixture.Handler.ConfirmPairing(confirm).IsSuccess);
                Assert.IsTrue(fixture.Handler.CancelPairing(cancel).IsSuccess);
                Assert.IsTrue(fixture.Handler.DisableSync(disable).IsSuccess);
                Assert.IsTrue(fixture.Handler.SynchronizeNow().IsSuccess);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "status", "enable", "confirm", "cancel",
                        "disable", "now"
                    },
                    fixture.Sync.Operations.ToArray());

                AssertError(
                    fixture.Handler.ConfirmPairing(ParseConfirm(false)),
                    AdminServerErrorCode.Conflict);
                Assert.AreEqual(6, fixture.Sync.Operations.Count);
            }
        }

        private static HandlerFixture CreateFixture(
            DirectorySnapshot snapshot,
            IList<string> order = null,
            CertificateAuthorityStatus caStatus = null,
            RegistrationModeOwner registrationModeOwner = null)
        {
            var store = new FakeStateStore(snapshot, order);
            StateCoordinatorOpenResult opened =
                StateMutationCoordinator.Open(store);
            Assert.IsTrue(opened.IsSuccess);
            var configuration = new FakeConfigurationState(order);
            var log = new FakeAdminSystemLog(order);
            var sync = new FakeSynchronizationController(order);
            var cursorKey = Enumerable.Range(0, 32)
                .Select(value => (byte)value)
                .ToArray();
            var handler = new AdminApplicationHttpRequestHandler(
                opened.Coordinator,
                configuration,
                log,
                sync,
                () => MutationUtc,
                new AdminCursorCodec(cursorKey),
                new FakeCertificateAuthorityAdministration(
                    caStatus
                        ?? new CertificateAuthorityStatus(
                            CertificateAuthorityOperationalState
                                .NotProvisioned)),
                registrationModeOwner
                    ?? new RegistrationModeOwner(
                        new StateMutationGate(),
                        new FakeRegistrationModeClock(MutationUtc)));
            return new HandlerFixture(
                handler,
                opened.Coordinator,
                store,
                configuration,
                log,
                sync);
        }

        private static CertificateAuthorityStatus CreateCaStatus(
            CertificateAuthorityOperationalState state,
            CertificateAuthorityIssuerRole role)
        {
            CertificateSerialNumber serialNumber;
            Assert.IsTrue(
                CertificateSerialNumber.TryCreate(
                    "01A4B5C6D7E8F90123456789ABCDEF01",
                    out serialNumber));
            return new CertificateAuthorityStatus(
                state,
                role,
                TestData.OriginA,
                TestData.OriginB,
                serialNumber,
                new byte[32],
                MutationUtc.AddDays(-1),
                MutationUtc.AddYears(1),
                1UL,
                1UL,
                state == CertificateAuthorityOperationalState.Ready
                    ? (DateTime?)MutationUtc
                    : null);
        }

        private static ServiceRecord Active(
            string productCode,
            string name,
            ulong logicalVersion)
        {
            return ServiceRecord.CreateActive(
                Definition(productCode, name),
                MutationUtc.AddMinutes(-10),
                logicalVersion,
                TestData.OriginA);
        }

        private static ServiceDefinition Definition(
            string productCode,
            string name)
        {
            return TestData.Definition(
                name: name,
                productCode: productCode,
                serviceIpv4Address: "10.0.0.1",
                port: 21000);
        }

        private static string Tamper(string cursor)
        {
            char replacement = cursor[0] == 'A' ? 'B' : 'A';
            return replacement + cursor.Substring(1);
        }

        private static AdminLoggingSettingsRequest LoggingRequest(int days)
        {
            return AdminServerXmlCodec.ParseLoggingSettingsRequest(
                Utf8(
                    "<LoggingSettings xmlns=\"urn:deepai:service-directory:admin\">"
                    + "<LogRetentionDays>"
                    + days
                    + "</LogRetentionDays></LoggingSettings>"));
        }

        private static AdminEnableSyncRequest ParseEnable()
        {
            return AdminServerXmlCodec.ParseEnableSyncRequest(
                Utf8(
                    "<EnableSync xmlns=\"urn:deepai:service-directory:admin\">"
                    + "<PeerEndpoint>https://10.0.0.2:21000</PeerEndpoint>"
                    + "<RePair>false</RePair></EnableSync>"));
        }

        private static AdminPairingConfirmationRequest ParseConfirm(
            bool confirmed)
        {
            return AdminServerXmlCodec.ParsePairingConfirmationRequest(
                Utf8(
                    "<PairingConfirmation xmlns=\"urn:deepai:service-directory:admin\">"
                    + "<PairingId>11111111-1111-4111-8111-111111111111</PairingId>"
                    + "<Confirmed>"
                    + (confirmed ? "true" : "false")
                    + "</Confirmed></PairingConfirmation>"));
        }

        private static AdminPairingCancellationRequest ParseCancel()
        {
            return AdminServerXmlCodec.ParsePairingCancellationRequest(
                Utf8(
                    "<PairingCancellation xmlns=\"urn:deepai:service-directory:admin\">"
                    + "<PairingId>11111111-1111-4111-8111-111111111111</PairingId>"
                    + "</PairingCancellation>"));
        }

        private static AdminDisableSyncRequest ParseDisable()
        {
            return AdminServerXmlCodec.ParseDisableSyncRequest(
                Utf8(
                    "<DisableSync xmlns=\"urn:deepai:service-directory:admin\">"
                    + "<ForgetPeer>false</ForgetPeer></DisableSync>"));
        }

        private static byte[] Utf8(string value)
        {
            return new UTF8Encoding(false, true).GetBytes(value);
        }

        private static void AssertError<T>(
            AdminHandlerResult<T> result,
            AdminServerErrorCode expected)
            where T : class
        {
            Assert.IsNotNull(result);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(expected, result.ErrorCode.Value);
        }

        private sealed class HandlerFixture
        {
            internal HandlerFixture(
                AdminApplicationHttpRequestHandler handler,
                StateMutationCoordinator coordinator,
                FakeStateStore store,
                FakeConfigurationState configuration,
                FakeAdminSystemLog log,
                FakeSynchronizationController sync)
            {
                Handler = handler;
                Coordinator = coordinator;
                Store = store;
                Configuration = configuration;
                Log = log;
                Sync = sync;
            }

            internal AdminApplicationHttpRequestHandler Handler { get; }

            internal StateMutationCoordinator Coordinator { get; }

            internal FakeStateStore Store { get; }

            internal FakeConfigurationState Configuration { get; }

            internal FakeAdminSystemLog Log { get; }

            internal FakeSynchronizationController Sync { get; }
        }

        private sealed class FakeStateStore : IServiceDirectoryStateStore
        {
            private readonly DirectorySnapshot _initial;
            private readonly IList<string> _order;

            internal FakeStateStore(
                DirectorySnapshot initial,
                IList<string> order)
            {
                _initial = initial;
                _order = order;
                CommitResult = StateCommitResult.Success();
            }

            internal StateCommitResult CommitResult { get; set; }

            internal int CommitCallCount { get; private set; }

            public StateLoadResult Load()
            {
                return StateLoadResult.Success(_initial);
            }

            public StateCommitResult Commit(
                DirectorySnapshot expectedSnapshot,
                DirectorySnapshot nextSnapshot)
            {
                CommitCallCount++;
                _order?.Add("commit");
                return CommitResult;
            }
        }

        private sealed class FakeConfigurationState :
            IAdminConfigurationState
        {
            private readonly IList<string> _order;

            internal FakeConfigurationState(IList<string> order)
            {
                _order = order;
                UpdateStatus = AdminConfigurationUpdateStatus.Completed;
                Current = ServiceDirectoryConfiguration.CreateInitial(
                    TestData.DirectoryIdentity(
                        "management.internal",
                        "10.0.0.10"),
                    new Guid("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
            }

            internal ServiceDirectoryConfiguration Current { get; private set; }

            internal int SetCallCount { get; private set; }

            internal AdminConfigurationUpdateStatus UpdateStatus { get; set; }

            public ServiceDirectoryConfiguration GetCurrent()
            {
                return Current;
            }

            public AdminConfigurationUpdateResult SetLogRetentionDays(
                int logRetentionDays)
            {
                SetCallCount++;
                _order?.Add("config:" + logRetentionDays);
                if (UpdateStatus != AdminConfigurationUpdateStatus.Completed)
                {
                    return AdminConfigurationUpdateResult.Failure(
                        UpdateStatus);
                }

                Current = Current.WithLogRetentionDays(logRetentionDays);
                return AdminConfigurationUpdateResult.Success(Current);
            }
        }

        private sealed class FakeAdminSystemLog : IAdminSystemLogSink
        {
            private readonly IList<string> _order;

            internal FakeAdminSystemLog(IList<string> order)
            {
                _order = order;
            }

            internal List<string> Events { get; } = new List<string>();

            internal bool ThrowOnRetention { get; set; }

            internal bool ThrowOnWrite { get; set; }

            internal bool ThrowRetentionAfterWrite { get; set; }

            public void WriteRegisteredServiceCreated(
                ProductCode productCode,
                int retentionDays)
            {
                Add("created", productCode, retentionDays);
            }

            public void WriteRegisteredServiceUpdated(
                ProductCode productCode,
                int retentionDays)
            {
                Add("updated", productCode, retentionDays);
            }

            public void WriteRegisteredServiceDeleted(
                ProductCode productCode,
                int retentionDays)
            {
                Add("deleted", productCode, retentionDays);
            }

            public void ApplyRetention(int retentionDays)
            {
                string value = "retention:" + retentionDays;
                Events.Add(value);
                _order?.Add(value);
                if (ThrowOnRetention)
                {
                    throw new IOException("Injected retention failure.");
                }
            }

            private void Add(
                string eventName,
                ProductCode productCode,
                int retentionDays)
            {
                string value = eventName
                    + ":"
                    + productCode.Value
                    + ":"
                    + retentionDays;
                Events.Add(value);
                _order?.Add(value);
                if (ThrowOnWrite)
                {
                    throw new IOException("Injected system log failure.");
                }

                if (ThrowRetentionAfterWrite)
                {
                    throw new SystemLogRetentionAfterWriteException(
                        new IOException(
                            "Injected retention cleanup failure."));
                }
            }
        }

        private sealed class FakeSynchronizationController :
            IAdminSynchronizationController
        {
            private readonly IList<string> _order;

            internal FakeSynchronizationController(IList<string> order)
            {
                _order = order;
            }

            internal List<string> Operations { get; } = new List<string>();

            internal int ScheduleCallCount { get; private set; }

            public AdminHandlerResult<AdminServerSyncStatusResponse>
                GetStatus()
            {
                Operations.Add("status");
                return AdminHandlerResult<AdminServerSyncStatusResponse>
                    .Success(UnpairedStatus());
            }

            public AdminHandlerResult<AdminServerUnitResponse> Enable(
                AdminEnableSyncRequest request)
            {
                Operations.Add("enable");
                return Unit();
            }

            public AdminHandlerResult<AdminServerUnitResponse> ConfirmPairing(
                AdminPairingConfirmationRequest request)
            {
                Operations.Add("confirm");
                return Unit();
            }

            public AdminHandlerResult<AdminServerUnitResponse> CancelPairing(
                AdminPairingCancellationRequest request)
            {
                Operations.Add("cancel");
                return Unit();
            }

            public AdminHandlerResult<AdminServerSyncDisableResponse> Disable(
                AdminDisableSyncRequest request)
            {
                Operations.Add("disable");
                return AdminHandlerResult<AdminServerSyncDisableResponse>
                    .Success(
                        new AdminServerSyncDisableResponse(
                            AdminPairingState.PairedDisabled,
                            AdminPeerNotificationOperation.Release,
                            AdminPeerNotificationResult.NotRequired,
                            MutationUtc));
            }

            public AdminHandlerResult<AdminServerUnitResponse>
                SynchronizeNow()
            {
                Operations.Add("now");
                return Unit();
            }

            public void ScheduleDirectoryChanged()
            {
                ScheduleCallCount++;
                _order?.Add("schedule");
            }

            private static AdminHandlerResult<AdminServerUnitResponse> Unit()
            {
                return AdminHandlerResult<AdminServerUnitResponse>.Success(
                    AdminServerUnitResponse.Value);
            }

            private static AdminServerSyncStatusResponse UnpairedStatus()
            {
                return new AdminServerSyncStatusResponse(
                    enabled: false,
                    pairingState: AdminPairingState.Unpaired,
                    peerEndpoint: null,
                    peerInstanceId: null,
                    keyEpoch: null,
                    lastSyncUtc: null,
                    lastResult: "NOT_RUN",
                    clockSkewSeconds: null,
                    pairingId: null,
                    sas: null,
                    pairingExpiresUtc: null,
                    pairingRemainingSeconds: null,
                    localConfirmed: null,
                    remoteConfirmed: null,
                    commitExpiresUtc: null,
                    localCommitConfirmed: null,
                    remoteCommitConfirmed: null,
                    lastPeerNotificationOperation:
                        AdminPeerNotificationOperation.None,
                    lastPeerNotificationResult:
                        AdminPeerNotificationResult.NotRun,
                    lastPeerNotificationUtc: null);
            }
        }

        private sealed class FakeRegistrationModeClock :
            IRegistrationModeClock
        {
            internal FakeRegistrationModeClock(DateTime utcNow)
            {
                UtcNow = utcNow;
            }

            public DateTime UtcNow { get; private set; }

            public TimeSpan MonotonicElapsed { get; private set; }

            internal void Advance(TimeSpan elapsed)
            {
                UtcNow = UtcNow.Add(elapsed);
                MonotonicElapsed = MonotonicElapsed.Add(elapsed);
            }
        }

        private sealed class FakeCertificateAuthorityAdministration :
            ICertificateAuthorityAdministration
        {
            private readonly CertificateAuthorityStatus _status;

            internal FakeCertificateAuthorityAdministration(
                CertificateAuthorityStatus status)
            {
                _status = status ?? throw new ArgumentNullException(
                    nameof(status));
            }

            public CertificateAuthorityStatus GetStatus()
            {
                return _status;
            }

            public CertificateAuthorityBackupResult CreateBackup(
                string password,
                DateTime createdUtc)
            {
                throw new NotSupportedException();
            }

            public CertificateLedgerSnapshot GetLedgerSnapshot()
            {
                throw new NotSupportedException();
            }

            public CertificateRevocationResult Revoke(
                string serialNumber,
                CertificateRevocationReason reason,
                DateTime revokedUtc)
            {
                throw new NotSupportedException();
            }
        }

    }
}
