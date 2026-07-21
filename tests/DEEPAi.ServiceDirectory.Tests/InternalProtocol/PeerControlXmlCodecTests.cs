using System;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.InternalProtocol
{
    [TestClass]
    public sealed class PeerControlXmlCodecTests
    {
        private const string XmlNamespace =
            "urn:deepai:service-directory:peer";
        private const string PairingId =
            "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12";
        private const string InitiatorId =
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77";
        private const string ResponderId =
            "9f2ed127-9834-42b4-a379-eaad9df8fcec";
        private const string RevokeId =
            "94e02957-59bc-44f8-87db-e71ee91ebded";
        private const string PublicKeyBase64 =
            "RUNLMSAAAABloK4ciP/vr+gXtzQEXMzQMgBHfkLjiUjjQm6GAvx3g0ENy+mRPUqArh8G9gdeMcF61lPU8RT5kg4QRRR0JBqE";

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void PairingHelloRequestRoundTripsCanonicalWireAndCopiesBuffers()
        {
            byte[] nonce = CreateBytes(0x20, 32);
            byte[] publicKey = Convert.FromBase64String(PublicKeyBase64);
            var request = new PeerPairingHelloRequest(
                new Guid(PairingId),
                new Guid(InitiatorId),
                "https://10.0.0.1:21000",
                nonce,
                publicKey,
                0);

            nonce[0] = 0xff;
            publicKey[0] = 0xff;
            byte[] body = PeerSyncXmlCodec.SerializePairingHelloRequest(
                request);
            AssertNoBomOrDeclaration(body);

            PeerPairingHelloRequest parsed = PeerSyncXmlCodec
                .ParsePairingHelloRequest(body);
            Assert.AreEqual(PeerSyncContract.PairingAlgorithm,
                parsed.Algorithm);
            Assert.AreEqual(new Guid(PairingId), parsed.PairingId);
            Assert.AreEqual(new Guid(InitiatorId),
                parsed.InitiatorInstanceId);
            Assert.AreEqual("https://10.0.0.1:21000",
                parsed.InitiatorEndpoint);
            Assert.AreEqual((ulong)0,
                parsed.InitiatorLastPeerKeyEpoch);
            Assert.AreEqual((byte)0x20, parsed.CopyInitiatorNonce()[0]);
            Assert.AreEqual((byte)0x45,
                parsed.CopyInitiatorPublicKey()[0]);
        }

        [TestMethod]
        public void PairingHelloRejectsNonCanonicalAndUnknownWireValues()
        {
            string valid = Decode(
                PeerSyncXmlCodec.SerializePairingHelloRequest(
                    CreatePairingHelloRequest()));
            byte[] badKey = Convert.FromBase64String(PublicKeyBase64);
            badKey[0] ^= 0x01;
            string badKeyBase64 = Convert.ToBase64String(badKey);
            byte[] badPoint = new byte[72];
            byte[] publicKeyHeader =
            {
                0x45, 0x43, 0x4b, 0x31,
                0x20, 0x00, 0x00, 0x00
            };
            Buffer.BlockCopy(
                publicKeyHeader,
                0,
                badPoint,
                0,
                publicKeyHeader.Length);
            string badPointBase64 = Convert.ToBase64String(badPoint);
            string nonceBase64 = Convert.ToBase64String(
                CreateBytes(0x20, 32));
            string[] invalidDocuments =
            {
                valid.Replace(
                    PeerSyncContract.PairingAlgorithm,
                    "DPAI-SD-ECDH-P256-HMAC-SHA256-v2"),
                valid.Replace(PairingId, PairingId.ToUpperInvariant()),
                valid.Replace(
                    "https://10.0.0.1:21000",
                    "HTTPS://10.0.0.1:21000"),
                valid.Replace(
                    nonceBase64,
                    nonceBase64.Substring(0, 4) + " "
                        + nonceBase64.Substring(4)),
                valid.Replace(PublicKeyBase64, badKeyBase64),
                valid.Replace(PublicKeyBase64, badPointBase64),
                valid.Replace(
                    "<InitiatorLastPeerKeyEpoch>0",
                    "<InitiatorLastPeerKeyEpoch>00"),
                valid.Replace(
                    "<PairingHello ",
                    "<PairingHello Unexpected=\"true\" "),
                valid.Replace(
                    "</PairingHello>",
                    "<Unknown /></PairingHello>"),
                valid.Replace(XmlNamespace, "urn:wrong")
            };

            foreach (string invalidDocument in invalidDocuments)
            {
                AssertInvalid(
                    () => PeerSyncXmlCodec.ParsePairingHelloRequest(
                        Encode(invalidDocument)));
            }
        }

        [TestMethod]
        public void PairingKeyDecisionAndCommitRequestsRoundTrip()
        {
            byte[] hash = CreateBytes(0x00, 32);
            byte[] mac = CreateBytes(0x80, 32);
            var confirmation = new PeerPairingKeyConfirmation(
                new Guid(PairingId),
                ulong.MaxValue,
                PeerPairingRole.Initiator,
                new Guid(InitiatorId),
                new Guid(ResponderId),
                hash,
                mac);
            PeerPairingKeyConfirmation parsedConfirmation =
                PeerSyncXmlCodec.ParsePairingKeyConfirmRequest(
                    PeerSyncXmlCodec.SerializePairingKeyConfirmRequest(
                        confirmation));
            Assert.AreEqual(ulong.MaxValue, parsedConfirmation.KeyEpoch);
            Assert.AreEqual(PeerPairingRole.Initiator,
                parsedConfirmation.SenderRole);
            CollectionAssert.AreEqual(hash,
                parsedConfirmation.CopyTranscriptHash());
            CollectionAssert.AreEqual(mac,
                parsedConfirmation.CopyConfirmationMac());

            var decision = new PeerPairingDecision(
                new Guid(PairingId),
                1,
                PeerPairingRole.Responder,
                new Guid(ResponderId),
                new Guid(InitiatorId),
                hash,
                PeerPairingDecisionValue.Cancelled);
            PeerPairingDecision parsedDecision = PeerSyncXmlCodec
                .ParsePairingDecisionRequest(
                    PeerSyncXmlCodec.SerializePairingDecisionRequest(
                        decision));
            Assert.AreEqual(PeerPairingRole.Responder,
                parsedDecision.SenderRole);
            Assert.AreEqual(PeerPairingDecisionValue.Cancelled,
                parsedDecision.Decision);

            var commit = new PeerPairingCommit(
                new Guid(PairingId),
                1,
                PeerPairingRole.Initiator,
                new Guid(InitiatorId),
                new Guid(ResponderId),
                hash);
            PeerPairingCommit parsedCommit = PeerSyncXmlCodec
                .ParsePairingCommitRequest(
                    PeerSyncXmlCodec.SerializePairingCommitRequest(commit));
            Assert.AreEqual("COMMIT", parsedCommit.Commit);
            Assert.AreEqual(new Guid(ResponderId),
                parsedCommit.ReceiverInstanceId);
        }

        [TestMethod]
        public void PairingMessagesRejectBindingMarkerAndBase64Violations()
        {
            byte[] hash = CreateBytes(0x00, 32);
            byte[] mac = CreateBytes(0x80, 32);
            var confirmation = new PeerPairingKeyConfirmation(
                new Guid(PairingId),
                1,
                PeerPairingRole.Initiator,
                new Guid(InitiatorId),
                new Guid(ResponderId),
                hash,
                mac);
            string confirmationXml = Decode(
                PeerSyncXmlCodec.SerializePairingKeyConfirmRequest(
                    confirmation));
            string hashBase64 = Convert.ToBase64String(hash);
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingKeyConfirmRequest(
                    Encode(confirmationXml.Replace(
                        hashBase64,
                        hashBase64.Substring(0, 8) + "\r\n"
                            + hashBase64.Substring(8)))));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingKeyConfirmRequest(
                    Encode(confirmationXml.Replace(
                        ResponderId,
                        InitiatorId))));

            var commit = new PeerPairingCommit(
                new Guid(PairingId),
                1,
                PeerPairingRole.Initiator,
                new Guid(InitiatorId),
                new Guid(ResponderId),
                hash);
            string commitXml = Decode(
                PeerSyncXmlCodec.SerializePairingCommitRequest(commit));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingCommitRequest(
                    Encode(commitXml.Replace(
                        "<Commit>COMMIT</Commit>",
                        "<Commit>commit</Commit>"))));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingCommitRequest(
                    Encode(commitXml.Replace(
                        "<KeyEpoch>1</KeyEpoch>",
                        "<KeyEpoch>01</KeyEpoch>"))));
        }

        [TestMethod]
        public void HandshakeRequestAndResponseRoundTripExactTenMinuteSession()
        {
            DateTime utcNow = new DateTime(
                2026,
                7,
                18,
                2,
                0,
                1,
                123,
                DateTimeKind.Utc);
            byte[] requestNonce = CreateBytes(0x20, 32);
            var request = new PeerHandshakeRequest(
                new Guid(InitiatorId),
                new Guid(ResponderId),
                42,
                requestNonce,
                utcNow,
                true);
            PeerHandshakeRequest parsedRequest = PeerSyncXmlCodec
                .ParseAuthenticatedHandshakeRequest(
                    PeerSyncXmlCodec.SerializeHandshakeRequest(request));
            Assert.AreEqual((ulong)42, parsedRequest.KeyEpoch);
            Assert.AreEqual(utcNow, parsedRequest.UtcNow);
            Assert.IsTrue(parsedRequest.SyncEnabled);
            CollectionAssert.AreEqual(requestNonce,
                parsedRequest.CopyHandshakeNonce());

            byte[] responseNonce = CreateBytes(0x40, 32);
            byte[] sessionId = CreateBytes(0x60, 16);
            var handshakeResult = new PeerHandshakeResult(
                new Guid(ResponderId),
                42,
                responseNonce,
                sessionId,
                utcNow.AddMinutes(10),
                utcNow,
                true);
            PeerControlResponse parsedResponse = PeerSyncXmlCodec
                .ParseAuthenticatedHandshakeResponse(
                    PeerSyncXmlCodec.SerializeControlResponse(
                        PeerControlResponse.CreateHandshakeSuccess(
                            handshakeResult)));
            Assert.AreEqual(PeerControlResponseKind.Handshake,
                parsedResponse.Kind);
            Assert.AreEqual(utcNow.AddMinutes(10),
                parsedResponse.Handshake.ExpiresUtc);
            CollectionAssert.AreEqual(sessionId,
                parsedResponse.Handshake.CopySessionId());
        }

        [TestMethod]
        public void HandshakeRejectsNonCanonicalAndInvalidSessionSemantics()
        {
            DateTime utcNow = new DateTime(
                2026,
                7,
                18,
                2,
                0,
                0,
                DateTimeKind.Utc);
            var request = new PeerHandshakeRequest(
                new Guid(InitiatorId),
                new Guid(ResponderId),
                1,
                CreateBytes(0x20, 32),
                utcNow,
                true);
            string requestXml = Decode(
                PeerSyncXmlCodec.SerializeHandshakeRequest(request));
            string[] invalidRequests =
            {
                requestXml.Replace(InitiatorId,
                    InitiatorId.ToUpperInvariant()),
                requestXml.Replace(
                    "<KeyEpoch>1</KeyEpoch>",
                    "<KeyEpoch>01</KeyEpoch>"),
                requestXml.Replace(
                    "<SyncEnabled>true</SyncEnabled>",
                    "<SyncEnabled>1</SyncEnabled>"),
                requestXml.Replace(ResponderId, InitiatorId)
            };
            foreach (string invalidRequest in invalidRequests)
            {
                AssertInvalid(
                    () => PeerSyncXmlCodec
                        .ParseAuthenticatedHandshakeRequest(
                            Encode(invalidRequest)));
            }

            var result = new PeerHandshakeResult(
                new Guid(ResponderId),
                1,
                CreateBytes(0x40, 32),
                CreateBytes(0x60, 16),
                utcNow.AddMinutes(10),
                utcNow,
                true);
            string responseXml = Decode(
                PeerSyncXmlCodec.SerializeControlResponse(
                    PeerControlResponse.CreateHandshakeSuccess(result)));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParseControlResponse(
                    Encode(responseXml.Replace(
                        "<ExpiresUtc>2026-07-18T02:10:00Z</ExpiresUtc>",
                        "<ExpiresUtc>2026-07-18T02:09:59Z</ExpiresUtc>"))));
            string overflowingLifetime = responseXml
                .Replace(
                    "<ExpiresUtc>2026-07-18T02:10:00Z</ExpiresUtc>",
                    "<ExpiresUtc>9999-12-31T23:59:59Z</ExpiresUtc>")
                .Replace(
                    "<UtcNow>2026-07-18T02:00:00Z</UtcNow>",
                    "<UtcNow>9999-12-31T23:59:59Z</UtcNow>");
            AssertInvalid(
                () => PeerSyncXmlCodec.ParseControlResponse(
                    Encode(overflowingLifetime)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerHandshakeResult(
                    new Guid(ResponderId),
                    1,
                    CreateBytes(0x40, 32),
                    CreateBytes(0x60, 16),
                    utcNow.AddMinutes(9),
                    utcNow,
                    true));
        }

        [TestMethod]
        public void ReleaseAndRevokeRequestsRoundTripCanonicalBindings()
        {
            byte[] sessionId = CreateBytes(0x60, 16);
            var release = new PeerReleaseRequest(
                new Guid(InitiatorId),
                sessionId);
            PeerReleaseRequest parsedRelease = PeerSyncXmlCodec
                .ParseAuthenticatedReleaseRequest(
                    PeerSyncXmlCodec.SerializeReleaseRequest(release));
            Assert.AreEqual(new Guid(InitiatorId),
                parsedRelease.InstanceId);
            CollectionAssert.AreEqual(sessionId,
                parsedRelease.CopySessionId());

            var revoke = new PeerRevokeRequest(
                new Guid(InitiatorId),
                new Guid(ResponderId),
                ulong.MaxValue,
                new Guid(RevokeId));
            PeerRevokeRequest parsedRevoke = PeerSyncXmlCodec
                .ParseAuthenticatedRevokeRequest(
                    PeerSyncXmlCodec.SerializeRevokeRequest(revoke));
            Assert.AreEqual(ulong.MaxValue, parsedRevoke.KeyEpoch);
            Assert.AreEqual(new Guid(RevokeId), parsedRevoke.RevokeId);

            string revokeXml = Decode(
                PeerSyncXmlCodec.SerializeRevokeRequest(revoke));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParseAuthenticatedRevokeRequest(
                    Encode(revokeXml.Replace(ResponderId, InitiatorId))));
        }

        [TestMethod]
        public void PairingSuccessResponsesRoundTripPurposeSpecificPayloads()
        {
            var helloResult = new PeerPairingHelloResult(
                new Guid(PairingId),
                new Guid(ResponderId),
                "https://10.0.0.2:21000",
                CreateBytes(0x40, 32),
                Convert.FromBase64String(PublicKeyBase64),
                7,
                8);
            PeerControlResponse helloResponse = PeerSyncXmlCodec
                .ParsePairingHelloResponse(
                    PeerSyncXmlCodec.SerializeControlResponse(
                        PeerControlResponse.CreatePairingHelloSuccess(
                            helloResult)));
            Assert.AreEqual(PeerControlResponseKind.PairingHello,
                helloResponse.Kind);
            Assert.AreEqual((ulong)8,
                helloResponse.PairingHello.KeyEpoch);
            Assert.AreEqual("https://10.0.0.2:21000",
                helloResponse.PairingHello.ResponderEndpoint);

            var confirmation = new PeerPairingKeyConfirmation(
                new Guid(PairingId),
                8,
                PeerPairingRole.Responder,
                new Guid(ResponderId),
                new Guid(InitiatorId),
                CreateBytes(0x00, 32),
                CreateBytes(0x80, 32));
            PeerControlResponse confirmationResponse = PeerSyncXmlCodec
                .ParsePairingKeyConfirmResponse(
                    PeerSyncXmlCodec.SerializeControlResponse(
                        PeerControlResponse
                            .CreatePairingKeyConfirmSuccess(
                                confirmation)));
            Assert.AreEqual(
                PeerControlResponseKind.PairingKeyConfirmation,
                confirmationResponse.Kind);
            Assert.AreEqual(PeerPairingRole.Responder,
                confirmationResponse.PairingKeyConfirmation.SenderRole);
        }

        [TestMethod]
        public void ControlResponsesEnforceResultCodePayloadAndNoReflection()
        {
            PeerControlResponse unit = PeerSyncXmlCodec
                .ParsePairingDecisionResponse(
                PeerSyncXmlCodec.SerializeControlResponse(
                    PeerControlResponse.CreateUnitSuccess()));
            Assert.AreEqual(PeerControlResponseKind.UnitSuccess, unit.Kind);

            string remoteMessage = "remote detail must not be reflected";
            string errorXml = "<Response xmlns=\""
                + XmlNamespace
                + "\"><Result>ERROR</Result>"
                + "<Code>2004</Code><Message>"
                + remoteMessage
                + "</Message></Response>";
            PeerControlResponse remote = PeerSyncXmlCodec
                .ParseAuthenticatedReleaseResponse(Encode(errorXml));
            Assert.AreEqual(PeerSyncResponseCode.SyncDisabled,
                remote.Code);
            Assert.AreEqual(remoteMessage, remote.Message);

            string sanitized = Decode(
                PeerSyncXmlCodec.SerializeControlResponse(remote));
            Assert.IsFalse(sanitized.Contains(remoteMessage));
            Assert.AreEqual(string.Empty,
                PeerSyncXmlCodec
                    .ParseAuthenticatedReleaseResponse(Encode(sanitized))
                    .Message);

            string xsiTypedResponse = "<Response xmlns=\""
                + XmlNamespace
                + "\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\""
                + " xmlns:peer=\""
                + XmlNamespace
                + "\" xsi:type=\"peer:ResponseType\"><Result>OK</Result>"
                + "<Code>0</Code><Message /></Response>";
            AssertInvalid(
                () => PeerSyncXmlCodec.ParseControlResponse(
                    Encode(xsiTypedResponse)));

            DateTime utcNow = new DateTime(
                2026,
                7,
                18,
                2,
                0,
                0,
                DateTimeKind.Utc);
            byte[] wrongEndpointPayload = PeerSyncXmlCodec
                .SerializeControlResponse(
                    PeerControlResponse.CreateHandshakeSuccess(
                        new PeerHandshakeResult(
                            new Guid(ResponderId),
                            1,
                            CreateBytes(0x40, 32),
                            CreateBytes(0x60, 16),
                            utcNow.AddMinutes(10),
                            utcNow,
                            true)));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingHelloResponse(
                    wrongEndpointPayload));

            string[] invalidResponses =
            {
                errorXml.Replace(
                    "</Response>",
                    "<Extensions><Future /></Extensions></Response>"),
                errorXml.Replace("<Code>2004</Code>", "<Code>0</Code>"),
                errorXml.Replace("<Result>ERROR</Result>",
                    "<Result>OK</Result>"),
                "<Response xmlns=\"" + XmlNamespace
                    + "\"><Result>ERROR</Result><Code>2004</Code>"
                    + "<Message /><Handshake><InstanceId>"
                    + ResponderId
                    + "</InstanceId><KeyEpoch>1</KeyEpoch>"
                    + "<HandshakeNonce>"
                    + Convert.ToBase64String(CreateBytes(0x40, 32))
                    + "</HandshakeNonce><SessionId>"
                    + Convert.ToBase64String(CreateBytes(0x60, 16))
                    + "</SessionId><ExpiresUtc>2026-07-18T02:10:00Z"
                    + "</ExpiresUtc><UtcNow>2026-07-18T02:00:00Z"
                    + "</UtcNow><SyncEnabled>true</SyncEnabled>"
                    + "</Handshake></Response>"
            };
            foreach (string invalidResponse in invalidResponses)
            {
                AssertInvalid(
                    () => PeerSyncXmlCodec.ParseControlResponse(
                        Encode(invalidResponse)));
            }
        }

        [TestMethod]
        public void ControlBoundaryRejectsInvalidUtf8DtdDepthAndOversize()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => PeerSyncXmlCodec.ParsePairingHelloRequest(null));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingHelloRequest(
                    new byte[0]));
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingHelloRequest(
                    new byte[] { 0xc3, 0x28 }));

            PeerSyncProtocolException tooLarge =
                Assert.ThrowsExactly<PeerSyncProtocolException>(
                    () => PeerSyncXmlCodec.ParsePairingHelloRequest(
                        new byte[
                            PeerSyncContract.MaximumControlBodyBytes + 1]));
            Assert.AreEqual(PeerSyncProtocolFailure.BodyTooLarge,
                tooLarge.Failure);

            string valid = Decode(
                PeerSyncXmlCodec.SerializePairingHelloRequest(
                    CreatePairingHelloRequest()));
            int padding = PeerSyncContract.MaximumControlBodyBytes
                - Encode(valid).Length;
            string exact = valid + new string(' ', padding);
            Assert.AreEqual(PeerSyncContract.MaximumControlBodyBytes,
                Encode(exact).Length);
            Assert.AreEqual(new Guid(PairingId),
                PeerSyncXmlCodec.ParsePairingHelloRequest(Encode(exact))
                    .PairingId);
            PeerSyncProtocolException oneMore =
                Assert.ThrowsExactly<PeerSyncProtocolException>(
                    () => PeerSyncXmlCodec.ParsePairingHelloRequest(
                        Encode(exact + " ")));
            Assert.AreEqual(PeerSyncProtocolFailure.BodyTooLarge,
                oneMore.Failure);

            string dtd = "<!DOCTYPE PairingHello [<!ENTITY xxe SYSTEM "
                + "\"file:///C:/Windows/win.ini\">]>"
                + valid;
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingHelloRequest(
                    Encode(dtd)));

            var deep = new StringBuilder();
            deep.Append("<PairingHello xmlns=\"");
            deep.Append(XmlNamespace);
            deep.Append("\">");
            for (int index = 0; index < 16; index++)
            {
                deep.Append("<Nested>");
            }

            for (int index = 0; index < 16; index++)
            {
                deep.Append("</Nested>");
            }

            deep.Append("</PairingHello>");
            AssertInvalid(
                () => PeerSyncXmlCodec.ParsePairingHelloRequest(
                    Encode(deep.ToString())));
        }

        private static PeerPairingHelloRequest CreatePairingHelloRequest()
        {
            return new PeerPairingHelloRequest(
                new Guid(PairingId),
                new Guid(InitiatorId),
                "https://10.0.0.1:21000",
                CreateBytes(0x20, 32),
                Convert.FromBase64String(PublicKeyBase64),
                0);
        }

        private static byte[] CreateBytes(byte first, int count)
        {
            var value = new byte[count];
            for (int index = 0; index < value.Length; index++)
            {
                value[index] = unchecked((byte)(first + index));
            }

            return value;
        }

        private static void AssertInvalid(Action action)
        {
            PeerSyncProtocolException exception =
                Assert.ThrowsExactly<PeerSyncProtocolException>(action);
            Assert.AreEqual(PeerSyncProtocolFailure.InvalidRequest,
                exception.Failure);
        }

        private static void AssertNoBomOrDeclaration(byte[] body)
        {
            Assert.IsTrue(body.Length > 0);
            Assert.IsFalse(
                body.Length >= 3
                && body[0] == 0xef
                && body[1] == 0xbb
                && body[2] == 0xbf);
            Assert.IsFalse(
                Decode(body).StartsWith("<?xml", StringComparison.Ordinal));
        }

        private static byte[] Encode(string value)
        {
            return StrictUtf8.GetBytes(value);
        }

        private static string Decode(byte[] value)
        {
            return StrictUtf8.GetString(value);
        }
    }
}
