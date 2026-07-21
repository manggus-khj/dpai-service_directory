using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateRenewalTransactionTests
    {
        [TestMethod]
        public void RenewalSchedulesOverlapAndExactRetryReturnsSameLeaf()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                ServiceDefinition definition = TestData.Definition(
                    productCode: "AB12",
                    serviceHostName: "service.internal",
                    serviceIpv4Address: "10.20.30.40");
                PkiTestSigningRequest currentKey = Register(
                    context,
                    definition,
                    TestData.Utc(5),
                    out ExternalRegistrationServiceResult registration);
                PkiTestSigningRequest nextKey =
                    PkiTestData.CreateRsaSigningRequest(
                        definition.ServiceEndpointIdentity);
                Guid renewalRequestId = Guid.NewGuid();
                ExternalCertificateRenewalRequest firstRequest =
                    CreateRenewalRequest(
                        definition,
                        registration.Certificate.SerialNumber,
                        renewalRequestId,
                        TestData.Utc(10),
                        Nonce(0),
                        nextKey,
                        currentKey.KeyPair.Private);

                ExternalRegistrationServiceResult renewed = Renew(
                    context,
                    definition,
                    nextKey,
                    firstRequest,
                    TestData.Utc(10));
                ExternalCertificateRenewalRequest retryRequest =
                    CreateRenewalRequest(
                        definition,
                        registration.Certificate.SerialNumber,
                        renewalRequestId,
                        TestData.Utc(11),
                        Nonce(16),
                        nextKey,
                        currentKey.KeyPair.Private);
                ExternalRegistrationServiceResult replayed = Renew(
                    context,
                    definition,
                    nextKey,
                    retryRequest,
                    TestData.Utc(11));

                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Renewed,
                    renewed.Status);
                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Replayed,
                    replayed.Status);
                Assert.AreEqual(
                    renewed.Certificate.SerialNumber,
                    replayed.Certificate.SerialNumber);
                Assert.AreEqual(
                    1UL,
                    context.DirectoryState.CurrentSnapshot.LogicalClock);
                using (CertificateAuthorityStoreSnapshot snapshot =
                    context.Store.GetCurrent())
                {
                    CertificateLedgerEntry oldEntry = Entry(
                        snapshot,
                        registration.Certificate.SerialNumber);
                    Assert.AreEqual(
                        CertificateLedgerStatus.Retiring,
                        oldEntry.Status);
                    Assert.AreEqual(
                        TestData.Utc(10).AddDays(
                            CertificateAuthorityStore
                                .UnchangedIdentityOverlapDays),
                        oldEntry.ScheduledRevocationUtc);
                    Assert.AreEqual(1UL, snapshot.State.CrlNumber);
                }
            }
        }

        [TestMethod]
        public void IdentityChangeUpdatesDirectoryAndUsesOneDayOverlap()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                ServiceDefinition currentDefinition = TestData.Definition(
                    productCode: "AB12",
                    serviceHostName: "service.internal",
                    serviceIpv4Address: "10.20.30.40");
                PkiTestSigningRequest currentKey = Register(
                    context,
                    currentDefinition,
                    TestData.Utc(5),
                    out ExternalRegistrationServiceResult registration);
                ServiceDefinition changedDefinition = TestData.Definition(
                    productCode: "AB12",
                    serviceHostName: "service.internal",
                    serviceIpv4Address: "10.20.30.41");
                PkiTestSigningRequest nextKey =
                    PkiTestData.CreateRsaSigningRequest(
                        changedDefinition.ServiceEndpointIdentity);
                ExternalCertificateRenewalRequest request =
                    CreateRenewalRequest(
                        changedDefinition,
                        registration.Certificate.SerialNumber,
                        Guid.NewGuid(),
                        TestData.Utc(10),
                        Nonce(32),
                        nextKey,
                        currentKey.KeyPair.Private);

                context.FaultInjector.Clear();
                ExternalRegistrationServiceResult renewed = Renew(
                    context,
                    changedDefinition,
                    nextKey,
                    request,
                    TestData.Utc(10));

                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Renewed,
                    renewed.Status);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        StateFileTarget.Directory,
                        StateFileTarget.PkiMetadata,
                        StateFileTarget.CertificateLedger
                    },
                    context.FaultInjector.AppliedTargets.ToArray());
                Assert.AreEqual(
                    2UL,
                    context.DirectoryState.CurrentSnapshot.LogicalClock);
                Assert.AreEqual(
                    "10.20.30.41",
                    context.DirectoryState.CurrentSnapshot.Records.Values
                        .Single().Definition.ServiceIpv4Address);
                using (CertificateAuthorityStoreSnapshot snapshot =
                    context.Store.GetCurrent())
                {
                    CertificateLedgerEntry oldEntry = Entry(
                        snapshot,
                        registration.Certificate.SerialNumber);
                    Assert.AreEqual(
                        TestData.Utc(10).AddHours(
                            CertificateAuthorityStore
                                .ChangedIdentityOverlapHours),
                        oldEntry.ScheduledRevocationUtc);
                }
            }
        }

        [TestMethod]
        public void InvalidProofAndRepeatedNonceNeverIssueAnotherLeaf()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                ServiceDefinition definition = TestData.Definition(
                    productCode: "AB12",
                    serviceHostName: "service.internal",
                    serviceIpv4Address: "10.20.30.40");
                PkiTestSigningRequest currentKey = Register(
                    context,
                    definition,
                    TestData.Utc(5),
                    out ExternalRegistrationServiceResult registration);
                PkiTestSigningRequest nextKey =
                    PkiTestData.CreateRsaSigningRequest(
                        definition.ServiceEndpointIdentity);
                byte[] nonce = Nonce(48);
                ExternalCertificateRenewalRequest valid =
                    CreateRenewalRequest(
                        definition,
                        registration.Certificate.SerialNumber,
                        Guid.NewGuid(),
                        TestData.Utc(10),
                        nonce,
                        nextKey,
                        currentKey.KeyPair.Private);
                ExternalCertificateRenewalRequest invalid =
                    CreateRenewalRequest(
                        definition,
                        registration.Certificate.SerialNumber,
                        Guid.NewGuid(),
                        TestData.Utc(10),
                        Nonce(64),
                        nextKey,
                        nextKey.KeyPair.Private);

                ExternalRegistrationServiceResult rejected = Renew(
                    context,
                    definition,
                    nextKey,
                    invalid,
                    TestData.Utc(10));
                ExternalRegistrationServiceResult renewed = Renew(
                    context,
                    definition,
                    nextKey,
                    valid,
                    TestData.Utc(10));
                ExternalRegistrationServiceResult replayRejected = Renew(
                    context,
                    definition,
                    nextKey,
                    valid,
                    TestData.Utc(10));

                Assert.AreEqual(
                    ExternalRegistrationServiceStatus
                        .InvalidCertificateProof,
                    rejected.Status);
                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Renewed,
                    renewed.Status);
                Assert.AreEqual(
                    ExternalRegistrationServiceStatus
                        .InvalidCertificateProof,
                    replayRejected.Status);
                using (CertificateAuthorityStoreSnapshot snapshot =
                    context.Store.GetCurrent())
                {
                    Assert.AreEqual(
                        2,
                        snapshot.Ledger.EntriesBySerial.Count);
                }
            }
        }

        [TestMethod]
        public void CrlReadMaintenancePublishesDueRetirement()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                ServiceDefinition definition = TestData.Definition(
                    productCode: "AB12",
                    serviceHostName: "service.internal",
                    serviceIpv4Address: "10.20.30.40");
                PkiTestSigningRequest currentKey = Register(
                    context,
                    definition,
                    TestData.Utc(5),
                    out ExternalRegistrationServiceResult registration);
                PkiTestSigningRequest nextKey =
                    PkiTestData.CreateRsaSigningRequest(
                        definition.ServiceEndpointIdentity);
                ExternalCertificateRenewalRequest request =
                    CreateRenewalRequest(
                        definition,
                        registration.Certificate.SerialNumber,
                        Guid.NewGuid(),
                        TestData.Utc(10),
                        Nonce(80),
                        nextKey,
                        currentKey.KeyPair.Private);
                Renew(
                    context,
                    definition,
                    nextKey,
                    request,
                    TestData.Utc(10));
                DateTime dueUtc = TestData.Utc(10).AddDays(
                    CertificateAuthorityStore
                        .UnchangedIdentityOverlapDays);

                Assert.IsTrue(
                    context.Store.PublishDueScheduledRetirements(
                        context.DirectoryState,
                        context.InstanceId,
                        dueUtc));

                using (CertificateAuthorityStoreSnapshot snapshot =
                    context.Store.GetCurrent())
                {
                    CertificateLedgerEntry oldEntry = Entry(
                        snapshot,
                        registration.Certificate.SerialNumber);
                    Assert.AreEqual(
                        CertificateLedgerStatus.Revoked,
                        oldEntry.Status);
                    Assert.AreEqual(
                        CertificateRevocationReason.Superseded,
                        oldEntry.RevocationReason);
                    Assert.AreEqual(2UL, snapshot.State.CrlNumber);
                }
            }
        }

        [TestMethod]
        public void EcdsaCurrentLeafProofCanRenewToRsaLeaf()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                ServiceDefinition definition = TestData.Definition(
                    productCode: "AB12",
                    serviceHostName: "service.internal",
                    serviceIpv4Address: "10.20.30.40");
                PkiTestSigningRequest currentKey = Register(
                    context,
                    definition,
                    TestData.Utc(5),
                    out ExternalRegistrationServiceResult registration,
                    true);
                PkiTestSigningRequest nextKey =
                    PkiTestData.CreateRsaSigningRequest(
                        definition.ServiceEndpointIdentity);
                ExternalCertificateRenewalRequest request =
                    CreateRenewalRequest(
                        definition,
                        registration.Certificate.SerialNumber,
                        Guid.NewGuid(),
                        TestData.Utc(10),
                        Nonce(96),
                        nextKey,
                        currentKey.KeyPair.Private);

                ExternalRegistrationServiceResult renewed = Renew(
                    context,
                    definition,
                    nextKey,
                    request,
                    TestData.Utc(10));

                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Renewed,
                    renewed.Status);
            }
        }

        private static PkiTestSigningRequest Register(
            CertificateServiceMutationTestContext context,
            ServiceDefinition definition,
            DateTime utcNow,
            out ExternalRegistrationServiceResult result,
            bool useEcdsa = false)
        {
            PkiTestSigningRequest request = useEcdsa
                ? PkiTestData.CreateEcdsaP256SigningRequest(
                    definition.ServiceEndpointIdentity)
                : PkiTestData.CreateRsaSigningRequest(
                    definition.ServiceEndpointIdentity);
            Assert.IsTrue(CertificateSigningRequestValidator.TryValidate(
                request.DerBytes,
                definition.ServiceEndpointIdentity,
                out ValidatedCertificateSigningRequest validated,
                out CertificateSigningRequestValidationError error),
                error.ToString());
            var evidence =
                CertificateIssuanceRequestEvidence.CreateRegistration(
                    Guid.NewGuid(),
                    definition,
                    request.DerBytes);
            var mode = new RegistrationModeOwner(context.MutationGate);
            mode.Open();
            result = context.Store.RegisterService(
                context.DirectoryState,
                mode,
                context.DirectoryIdentity,
                context.InstanceId,
                definition,
                validated,
                evidence,
                utcNow);
            Assert.AreEqual(
                ExternalRegistrationServiceStatus.Registered,
                result.Status);
            return request;
        }

        private static ExternalRegistrationServiceResult Renew(
            CertificateServiceMutationTestContext context,
            ServiceDefinition definition,
            PkiTestSigningRequest nextKey,
            ExternalCertificateRenewalRequest request,
            DateTime utcNow)
        {
            Assert.IsTrue(CertificateSigningRequestValidator.TryValidate(
                nextKey.DerBytes,
                definition.ServiceEndpointIdentity,
                out ValidatedCertificateSigningRequest validated,
                out CertificateSigningRequestValidationError error),
                error.ToString());
            Assert.IsTrue(CertificateSerialNumber.TryCreate(
                request.CurrentSerialNumber,
                out CertificateSerialNumber currentSerial));
            var evidence =
                CertificateIssuanceRequestEvidence.CreateRenewal(
                    request.RenewalRequestId,
                    currentSerial,
                    definition,
                    nextKey.DerBytes);
            return context.Store.RenewService(
                context.DirectoryState,
                context.DirectoryIdentity,
                context.InstanceId,
                definition,
                validated,
                evidence,
                request,
                utcNow);
        }

        private static ExternalCertificateRenewalRequest
            CreateRenewalRequest(
                ServiceDefinition definition,
                string currentSerialNumber,
                Guid renewalRequestId,
                DateTime timestampUtc,
                byte[] nonce,
                PkiTestSigningRequest nextKey,
                AsymmetricKeyParameter currentPrivateKey)
        {
            byte[] identitySha256 =
                CertificateRenewalProofValidator
                    .ComputeServiceIdentitySha256(definition);
            byte[] csrSha256;
            using (SHA256 sha256 = SHA256.Create())
            {
                csrSha256 = sha256.ComputeHash(nextKey.DerBytes);
            }

            byte[] canonical = Encoding.UTF8.GetBytes(string.Concat(
                "DPAI-SD-CERTIFICATE-RENEW\n",
                definition.ProductCode.Value,
                "\n",
                currentSerialNumber,
                "\n",
                renewalRequestId.ToString("D").ToLowerInvariant(),
                "\n",
                timestampUtc.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture),
                "\n",
                Convert.ToBase64String(nonce),
                "\n",
                Convert.ToBase64String(csrSha256),
                "\n",
                Convert.ToBase64String(identitySha256),
                "\n"));
            ISigner signer = SignerUtilities.GetSigner(
                currentPrivateKey is ECPrivateKeyParameters
                    ? "SHA256WITHECDSA"
                    : "SHA256WITHRSA");
            signer.Init(true, currentPrivateKey);
            signer.BlockUpdate(canonical, 0, canonical.Length);
            byte[] signature = signer.GenerateSignature();
            try
            {
                return new ExternalCertificateRenewalRequest(
                    renewalRequestId,
                    definition.ProductCode.Value,
                    currentSerialNumber,
                    timestampUtc,
                    nonce,
                    definition.Name,
                    definition.ServiceHostName,
                    definition.ServiceIpv4Address,
                    definition.Port,
                    nextKey.DerBytes,
                    identitySha256,
                    signature);
            }
            finally
            {
                Array.Clear(identitySha256, 0, identitySha256.Length);
                Array.Clear(csrSha256, 0, csrSha256.Length);
                Array.Clear(canonical, 0, canonical.Length);
                Array.Clear(signature, 0, signature.Length);
            }
        }

        private static CertificateLedgerEntry Entry(
            CertificateAuthorityStoreSnapshot snapshot,
            string serialNumber)
        {
            Assert.IsTrue(CertificateSerialNumber.TryCreate(
                serialNumber,
                out CertificateSerialNumber serial));
            Assert.IsTrue(snapshot.Ledger.TryGetBySerial(
                serial,
                out CertificateLedgerEntry entry));
            return entry;
        }

        private static byte[] Nonce(int start)
        {
            return Enumerable.Range(
                    start,
                    ExternalApiContract.RenewalNonceBytes)
                .Select(value => (byte)value)
                .ToArray();
        }
    }
}
