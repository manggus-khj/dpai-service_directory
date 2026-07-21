using System;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class CertificateIssuanceRequestEvidenceTests
    {
        private static readonly Guid RequestId = new Guid(
            "7f35b4b8-854d-4ca1-90bc-da196772f49f");
        private static readonly byte[] CsrDer =
            { 0x30, 0x03, 0x02, 0x01, 0x05 };

        [TestMethod]
        public void StablePayloadHashesMatchFixedRegistrationAndRenewalVectors()
        {
            ServiceDefinition definition = Definition("VMS Bridge");
            CertificateSerialNumber currentSerial = Serial(
                "01A4A5A6A7A8A9AAABACADAEAFB0B1B2");

            CertificateIssuanceRequestEvidence registration =
                CertificateIssuanceRequestEvidence.CreateRegistration(
                    RequestId,
                    definition,
                    CsrDer);
            CertificateIssuanceRequestEvidence renewal =
                CertificateIssuanceRequestEvidence.CreateRenewal(
                    RequestId,
                    currentSerial,
                    definition,
                    CsrDer);

            CollectionAssert.AreEqual(
                Hex("417C7763C4E320A6B747B3CB0C6D22F93741B29A32B48594B8EB4C144FE6D729"),
                registration.GetCsrSha256());
            CollectionAssert.AreEqual(
                Hex("1E6C1413E8C8627ACCF8D29B1D0EC9BE9E8C3321F825716F20C0873E7A0D687E"),
                registration.GetRequestPayloadSha256());
            CollectionAssert.AreEqual(
                Hex("092064340003EA51652881C10AD681610A87E9056CE91021E5240B58CCE5A014"),
                renewal.GetRequestPayloadSha256());
            CollectionAssert.AreEqual(
                Hex("01A4A5A6A7A8A9AAABACADAEAFB0B1B2"),
                currentSerial.ToByteArray());
        }

        [TestMethod]
        public void SnapshotOnlyReplaysSameKindCsrAndSemanticPayload()
        {
            ServiceDefinition definition = Definition("VMS Bridge");
            CertificateIssuanceRequestEvidence original =
                CertificateIssuanceRequestEvidence.CreateRegistration(
                    RequestId,
                    definition,
                    CsrDer);
            CertificateLedgerEntry stored = CreateEntry(original);
            var snapshot = new CertificateLedgerSnapshot(
                new[] { stored },
                1,
                1);

            CertificateLedgerEntry resolved;
            Assert.AreEqual(
                CertificateIssuanceReplayStatus.ExactReplay,
                snapshot.ResolveIssuanceRequest(
                    CertificateIssuanceRequestEvidence.CreateRegistration(
                        RequestId,
                        definition,
                        CsrDer),
                    out resolved));
            Assert.AreSame(stored, resolved);

            Assert.AreEqual(
                CertificateIssuanceReplayStatus.Conflict,
                snapshot.ResolveIssuanceRequest(
                    CertificateIssuanceRequestEvidence.CreateRegistration(
                        RequestId,
                        definition,
                        new byte[] { 0x30, 0x01, 0x00 }),
                    out resolved));
            Assert.AreEqual(
                CertificateIssuanceReplayStatus.Conflict,
                snapshot.ResolveIssuanceRequest(
                    CertificateIssuanceRequestEvidence.CreateRegistration(
                        RequestId,
                        Definition("Changed Name"),
                        CsrDer),
                    out resolved));
            Assert.AreEqual(
                CertificateIssuanceReplayStatus.Conflict,
                snapshot.ResolveIssuanceRequest(
                    CertificateIssuanceRequestEvidence.CreateRenewal(
                        RequestId,
                        Serial("01A4A5A6A7A8A9AAABACADAEAFB0B1B2"),
                        definition,
                        CsrDer),
                    out resolved));
            Assert.AreEqual(
                CertificateIssuanceReplayStatus.NewRequest,
                snapshot.ResolveIssuanceRequest(
                    CertificateIssuanceRequestEvidence.CreateRegistration(
                        Guid.NewGuid(),
                        definition,
                        CsrDer),
                    out resolved));
            Assert.IsNull(resolved);
        }

        private static CertificateLedgerEntry CreateEntry(
            CertificateIssuanceRequestEvidence evidence)
        {
            byte[] csrSha256 = evidence.GetCsrSha256();
            byte[] payloadSha256 = evidence.GetRequestPayloadSha256();
            try
            {
                return CertificateLedgerEntry.CreateIssued(
                    Serial("02A4A5A6A7A8A9AAABACADAEAFB0B1B2"),
                    evidence.ServiceDefinition,
                    evidence.RequestId,
                    evidence.IssuanceKind,
                    csrSha256,
                    payloadSha256,
                    Hash(0x33),
                    new byte[] { 0x30, 0x01, 0x00 },
                    TestData.Utc(1),
                    TestData.Utc(0),
                    TestData.Utc(3));
            }
            finally
            {
                Array.Clear(csrSha256, 0, csrSha256.Length);
                Array.Clear(payloadSha256, 0, payloadSha256.Length);
            }
        }

        private static ServiceDefinition Definition(string name)
        {
            return TestData.Definition(
                name,
                "AB12",
                "vms-bridge.example.local",
                "10.20.30.40",
                21000);
        }

        private static CertificateSerialNumber Serial(string value)
        {
            CertificateSerialNumber result;
            Assert.IsTrue(CertificateSerialNumber.TryCreate(value, out result));
            return result;
        }

        private static byte[] Hash(byte value)
        {
            var result = new byte[CertificateLedgerEntry.Sha256Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = value;
            }

            return result;
        }

        private static byte[] Hex(string value)
        {
            var result = new byte[value.Length / 2];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = Convert.ToByte(
                    value.Substring(index * 2, 2),
                    16);
            }

            return result;
        }
    }
}
