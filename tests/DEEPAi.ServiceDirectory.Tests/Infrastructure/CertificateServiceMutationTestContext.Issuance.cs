using System;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    internal sealed partial class CertificateServiceMutationTestContext
    {
        internal CertificateServiceMutationCandidate PrepareRegistration()
        {
            DirectorySnapshot expected = DirectoryState.CurrentSnapshot;
            ServiceDefinition definition = TestData.Definition(
                serviceHostName: "vms-bridge.example.local",
                serviceIpv4Address: "10.20.30.40");
            DateTime issuedUtc = TestData.Utc(5);
            PkiTestSigningRequest request =
                PkiTestData.CreateRsaSigningRequest(
                    definition.ServiceEndpointIdentity);
            CertificateIssuanceRequestEvidence evidence =
                CertificateIssuanceRequestEvidence.CreateRegistration(
                    Guid.NewGuid(),
                    definition,
                    request.DerBytes);

            using (CertificateAuthorityStoreSnapshot current =
                _store.GetCurrent())
            {
                CertificateLedgerEntry issued = IssueEntry(
                    current,
                    definition,
                    evidence,
                    request.DerBytes,
                    issuedUtc,
                    out CertificateSerialNumber serialNumber);
                var nextLedger = new CertificateLedgerSnapshot(
                    new[] { issued },
                    current.State.PkiRevision + 1,
                    current.State.CrlNumber);
                var nextDirectory = new DirectorySnapshot(
                    new[]
                    {
                        ServiceRecord.CreateActive(
                            definition,
                            issuedUtc,
                            expected.LogicalClock + 1,
                            InstanceId)
                    },
                    new PendingRegistration[0],
                    expected.LogicalClock + 1);
                return new CertificateServiceMutationCandidate(
                    CertificateServiceMutationOperation.Registration,
                    expected,
                    nextDirectory,
                    current.State.WithHighWater(
                        nextLedger.PkiRevision,
                        nextLedger.CrlNumber),
                    nextLedger,
                    current.CrlDer,
                    evidence,
                    serialNumber);
            }
        }

        internal CertificateServiceMutationCandidate PrepareRenewal()
        {
            DirectorySnapshot directory = DirectoryState.CurrentSnapshot;
            using (CertificateAuthorityStoreSnapshot current =
                _store.GetCurrent())
            {
                CertificateLedgerEntry previous = current.Ledger
                    .EntriesBySerial.Values.Single(
                        entry => entry.Status
                            == CertificateLedgerStatus.Current);
                DateTime issuedUtc = TestData.Utc(10);
                PkiTestSigningRequest request =
                    PkiTestData.CreateRsaSigningRequest(
                        previous.ServiceIdentity);
                var evidence =
                    CertificateIssuanceRequestEvidence.CreateRenewal(
                        Guid.NewGuid(),
                        previous.SerialNumber,
                        previous.ServiceDefinition,
                        request.DerBytes);
                CertificateLedgerEntry issued = IssueEntry(
                    current,
                    previous.ServiceDefinition,
                    evidence,
                    request.DerBytes,
                    issuedUtc,
                    out CertificateSerialNumber serialNumber);
                CertificateLedgerEntry[] entries = current.Ledger
                    .EntriesBySerial.Values
                    .Select(entry => entry.SerialNumber
                            == previous.SerialNumber
                        ? entry.ScheduleRevocation(TestData.Utc(20))
                        : entry)
                    .Concat(new[] { issued })
                    .ToArray();
                var nextLedger = new CertificateLedgerSnapshot(
                    entries,
                    current.State.PkiRevision + 1,
                    current.State.CrlNumber);
                return new CertificateServiceMutationCandidate(
                    CertificateServiceMutationOperation.Renewal,
                    directory,
                    directory,
                    current.State.WithHighWater(
                        nextLedger.PkiRevision,
                        nextLedger.CrlNumber),
                    nextLedger,
                    current.CrlDer,
                    evidence,
                    serialNumber);
            }
        }

        private CertificateLedgerEntry IssueEntry(
            CertificateAuthorityStoreSnapshot current,
            ServiceDefinition definition,
            CertificateIssuanceRequestEvidence evidence,
            byte[] csrDer,
            DateTime issuedUtc,
            out CertificateSerialNumber serialNumber)
        {
            if (!CertificateSigningRequestValidator.TryValidate(
                    csrDer,
                    definition.ServiceEndpointIdentity,
                    out ValidatedCertificateSigningRequest validated,
                    out CertificateSigningRequestValidationError error))
            {
                throw new InvalidDataException(
                    "The test CSR is invalid: " + error + ".");
            }

            byte[] privateKey = null;
            byte[] leafDer = null;
            byte[] csrSha256 = null;
            byte[] payloadSha256 = null;
            byte[] spkiSha256 = null;
            try
            {
                privateKey = _protector.Unprotect(
                    current.ProtectedPrivateKey);
                SiteCertificateAuthority authority =
                    SiteCertificateAuthority.Restore(
                        current.State.SiteId,
                        current.CaCertificateDer,
                        privateKey,
                        issuedUtc);
                PkiSerialNumber serial = PkiSerialNumber.CreateRandom(
                    new SecureRandom(),
                    value => current.Ledger.EntriesBySerial.Values.Any(
                        entry => StringComparer.Ordinal.Equals(
                            entry.SerialNumber.Hex,
                            value)));
                serialNumber = serial.ToLedgerSerialNumber();
                using (IssuedCertificateArtifact artifact =
                    authority.IssueServiceLeaf(
                        validated,
                        DirectoryIdentity,
                        serial,
                        issuedUtc,
                        new SecureRandom()))
                {
                    leafDer = artifact.GetCertificateDer();
                    csrSha256 = evidence.GetCsrSha256();
                    payloadSha256 = evidence.GetRequestPayloadSha256();
                    spkiSha256 = validated
                        .GetSubjectPublicKeyInfoSha256();
                    return CertificateLedgerEntry.CreateIssued(
                        serialNumber,
                        definition,
                        evidence.RequestId,
                        evidence.IssuanceKind,
                        csrSha256,
                        payloadSha256,
                        spkiSha256,
                        leafDer,
                        issuedUtc,
                        artifact.NotBeforeUtc,
                        artifact.NotAfterUtc);
                }
            }
            finally
            {
                Clear(privateKey);
                Clear(leafDer);
                Clear(csrSha256);
                Clear(payloadSha256);
                Clear(spkiSha256);
            }
        }
    }
}
