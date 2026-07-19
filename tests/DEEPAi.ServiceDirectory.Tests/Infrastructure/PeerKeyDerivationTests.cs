using System;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerKeyDerivationTests
    {
        private static readonly Guid FirstInstanceId =
            new Guid("11111111-1111-1111-1111-111111111111");

        private static readonly Guid SecondInstanceId =
            new Guid("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        [TestMethod]
        public void PairBoundPurposeKeysMatchFixedVectors()
        {
            byte[] pairRoot = SequentialBytes(0, 32);

            AssertVector(
                PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    42UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    PeerPairBoundKeyPurpose.HandshakeRequest),
                "9eMpt5w0txG4uK1XusGsHSVIRt36ow/YfKGJ8mOAN30=");
            AssertVector(
                PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    42UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    PeerPairBoundKeyPurpose.HandshakeResponse),
                "wxz21DWJInmWxxj9aFHY4Kgi4qsCD1AP/fZuyxHT4YI=");
            AssertVector(
                PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    42UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    PeerPairBoundKeyPurpose.RevokeRequest),
                "NFgDZiO3xoNAPDE+kINAAy/sVL+EBQaCXLUHvhoOwXw=");
            AssertVector(
                PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    42UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    PeerPairBoundKeyPurpose.RevokeResponse),
                "4oqaxg5AmxEA8NMqE6v1rIL7uHQVmKulNhICCQtWSbI=");
        }

        [TestMethod]
        public void SessionPurposeKeysMatchFixedVectors()
        {
            byte[] pairRoot = SequentialBytes(0, 32);
            byte[] requestNonce = SequentialBytes(32, 32);
            byte[] responseNonce = SequentialBytes(64, 32);
            byte[] sessionId = SequentialBytes(96, 16);

            AssertVector(
                PeerKeyDerivation.DeriveSessionKey(
                    pairRoot,
                    42UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    requestNonce,
                    responseNonce,
                    sessionId,
                    PeerSessionKeyPurpose.Request),
                "as76V2zzG1kia6KNw5T9w5Oddvdd7A66DTD/k8pYWvs=");
            AssertVector(
                PeerKeyDerivation.DeriveSessionKey(
                    pairRoot,
                    42UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    requestNonce,
                    responseNonce,
                    sessionId,
                    PeerSessionKeyPurpose.Response),
                "11/2F9NgF7qMZIpo8hNiK/jULF+FCKTh1XzOF6QB+Lc=");
        }

        [TestMethod]
        public void InstanceOrderingIsCanonicalAcrossBothPeers()
        {
            byte[] pairRoot = SequentialBytes(0, 32);
            byte[] first = PeerKeyDerivation.DerivePairBoundKey(
                pairRoot,
                42UL,
                FirstInstanceId,
                SecondInstanceId,
                PeerPairBoundKeyPurpose.HandshakeRequest);
            byte[] reversed = PeerKeyDerivation.DerivePairBoundKey(
                pairRoot,
                42UL,
                SecondInstanceId,
                FirstInstanceId,
                PeerPairBoundKeyPurpose.HandshakeRequest);

            CollectionAssert.AreEqual(first, reversed);
        }

        [TestMethod]
        public void ContextChangesProduceDifferentKeysWithoutMutatingInputs()
        {
            byte[] pairRoot = SequentialBytes(0, 32);
            byte[] originalRoot = (byte[])pairRoot.Clone();
            byte[] requestNonce = SequentialBytes(32, 32);
            byte[] originalRequestNonce = (byte[])requestNonce.Clone();
            byte[] responseNonce = SequentialBytes(64, 32);
            byte[] sessionId = SequentialBytes(96, 16);

            byte[] first = PeerKeyDerivation.DeriveSessionKey(
                pairRoot,
                42UL,
                FirstInstanceId,
                SecondInstanceId,
                requestNonce,
                responseNonce,
                sessionId,
                PeerSessionKeyPurpose.Request);
            sessionId[0] ^= 0x01;
            byte[] changed = PeerKeyDerivation.DeriveSessionKey(
                pairRoot,
                42UL,
                FirstInstanceId,
                SecondInstanceId,
                requestNonce,
                responseNonce,
                sessionId,
                PeerSessionKeyPurpose.Request);

            CollectionAssert.AreNotEqual(first, changed);
            CollectionAssert.AreEqual(originalRoot, pairRoot);
            CollectionAssert.AreEqual(
                originalRequestNonce,
                requestNonce);
        }

        [TestMethod]
        public void InvalidBindingAndLengthsFailClosed()
        {
            byte[] pairRoot = SequentialBytes(0, 32);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    0UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    PeerPairBoundKeyPurpose.HandshakeRequest));
            Assert.ThrowsExactly<ArgumentException>(
                () => PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    1UL,
                    FirstInstanceId,
                    FirstInstanceId,
                    PeerPairBoundKeyPurpose.HandshakeRequest));
            Assert.ThrowsExactly<ArgumentException>(
                () => PeerKeyDerivation.DeriveSessionKey(
                    pairRoot,
                    1UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    new byte[31],
                    new byte[32],
                    new byte[16],
                    PeerSessionKeyPurpose.Request));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    1UL,
                    FirstInstanceId,
                    SecondInstanceId,
                    (PeerPairBoundKeyPurpose)99));
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

        private static void AssertVector(byte[] actual, string expectedBase64)
        {
            CollectionAssert.AreEqual(
                Convert.FromBase64String(expectedBase64),
                actual);
        }
    }
}
