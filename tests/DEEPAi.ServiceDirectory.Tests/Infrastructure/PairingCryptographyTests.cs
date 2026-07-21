using System;
using System.Collections.Generic;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PairingCryptographyTests
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly Guid PairingId = new Guid(
            "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12");
        private static readonly Guid InitiatorId = new Guid(
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77");
        private static readonly Guid ResponderId = new Guid(
            "9f2ed127-9834-42b4-a379-eaad9df8fcec");
        private const string InitiatorPublicKey =
            "RUNLMSAAAABloK4ciP/vr+gXtzQEXMzQMgBHfkLjiUjjQm6GAvx3g0ENy+mRPUqArh8G9gdeMcF61lPU8RT5kg4QRRR0JBqE";
        private const string ResponderPublicKey =
            "RUNLMSAAAABvkt9l4qYtS/z18CZhZJe8+hghxECZTXSFzkgMBqt3JKykJ2tOI2NyVkkWpmxKD5gRp25CcZF3huKwSbf35Gda";

        [TestMethod]
        public void TranscriptMatchesIndependentFixedVectorAndCopiesInputs()
        {
            byte[] initiatorNonce = SequentialBytes(0, 32);
            byte[] responderNonce = SequentialBytes(32, 32);
            byte[] initiatorKey = Convert.FromBase64String(
                InitiatorPublicKey);
            byte[] responderKey = Convert.FromBase64String(
                ResponderPublicKey);

            byte[] hash = PairingTranscript.CreateHash(
                PairingId,
                InitiatorId,
                ResponderId,
                "https://10.0.0.1:21000",
                "https://10.0.0.2:21000",
                initiatorNonce,
                responderNonce,
                initiatorKey,
                responderKey,
                7,
                41,
                42);

            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "oKMjYfp2Y5rwo/qAupzLoUDVWoulB0uJfmTtLZDF818="),
                hash);
            CollectionAssert.AreEqual(
                SequentialBytes(0, 32),
                initiatorNonce);
            CollectionAssert.AreEqual(
                SequentialBytes(32, 32),
                responderNonce);
            CollectionAssert.AreEqual(
                Convert.FromBase64String(InitiatorPublicKey),
                initiatorKey);
            CollectionAssert.AreEqual(
                Convert.FromBase64String(ResponderPublicKey),
                responderKey);
        }

        [TestMethod]
        public void TranscriptRejectsNonCanonicalAndInvalidBindingValues()
        {
            byte[] initiatorKey = Convert.FromBase64String(
                InitiatorPublicKey);
            byte[] responderKey = Convert.FromBase64String(
                ResponderPublicKey);

            Assert.ThrowsExactly<ArgumentException>(
                () => CreateTranscript(
                    InitiatorId,
                    InitiatorId,
                    "https://10.0.0.1:21000",
                    7,
                    41,
                    42,
                    initiatorKey,
                    responderKey));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateTranscript(
                    InitiatorId,
                    ResponderId,
                    "HTTP://10.0.0.1:21000",
                    7,
                    41,
                    42,
                    initiatorKey,
                    responderKey));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateTranscript(
                    InitiatorId,
                    ResponderId,
                    "https://10.0.0.1:21000",
                    7,
                    41,
                    41,
                    initiatorKey,
                    responderKey));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateTranscript(
                    InitiatorId,
                    ResponderId,
                    "https://10.0.0.1:21000",
                    ulong.MaxValue,
                    41,
                    ulong.MaxValue,
                    initiatorKey,
                    responderKey));

            byte[] invalidKey = (byte[])initiatorKey.Clone();
            invalidKey[0] ^= 0x01;
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateTranscript(
                    InitiatorId,
                    ResponderId,
                    "https://10.0.0.1:21000",
                    7,
                    41,
                    42,
                    invalidKey,
                    responderKey));
        }

        [TestMethod]
        public void ExistingPurposePrimitivesMatchIndependentFixedVectors()
        {
            byte[] k0 = SequentialBytes(0, 32);
            byte[] transcript = SequentialBytes(32, 32);

            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "j37R9xjn3BaxkelhkSU7NI+o9y/u52TV0tpvOv0TI6U="),
                PairingCryptography.CreateConfirmationMac(
                    k0,
                    transcript,
                    PairingConfirmationDirection.Initiator));
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "zJ8M7Yn7EJ3KNpPCt/0opC2LsefYTLASYzj/tFdW7to="),
                PairingCryptography.CreateConfirmationMac(
                    k0,
                    transcript,
                    PairingConfirmationDirection.Responder));
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "s4mT0LCXyd23T3jiwUHIqXNjeVvZVMGuDlcec5jleO8="),
                PairingCryptography.DerivePairRoot(k0, transcript));
            CollectionAssert.AreEqual(
                "17267304".ToCharArray(),
                PairingCryptography.CreateSas(k0, transcript));

            CollectionAssert.AreEqual(SequentialBytes(0, 32), k0);
            CollectionAssert.AreEqual(
                SequentialBytes(32, 32),
                transcript);
        }

        [TestMethod]
        public void SecretContextOwnsCopiesAndClosesEphemeralKeyAccess()
        {
            byte[] k0 = SequentialBytes(0, 32);
            byte[] transcript = SequentialBytes(32, 32);
            var context = new PairingSecretContext(k0, transcript);
            Array.Clear(k0, 0, k0.Length);
            Array.Clear(transcript, 0, transcript.Length);

            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "j37R9xjn3BaxkelhkSU7NI+o9y/u52TV0tpvOv0TI6U="),
                context.CreateConfirmationMac(
                    PairingConfirmationDirection.Initiator));
            CollectionAssert.AreEqual(
                "17267304".ToCharArray(),
                context.CreateSas());
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "s4mT0LCXyd23T3jiwUHIqXNjeVvZVMGuDlcec5jleO8="),
                context.DerivePairRoot());

            context.Dispose();
            context.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => context.CreateConfirmationMac(
                    PairingConfirmationDirection.Responder));
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => context.CreateSas());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => context.DerivePairRoot());
        }

        [TestMethod]
        public void DecisionRequestAndResponseMatchIndependentFixedVectors()
        {
            byte[] k0 = SequentialBytes(0, 32);
            byte[] transcript = SequentialBytes(64, 32);
            byte[] requestMac = PairingTerminalMessageAuthenticator
                .CreateDecisionRequestMac(
                    k0,
                    transcript,
                    PairingId,
                    42,
                    PairingConfirmationDirection.Initiator,
                    InitiatorId,
                    ResponderId,
                    PairingTerminalDecision.Confirmed);
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "S3SxJqi/TasWlS/E3Amqh46VcNhKJiTmG/mbuNMxGqE="),
                requestMac);

            byte[] body = StrictUtf8.GetBytes("<Response />");
            byte[] responseMac = PairingTerminalMessageAuthenticator
                .CreateDecisionResponseMac(
                    k0,
                    transcript,
                    PairingId,
                    42,
                    PairingConfirmationDirection.Responder,
                    ResponderId,
                    InitiatorId,
                    requestMac,
                    200,
                    "OK",
                    0,
                    body);
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "2z61s6UWAe4pj1onMeF/XUGJEUtJnXZHn81hNeaVGS8="),
                responseMac);
            Assert.IsTrue(
                PairingTerminalMessageAuthenticator.VerifyMac(
                    responseMac,
                    (byte[])responseMac.Clone()));

            CollectionAssert.AreEqual(SequentialBytes(0, 32), k0);
            CollectionAssert.AreEqual(
                SequentialBytes(64, 32),
                transcript);
            CollectionAssert.AreEqual(
                StrictUtf8.GetBytes("<Response />"),
                body);
        }

        [TestMethod]
        public void CommitRequestAndResponseMatchIndependentFixedVectors()
        {
            byte[] pairRoot = SequentialBytes(32, 32);
            byte[] transcript = SequentialBytes(64, 32);
            byte[] requestMac = PairingTerminalMessageAuthenticator
                .CreateCommitRequestMac(
                    pairRoot,
                    transcript,
                    PairingId,
                    42,
                    PairingConfirmationDirection.Responder,
                    ResponderId,
                    InitiatorId);
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "6mNNyEIPS/U7ZneF7bBkP30hcNbB04tB/Z+6SfhPLnk="),
                requestMac);

            byte[] body = StrictUtf8.GetBytes(
                "<Response>Error</Response>");
            byte[] responseMac = PairingTerminalMessageAuthenticator
                .CreateCommitResponseMac(
                    pairRoot,
                    transcript,
                    PairingId,
                    42,
                    PairingConfirmationDirection.Initiator,
                    InitiatorId,
                    ResponderId,
                    requestMac,
                    409,
                    "ERROR",
                    1002,
                    body);
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "hK7sS97wrqvtSw/pijdkoJoPjo4U4FBfChovl7mhxUM="),
                responseMac);

            byte[] changedBody = StrictUtf8.GetBytes(
                "<Response>error</Response>");
            byte[] changedMac = PairingTerminalMessageAuthenticator
                .CreateCommitResponseMac(
                    pairRoot,
                    transcript,
                    PairingId,
                    42,
                    PairingConfirmationDirection.Initiator,
                    InitiatorId,
                    ResponderId,
                    requestMac,
                    409,
                    "ERROR",
                    1002,
                    changedBody);
            Assert.IsFalse(
                PairingTerminalMessageAuthenticator.VerifyMac(
                    responseMac,
                    changedMac));
        }

        [TestMethod]
        [DataRow(200, 0, "OK")]
        [DataRow(400, 1000, "ERROR")]
        [DataRow(404, 1001, "ERROR")]
        [DataRow(409, 1002, "ERROR")]
        [DataRow(413, 1004, "ERROR")]
        [DataRow(429, 1004, "ERROR")]
        [DataRow(403, 2001, "ERROR")]
        [DataRow(409, 2002, "ERROR")]
        [DataRow(401, 2003, "ERROR")]
        [DataRow(409, 2004, "ERROR")]
        [DataRow(409, 2005, "ERROR")]
        [DataRow(409, 2006, "ERROR")]
        [DataRow(409, 2007, "ERROR")]
        [DataRow(500, 3000, "ERROR")]
        public void TerminalResponseMacAcceptsEveryDocumentedStatusMapping(
            int httpStatus,
            int code,
            string result)
        {
            byte[] mac = PairingTerminalMessageAuthenticator
                .CreateCommitResponseMac(
                    SequentialBytes(0, 32),
                    SequentialBytes(64, 32),
                    PairingId,
                    42,
                    PairingConfirmationDirection.Responder,
                    ResponderId,
                    InitiatorId,
                    new byte[32],
                    httpStatus,
                    result,
                    checked((uint)code),
                    new byte[0]);

            Assert.AreEqual(32, mac.Length);
        }

        [TestMethod]
        public void TerminalMacValidationFailsClosedOnInvalidBindings()
        {
            byte[] secret = SequentialBytes(0, 32);
            byte[] transcript = SequentialBytes(64, 32);

            Assert.ThrowsExactly<ArgumentException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateDecisionRequestMac(
                        new byte[31],
                        transcript,
                        PairingId,
                        42,
                        PairingConfirmationDirection.Initiator,
                        InitiatorId,
                        ResponderId,
                        PairingTerminalDecision.Confirmed));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateCommitRequestMac(
                        secret,
                        transcript,
                        PairingId,
                        0,
                        PairingConfirmationDirection.Initiator,
                        InitiatorId,
                        ResponderId));
            Assert.ThrowsExactly<ArgumentException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateCommitRequestMac(
                        secret,
                        transcript,
                        PairingId,
                        42,
                        PairingConfirmationDirection.Initiator,
                        InitiatorId,
                        InitiatorId));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateDecisionRequestMac(
                        secret,
                        transcript,
                        PairingId,
                        42,
                        PairingConfirmationDirection.Initiator,
                        InitiatorId,
                        ResponderId,
                        (PairingTerminalDecision)0));
            Assert.ThrowsExactly<ArgumentException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateDecisionResponseMac(
                        secret,
                        transcript,
                        PairingId,
                        42,
                        PairingConfirmationDirection.Responder,
                        ResponderId,
                        InitiatorId,
                        new byte[32],
                        200,
                        "OK",
                        1002,
                        new byte[0]));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateDecisionResponseMac(
                        secret,
                        transcript,
                        PairingId,
                        42,
                        PairingConfirmationDirection.Responder,
                        ResponderId,
                        InitiatorId,
                        new byte[32],
                        409,
                        "ERROR",
                        9999,
                        new byte[0]));
            Assert.ThrowsExactly<ArgumentException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateDecisionResponseMac(
                        secret,
                        transcript,
                        PairingId,
                        42,
                        PairingConfirmationDirection.Responder,
                        ResponderId,
                        InitiatorId,
                        new byte[32],
                        201,
                        "OK",
                        0,
                        new byte[0]));
            Assert.ThrowsExactly<ArgumentException>(
                () => PairingTerminalMessageAuthenticator
                    .CreateDecisionResponseMac(
                        secret,
                        transcript,
                        PairingId,
                        42,
                        PairingConfirmationDirection.Responder,
                        ResponderId,
                        InitiatorId,
                        new byte[32],
                        400,
                        "ERROR",
                        1002,
                        new byte[0]));
            Assert.IsFalse(
                PairingTerminalMessageAuthenticator.VerifyMac(
                    new byte[32],
                    new byte[31]));
        }

        [TestMethod]
        public void PairingMacHeaderRequiresExactlyOneCanonicalValue()
        {
            byte[] expected = SequentialBytes(0, 32);
            string canonical = PairingMacHeaderCodec.Format(expected);
            Assert.AreEqual(44, canonical.Length);

            byte[] parsed;
            Assert.IsTrue(
                PairingMacHeaderCodec.TryParseExactlyOne(
                    new[] { canonical },
                    out parsed));
            CollectionAssert.AreEqual(expected, parsed);
            Assert.IsFalse(
                PairingMacHeaderCodec.TryParseExactlyOne(
                    null,
                    out parsed));
            Assert.IsFalse(
                PairingMacHeaderCodec.TryParseExactlyOne(
                    new string[0],
                    out parsed));
            Assert.IsFalse(
                PairingMacHeaderCodec.TryParseExactlyOne(
                    new[] { canonical, canonical },
                    out parsed));
            Assert.IsFalse(
                PairingMacHeaderCodec.TryParseExactlyOne(
                    new[] { " " + canonical },
                    out parsed));

            var splitValue = new List<string>
            {
                canonical.Substring(0, 22),
                canonical.Substring(22)
            };
            Assert.IsFalse(
                PairingMacHeaderCodec.TryParseExactlyOne(
                    splitValue,
                    out parsed));
            Assert.ThrowsExactly<ArgumentException>(
                () => PairingMacHeaderCodec.Format(new byte[31]));
        }

        private static byte[] SequentialBytes(int start, int count)
        {
            var value = new byte[count];
            for (int index = 0; index < value.Length; index++)
            {
                value[index] = checked((byte)(start + index));
            }

            return value;
        }

        private static byte[] CreateTranscript(
            Guid initiatorId,
            Guid responderId,
            string initiatorEndpoint,
            ulong initiatorLastEpoch,
            ulong responderLastEpoch,
            ulong keyEpoch,
            byte[] initiatorKey,
            byte[] responderKey)
        {
            return PairingTranscript.CreateHash(
                PairingId,
                initiatorId,
                responderId,
                initiatorEndpoint,
                "https://[2001:db8::2]:21000",
                SequentialBytes(0, 32),
                SequentialBytes(32, 32),
                initiatorKey,
                responderKey,
                initiatorLastEpoch,
                responderLastEpoch,
                keyEpoch);
        }
    }
}
