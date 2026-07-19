using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.InternalProtocol
{
    [TestClass]
    public sealed class AdminServerResponseXmlCodecTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void AdminResponseUsesTheFixedEmbeddedSchema()
        {
            CollectionAssert.Contains(
                typeof(AdminServerResponseXmlCodec).Assembly
                    .GetManifestResourceNames(),
                "DEEPAi.ServiceDirectory.InternalProtocol.Admin.admin.xsd");
        }

        [TestMethod]
        public void ServicesResponseRoundTripsCanonicalActiveAndDeletedItems()
        {
            DateTime activeUtc = Utc(2).AddTicks(1234000);
            DateTime deletedUtc = Utc(4);
            var source = new List<AdminServerServiceItem>
            {
                CreateServiceItem(
                    "Active App",
                    "ABCD",
                    "service.internal",
                    activeUtc,
                    false,
                    null),
                CreateServiceItem(
                    "Old App",
                    "WXYZ",
                    "10.0.0.7",
                    Utc(3),
                    true,
                    deletedUtc)
            };
            var response = new AdminServerServicesResponse(
                source,
                4,
                "opaque-cursor");
            source.Clear();

            byte[] body = AdminServerResponseXmlCodec
                .SerializeServicesResponse(response);
            string xml = Decode(body);
            AdminResponse<AdminPage<AdminServiceItem>> parsed =
                AdminXmlCodec.ParseServicesResponse(body);

            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(2, parsed.Payload.Items.Count);
            Assert.AreEqual(4, parsed.Payload.TotalCount);
            Assert.AreEqual("opaque-cursor", parsed.Payload.NextCursor);
            Assert.AreEqual("ABCD", parsed.Payload.Items[0].ProductCode);
            Assert.IsFalse(parsed.Payload.Items[0].Deleted);
            Assert.IsNull(parsed.Payload.Items[0].DeletedUtc);
            Assert.AreEqual("WXYZ", parsed.Payload.Items[1].ProductCode);
            Assert.IsTrue(parsed.Payload.Items[1].Deleted);
            Assert.AreEqual(deletedUtc,
                parsed.Payload.Items[1].DeletedUtc.Value);
            StringAssert.Contains(
                xml,
                "<LastModifiedUtc>2026-07-18T00:02:00.1234Z"
                    + "</LastModifiedUtc>");
            Assert.IsTrue(
                xml.IndexOf("<Services>", StringComparison.Ordinal)
                    < xml.IndexOf("<TotalCount>", StringComparison.Ordinal));
            Assert.IsTrue(
                xml.IndexOf("<TotalCount>", StringComparison.Ordinal)
                    < xml.IndexOf("<NextCursor>", StringComparison.Ordinal));
            Assert.IsFalse(xml.Contains("LogicalVersion"));
            Assert.IsFalse(xml.Contains("OriginInstanceId"));
            Assert.IsFalse(xml.Contains("<Extensions"));
            Assert.IsTrue(body.Length <= AdminApiContract.MaximumBodyBytes);
        }

        [TestMethod]
        public void ServicesResponseAcceptsExactSupplementaryNameBoundary()
        {
            string supplementaryName = string.Concat(
                Repeat("\U0001F600", 128));
            var response = new AdminServerServicesResponse(
                new[]
                {
                    CreateServiceItem(
                        supplementaryName,
                        "ABCD",
                        "service.internal",
                        Utc(0),
                        false,
                        null)
                },
                1,
                null);

            AdminResponse<AdminPage<AdminServiceItem>> parsed =
                AdminXmlCodec.ParseServicesResponse(
                    AdminServerResponseXmlCodec
                        .SerializeServicesResponse(response));

            Assert.AreEqual(supplementaryName,
                parsed.Payload.Items[0].Name);
            Assert.AreEqual(512,
                StrictUtf8.GetByteCount(parsed.Payload.Items[0].Name));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServiceDefinition(
                    string.Concat(Repeat("\U0001F600", 129)),
                    "ABCD",
                    "service.internal",
                    21000));
        }

        [TestMethod]
        public void EmptyPagesStillRoundTripRequiredTotalCount()
        {
            AdminPage<AdminServiceItem> services = AdminXmlCodec
                .ParseServicesResponse(
                    AdminServerResponseXmlCodec.SerializeServicesResponse(
                        new AdminServerServicesResponse(
                            new AdminServerServiceItem[0],
                            0,
                            null)))
                .Payload;
            Assert.AreEqual(0, services.Items.Count);
            Assert.AreEqual(0, services.TotalCount);

            AdminPage<AdminPendingItem> pending = AdminXmlCodec
                .ParsePendingResponse(
                    AdminServerResponseXmlCodec.SerializePendingResponse(
                        new AdminServerPendingResponse(
                            new AdminServerPendingItem[0],
                            0,
                            null)))
                .Payload;
            Assert.AreEqual(0, pending.Items.Count);
            Assert.AreEqual(0, pending.TotalCount);
        }

        [TestMethod]
        public void ServicesModelsRejectInvalidShapeOrderingAndPageSize()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServiceDefinition(
                    " App ",
                    "ABCD",
                    "service.internal",
                    21000));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServiceDefinition(
                    "App",
                    "ABCD",
                    "999.1.1.1",
                    21000));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServiceDefinition(
                    "App",
                    "abcd",
                    "service.internal",
                    21000));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServiceDefinition(
                    "App",
                    "ABCD",
                    " service.internal ",
                    21000));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateServiceItem(
                    "App",
                    "ABCD",
                    "service.internal",
                    Utc(0),
                    false,
                    Utc(1)));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateServiceItem(
                    "App",
                    "ABCD",
                    "service.internal",
                    DateTime.SpecifyKind(Utc(0), DateTimeKind.Local),
                    false,
                    null));

            AdminServerServiceItem abcd = CreateServiceItem(
                "A",
                "ABCD",
                "service.internal",
                Utc(0),
                false,
                null);
            AdminServerServiceItem wxyz = CreateServiceItem(
                "W",
                "WXYZ",
                "service.internal",
                Utc(0),
                false,
                null);
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServicesResponse(
                    new[] { wxyz, abcd },
                    2,
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServicesResponse(
                    new[] { abcd, abcd },
                    2,
                    null));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new AdminServerServicesResponse(
                    new[] { abcd, wxyz },
                    1,
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServicesResponse(
                    new[] { abcd },
                    1,
                    " "));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServicesResponse(
                    new[] { abcd },
                    1,
                    "unexpected-cursor"));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerServicesResponse(
                    new AdminServerServiceItem[0],
                    1,
                    null));

            var tooMany = new List<AdminServerServiceItem>();
            for (int index = 0;
                index <= AdminApiContract.PageSize;
                index++)
            {
                tooMany.Add(CreateServiceItem(
                    "App",
                    CreateProductCode(index),
                    "service.internal",
                    Utc(0),
                    false,
                    null));
            }

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new AdminServerServicesResponse(
                    tooMany,
                    tooMany.Count,
                    null));
        }

        [TestMethod]
        public void PendingResponseRoundTripsNewAndModifyInRequiredOrder()
        {
            DateTime requestedUtc = Utc(1);
            var first = new AdminServerPendingItem(
                new Guid("11111111-1111-1111-1111-111111111111"),
                AdminPendingRequestType.New,
                requestedUtc,
                "10.0.0.5",
                CreateDefinition("New App", "ABCD", "10.0.0.9"),
                null);
            var second = new AdminServerPendingItem(
                new Guid("22222222-2222-2222-2222-222222222222"),
                AdminPendingRequestType.Modify,
                requestedUtc,
                "2001:db8::1",
                CreateDefinition("Changed App", "WXYZ", "10.0.0.8"),
                CreateDefinition("Current App", "WXYZ", "10.0.0.7"));
            var response = new AdminServerPendingResponse(
                new[] { first, second },
                2,
                null);

            byte[] body = AdminServerResponseXmlCodec
                .SerializePendingResponse(response);
            string xml = Decode(body);
            AdminResponse<AdminPage<AdminPendingItem>> parsed =
                AdminXmlCodec.ParsePendingResponse(body);

            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(2, parsed.Payload.Items.Count);
            Assert.AreEqual(AdminPendingRequestType.New,
                parsed.Payload.Items[0].Type);
            Assert.IsNull(parsed.Payload.Items[0].Current);
            Assert.AreEqual(AdminPendingRequestType.Modify,
                parsed.Payload.Items[1].Type);
            Assert.IsNotNull(parsed.Payload.Items[1].Current);
            StringAssert.Contains(
                xml,
                "<Id>11111111-1111-1111-1111-111111111111</Id>");
            Assert.IsTrue(
                xml.IndexOf("<Requested>", StringComparison.Ordinal)
                    < xml.IndexOf("<Current>", StringComparison.Ordinal));
            Assert.IsFalse(xml.Contains("<NextCursor>"));
            AssertExtensionsAbsent(body);
        }

        [TestMethod]
        public void PendingModelsRejectTypeIpProductAndOrderingViolations()
        {
            AdminServerServiceDefinition abcd = CreateDefinition(
                "App",
                "ABCD",
                "service.internal");
            AdminServerServiceDefinition wxyz = CreateDefinition(
                "Other",
                "WXYZ",
                "service.internal");
            Guid id = new Guid(
                "11111111-1111-1111-1111-111111111111");

            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerPendingItem(
                    id,
                    AdminPendingRequestType.New,
                    Utc(0),
                    "10.0.0.5",
                    abcd,
                    abcd));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerPendingItem(
                    id,
                    AdminPendingRequestType.Modify,
                    Utc(0),
                    "10.0.0.5",
                    abcd,
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerPendingItem(
                    id,
                    AdminPendingRequestType.Modify,
                    Utc(0),
                    "010.0.0.5",
                    abcd,
                    abcd));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerPendingItem(
                    id,
                    AdminPendingRequestType.Modify,
                    Utc(0),
                    "10.0.0.5",
                    abcd,
                    wxyz));

            var later = new AdminServerPendingItem(
                id,
                AdminPendingRequestType.New,
                Utc(2),
                "10.0.0.5",
                abcd,
                null);
            var earlier = new AdminServerPendingItem(
                new Guid("22222222-2222-2222-2222-222222222222"),
                AdminPendingRequestType.New,
                Utc(1),
                "10.0.0.5",
                wxyz,
                null);
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerPendingResponse(
                    new[] { later, earlier },
                    2,
                    null));
        }

        [TestMethod]
        public void SasPendingSyncStatusRoundTripsConditionalFieldsInOrder()
        {
            Guid peerId = new Guid(
                "9f2ed127-9834-42b4-a379-eaad9df8fcec");
            Guid pairingId = new Guid(
                "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12");
            var response = new AdminServerSyncStatusResponse(
                false,
                AdminPairingState.SasPending,
                "http://10.0.0.2:21000",
                peerId,
                null,
                null,
                "NOT_RUN",
                null,
                pairingId,
                "00427193",
                Utc(5),
                247,
                false,
                false,
                null,
                null,
                null,
                AdminPeerNotificationOperation.None,
                AdminPeerNotificationResult.NotRun,
                null);

            byte[] body = AdminServerResponseXmlCodec
                .SerializeSyncStatusResponse(response);
            string xml = Decode(body);
            AdminResponse<AdminSyncStatus> parsed =
                AdminXmlCodec.ParseSyncResponse(body);

            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(AdminPairingState.SasPending,
                parsed.Payload.PairingState);
            Assert.AreEqual(peerId, parsed.Payload.PeerInstanceId.Value);
            Assert.AreEqual(pairingId, parsed.Payload.PairingId.Value);
            Assert.AreEqual("00427193", parsed.Payload.Sas);
            Assert.AreEqual(247,
                parsed.Payload.PairingRemainingSeconds.Value);
            Assert.IsFalse(parsed.Payload.LocalConfirmed.Value);
            Assert.IsFalse(parsed.Payload.RemoteConfirmed.Value);
            Assert.IsNull(parsed.Payload.KeyEpoch);
            Assert.IsNull(parsed.Payload.LastSyncUtc);
            Assert.IsNull(parsed.Payload.ClockSkewSeconds);
            StringAssert.Contains(
                xml,
                "<PeerInstanceId>"
                    + "9f2ed127-9834-42b4-a379-eaad9df8fcec"
                    + "</PeerInstanceId>");
            StringAssert.Contains(
                xml,
                "<PairingId>"
                    + "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12"
                    + "</PairingId>");
            AssertElementOrder(
                xml,
                "Enabled",
                "PairingState",
                "PeerEndpoint",
                "PeerInstanceId",
                "PairingId",
                "PairingExpiresUtc",
                "PairingRemainingSeconds",
                "Sas",
                "LocalConfirmed",
                "RemoteConfirmed",
                "LastResult",
                "LastPeerNotificationOperation",
                "LastPeerNotificationResult");
            AssertExtensionsAbsent(body);
        }

        [TestMethod]
        public void EnabledAndCommitSyncStatusesRoundTripExactShapes()
        {
            Guid peerId = new Guid(
                "9f2ed127-9834-42b4-a379-eaad9df8fcec");
            var enabled = new AdminServerSyncStatusResponse(
                true,
                AdminPairingState.Enabled,
                "http://10.0.0.2:21000",
                peerId,
                2,
                Utc(3),
                "OK",
                -2,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                AdminPeerNotificationOperation.Release,
                AdminPeerNotificationResult.Confirmed,
                Utc(2));
            byte[] enabledBody = AdminServerResponseXmlCodec
                .SerializeSyncStatusResponse(enabled);
            AdminSyncStatus enabledParsed = AdminXmlCodec.ParseSyncResponse(
                enabledBody).Payload;
            Assert.IsTrue(enabledParsed.Enabled);
            Assert.AreEqual((ulong)2, enabledParsed.KeyEpoch.Value);
            Assert.AreEqual((long)-2,
                enabledParsed.ClockSkewSeconds.Value);
            AssertExtensionsAbsent(enabledBody);

            var commit = new AdminServerSyncStatusResponse(
                false,
                AdminPairingState.PairedPendingCommit,
                "http://10.0.0.2:21000",
                peerId,
                3,
                null,
                "NOT_RUN",
                null,
                new Guid("b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12"),
                null,
                null,
                null,
                null,
                null,
                Utc(9),
                true,
                false,
                AdminPeerNotificationOperation.None,
                AdminPeerNotificationResult.NotRun,
                null);
            byte[] commitBody = AdminServerResponseXmlCodec
                .SerializeSyncStatusResponse(commit);
            AdminSyncStatus commitParsed = AdminXmlCodec.ParseSyncResponse(
                commitBody).Payload;
            Assert.AreEqual(AdminPairingState.PairedPendingCommit,
                commitParsed.PairingState);
            Assert.AreEqual(Utc(9), commitParsed.CommitExpiresUtc.Value);
            Assert.IsTrue(commitParsed.LocalCommitConfirmed.Value);
            Assert.IsFalse(commitParsed.RemoteCommitConfirmed.Value);
            Assert.IsNull(commitParsed.PairingExpiresUtc);
            Assert.IsNull(commitParsed.Sas);
            AssertExtensionsAbsent(commitBody);
        }

        [TestMethod]
        public void RemainingPairingStatesRoundTripTheirExactShapes()
        {
            const string Endpoint = "http://10.0.0.2:21000";
            Guid peerId = new Guid(
                "9f2ed127-9834-42b4-a379-eaad9df8fcec");
            Guid pairingId = new Guid(
                "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12");
            var responses = new[]
            {
                CreateSyncStatus(AdminPairingState.Unpaired),
                CreateSyncStatus(
                    AdminPairingState.PairingWindowOpen,
                    Endpoint,
                    pairingExpiresUtc: Utc(5),
                    pairingRemainingSeconds: 300),
                CreateSyncStatus(
                    AdminPairingState.Negotiating,
                    Endpoint,
                    pairingId: pairingId,
                    pairingExpiresUtc: Utc(5),
                    pairingRemainingSeconds: 200),
                CreateSyncStatus(
                    AdminPairingState.Negotiating,
                    Endpoint,
                    peerInstanceId: peerId,
                    pairingId: pairingId,
                    pairingExpiresUtc: Utc(5),
                    pairingRemainingSeconds: 100),
                CreateSyncStatus(
                    AdminPairingState.BothConfirmed,
                    Endpoint,
                    peerInstanceId: peerId,
                    pairingId: pairingId,
                    pairingExpiresUtc: Utc(5),
                    pairingRemainingSeconds: 0,
                    localConfirmed: true,
                    remoteConfirmed: true),
                CreateSyncStatus(
                    AdminPairingState.PairedDisabled,
                    Endpoint,
                    peerInstanceId: peerId,
                    keyEpoch: 4)
            };

            foreach (AdminServerSyncStatusResponse response in responses)
            {
                byte[] body = AdminServerResponseXmlCodec
                    .SerializeSyncStatusResponse(response);
                AdminSyncStatus parsed = AdminXmlCodec
                    .ParseSyncResponse(body)
                    .Payload;

                Assert.AreEqual(response.PairingState, parsed.PairingState);
                Assert.AreEqual(response.Enabled, parsed.Enabled);
                AssertExtensionsAbsent(body);
            }
        }

        [TestMethod]
        public void SyncStatusModelRejectsInvalidConditionalAndCanonicalValues()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerSyncStatusResponse(
                    false,
                    AdminPairingState.Unpaired,
                    null,
                    Guid.Empty,
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
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerSyncStatusResponse(
                    false,
                    AdminPairingState.Unpaired,
                    null,
                    null,
                    null,
                    Utc(1),
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
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerSyncStatusResponse(
                    false,
                    AdminPairingState.Unpaired,
                    null,
                    null,
                    null,
                    null,
                    "bad result",
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
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateSyncStatus(
                    AdminPairingState.PairingWindowOpen,
                    "HTTP://10.0.0.2:21000",
                    pairingExpiresUtc: Utc(5),
                    pairingRemainingSeconds: 60));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateSyncStatus(
                    AdminPairingState.PairingWindowOpen,
                    "http://[2001:0db8::1]:21000",
                    pairingExpiresUtc: Utc(5),
                    pairingRemainingSeconds: 60));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateSyncStatus(
                    AdminPairingState.Unpaired,
                    lastPeerNotificationOperation:
                        AdminPeerNotificationOperation.Revoke,
                    lastPeerNotificationResult:
                        AdminPeerNotificationResult.NotRequired,
                    lastPeerNotificationUtc: Utc(1)));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateSyncStatus(
                    AdminPairingState.Enabled,
                    "http://10.0.0.2:21000",
                    peerInstanceId: new Guid(
                        "9f2ed127-9834-42b4-a379-eaad9df8fcec"),
                    keyEpoch: 1,
                    lastSyncUtc: DateTime.SpecifyKind(
                        Utc(1),
                        DateTimeKind.Local),
                    lastResult: "OK"));
        }

        [TestMethod]
        public void DisableLoggingUnitAndErrorResponsesRoundTrip()
        {
            var disable = new AdminServerSyncDisableResponse(
                AdminPairingState.PairedDisabled,
                AdminPeerNotificationOperation.Release,
                AdminPeerNotificationResult.NotRequired,
                Utc(6));
            byte[] disableBody = AdminServerResponseXmlCodec
                .SerializeSyncDisableResponse(disable);
            AdminSyncDisableResult disableParsed = AdminXmlCodec
                .ParseSyncDisableResponse(
                    disableBody)
                .Payload;
            Assert.AreEqual(AdminPairingState.PairedDisabled,
                disableParsed.LocalPairingState);
            Assert.AreEqual(AdminPeerNotificationResult.NotRequired,
                disableParsed.PeerNotificationResult);
            AssertExtensionsAbsent(disableBody);

            var revoke = new AdminServerSyncDisableResponse(
                AdminPairingState.Unpaired,
                AdminPeerNotificationOperation.Revoke,
                AdminPeerNotificationResult.Confirmed,
                Utc(7));
            byte[] revokeBody = AdminServerResponseXmlCodec
                .SerializeSyncDisableResponse(revoke);
            AdminSyncDisableResult revokeParsed = AdminXmlCodec
                .ParseSyncDisableResponse(revokeBody)
                .Payload;
            Assert.AreEqual(AdminPairingState.Unpaired,
                revokeParsed.LocalPairingState);
            Assert.AreEqual(AdminPeerNotificationOperation.Revoke,
                revokeParsed.PeerNotificationOperation);
            AssertExtensionsAbsent(revokeBody);

            byte[] loggingBody = AdminServerResponseXmlCodec
                .SerializeLoggingResponse(
                    new AdminServerLoggingResponse(30));
            AdminLoggingSettings loggingParsed = AdminXmlCodec
                .ParseLoggingResponse(loggingBody)
                .Payload;
            Assert.AreEqual(30, loggingParsed.LogRetentionDays);
            AssertExtensionsAbsent(loggingBody);

            byte[] unitBody = AdminServerResponseXmlCodec
                .SerializeUnitResponse(AdminServerUnitResponse.Value);
            AdminResponse<AdminUnit> unitParsed = AdminXmlCodec
                .ParseUnitResponse(unitBody);
            Assert.IsTrue(unitParsed.IsSuccess);
            Assert.AreSame(AdminUnit.Value, unitParsed.Payload);
            AssertExtensionsAbsent(unitBody);

            var expectedMessages = new Dictionary<
                AdminServerErrorCode,
                string>
            {
                { AdminServerErrorCode.BadRequest,
                    "The request is invalid." },
                { AdminServerErrorCode.NotFound,
                    "The requested item was not found." },
                { AdminServerErrorCode.Conflict,
                    "The request conflicts with the current state." },
                { AdminServerErrorCode.LimitExceeded,
                    "The request limit was exceeded." },
                { AdminServerErrorCode.NotPeer,
                    "The caller is not the configured peer." },
                { AdminServerErrorCode.PeerMismatch,
                    "The peer configuration does not match." },
                { AdminServerErrorCode.ClockSkew,
                    "The peer clock is outside the allowed range." },
                { AdminServerErrorCode.SyncDisabled,
                    "Synchronization is disabled." },
                { AdminServerErrorCode.RevisionCollision,
                    "A synchronization revision conflict was detected." },
                { AdminServerErrorCode.DirectoryCapacity,
                    "The directory capacity was exceeded." },
                { AdminServerErrorCode.LogicalClockExhausted,
                    "The logical clock is exhausted." },
                { AdminServerErrorCode.Internal,
                    "The service directory could not process the request." }
            };

            foreach (AdminServerErrorCode code in
                Enum.GetValues(typeof(AdminServerErrorCode)))
            {
                var error = new AdminServerErrorResponse(code);
                byte[] body = AdminServerResponseXmlCodec
                    .SerializeErrorResponse(error);
                AdminResponse<AdminUnit> parsed =
                    AdminXmlCodec.ParseUnitResponse(body);
                Assert.IsFalse(parsed.IsSuccess);
                Assert.AreEqual((int)code, parsed.Code);
                Assert.AreEqual(expectedMessages[code], error.Message);
                Assert.AreEqual(expectedMessages[code], parsed.Message);
                Assert.IsNull(parsed.Payload);
                AssertExtensionsAbsent(body);
            }
        }

        [TestMethod]
        public void DisableResponseRejectsImpossibleNotificationOutcomes()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerSyncDisableResponse(
                    AdminPairingState.PairedDisabled,
                    AdminPeerNotificationOperation.Revoke,
                    AdminPeerNotificationResult.Confirmed,
                    Utc(1)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerSyncDisableResponse(
                    AdminPairingState.Unpaired,
                    AdminPeerNotificationOperation.Release,
                    AdminPeerNotificationResult.Confirmed,
                    Utc(1)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminServerSyncDisableResponse(
                    AdminPairingState.Unpaired,
                    AdminPeerNotificationOperation.Revoke,
                    AdminPeerNotificationResult.NotRequired,
                    Utc(1)));
        }

        [TestMethod]
        public void SerializerRejectsResponseAboveSixteenKiB()
        {
            string maximumName = string.Concat(
                Repeat("\U0001F600", 128));
            var items = new List<AdminServerServiceItem>();
            for (int index = 0; index < 40; index++)
            {
                items.Add(CreateServiceItem(
                    maximumName,
                    CreateProductCode(index),
                    "service.internal",
                    Utc(0),
                    false,
                    null));
            }

            var response = new AdminServerServicesResponse(
                items,
                items.Count,
                null);
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerResponseXmlCodec
                    .SerializeServicesResponse(response));
        }

        private static AdminServerServiceItem CreateServiceItem(
            string name,
            string productCode,
            string serverAddress,
            DateTime lastModifiedUtc,
            bool deleted,
            DateTime? deletedUtc)
        {
            return new AdminServerServiceItem(
                CreateDefinition(name, productCode, serverAddress),
                lastModifiedUtc,
                deleted,
                deletedUtc);
        }

        private static AdminServerServiceDefinition CreateDefinition(
            string name,
            string productCode,
            string serverAddress)
        {
            return new AdminServerServiceDefinition(
                name,
                productCode,
                serverAddress,
                21000);
        }

        private static AdminServerSyncStatusResponse CreateSyncStatus(
            AdminPairingState pairingState,
            string peerEndpoint = null,
            Guid? peerInstanceId = null,
            ulong? keyEpoch = null,
            Guid? pairingId = null,
            string sas = null,
            DateTime? pairingExpiresUtc = null,
            int? pairingRemainingSeconds = null,
            bool? localConfirmed = null,
            bool? remoteConfirmed = null,
            DateTime? commitExpiresUtc = null,
            bool? localCommitConfirmed = null,
            bool? remoteCommitConfirmed = null,
            DateTime? lastSyncUtc = null,
            string lastResult = "NOT_RUN",
            long? clockSkewSeconds = null,
            AdminPeerNotificationOperation lastPeerNotificationOperation =
                AdminPeerNotificationOperation.None,
            AdminPeerNotificationResult lastPeerNotificationResult =
                AdminPeerNotificationResult.NotRun,
            DateTime? lastPeerNotificationUtc = null)
        {
            return new AdminServerSyncStatusResponse(
                pairingState == AdminPairingState.Enabled,
                pairingState,
                peerEndpoint,
                peerInstanceId,
                keyEpoch,
                lastSyncUtc,
                lastResult,
                clockSkewSeconds,
                pairingId,
                sas,
                pairingExpiresUtc,
                pairingRemainingSeconds,
                localConfirmed,
                remoteConfirmed,
                commitExpiresUtc,
                localCommitConfirmed,
                remoteCommitConfirmed,
                lastPeerNotificationOperation,
                lastPeerNotificationResult,
                lastPeerNotificationUtc);
        }

        private static string CreateProductCode(int value)
        {
            return "A" + value.ToString(
                "D3",
                CultureInfo.InvariantCulture);
        }

        private static DateTime Utc(int minute)
        {
            return new DateTime(
                2026,
                7,
                18,
                0,
                minute,
                0,
                DateTimeKind.Utc);
        }

        private static void AssertElementOrder(
            string xml,
            params string[] names)
        {
            int previous = -1;
            foreach (string name in names)
            {
                int current = xml.IndexOf(
                    "<" + name + ">",
                    StringComparison.Ordinal);
                Assert.IsTrue(
                    current > previous,
                    "Element is missing or out of order: " + name);
                previous = current;
            }
        }

        private static string[] Repeat(string value, int count)
        {
            var values = new string[count];
            for (int index = 0; index < count; index++)
            {
                values[index] = value;
            }

            return values;
        }

        private static string Decode(byte[] body)
        {
            return StrictUtf8.GetString(body);
        }

        private static void AssertExtensionsAbsent(byte[] body)
        {
            Assert.IsFalse(Decode(body).Contains("<Extensions"));
        }
    }
}
