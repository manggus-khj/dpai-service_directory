using System;
using System.IO;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class StorageV1ConsistencyValidatorTests
    {
        private static readonly DateTime UtcNow = new DateTime(
            2026,
            7,
            21,
            1,
            0,
            0,
            DateTimeKind.Utc);
        private static readonly Guid ActiveInstanceId = new Guid(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

        [TestMethod]
        public void ActiveIssuerAcceptsExactDirectoryLedgerCaKeyAndCrl()
        {
            using (ActiveFixture fixture = ActiveFixture.Create())
            {
                ValidateActive(fixture, fixture.Directory, fixture.Ledger);
            }
        }

        [TestMethod]
        public void ActiveIssuerRejectsDirectoryAndLeafDerMismatch()
        {
            using (ActiveFixture fixture = ActiveFixture.Create())
            {
                var mismatchedDirectory = new DirectorySnapshot(
                    new[]
                    {
                        ServiceRecord.CreateActive(
                            TestData.Definition(
                                "Different Name",
                                "AB12",
                                "vms-bridge.example.local",
                                "10.20.30.40",
                                21500),
                            UtcNow,
                            1,
                            ActiveInstanceId)
                    },
                    new PendingRegistration[0],
                    1);
                Assert.ThrowsExactly<InvalidDataException>(() =>
                    ValidateActive(
                        fixture,
                        mismatchedDirectory,
                        fixture.Ledger));

                byte[] corruptedLeaf = fixture.Entry.GetLeafCertificate();
                corruptedLeaf[corruptedLeaf.Length - 1] ^= 0x01;
                CertificateLedgerEntry corruptedEntry = CreateEntry(
                    fixture.Entry.SerialNumber,
                    fixture.Entry.IssuerCaSerialNumber,
                    fixture.Entry.ServiceDefinition,
                    corruptedLeaf,
                    fixture.Entry.GetSubjectPublicKeyInfoSha256(),
                    fixture.Entry.NotBeforeUtc,
                    fixture.Entry.NotAfterUtc);
                var corruptedLedger = new CertificateLedgerSnapshot(
                    new[] { corruptedEntry },
                    1,
                    1);
                Assert.ThrowsExactly<InvalidDataException>(() =>
                    ValidateActive(
                        fixture,
                        fixture.Directory,
                        corruptedLedger));
                Array.Clear(
                    corruptedLeaf,
                    0,
                    corruptedLeaf.Length);
            }
        }

        [TestMethod]
        public void ActiveIssuerDoesNotTreatRetiringAsRevokedHistory()
        {
            using (ActiveFixture fixture = ActiveFixture.Create())
            {
                CertificateLedgerEntry retiring = fixture.Entry
                    .ScheduleRevocation(UtcNow.AddMinutes(30));
                var ledger = new CertificateLedgerSnapshot(
                    new[] { retiring },
                    1,
                    1);

                Assert.ThrowsExactly<InvalidDataException>(() =>
                    ValidateActive(
                        fixture,
                        fixture.Directory,
                        ledger));
                Assert.ThrowsExactly<InvalidDataException>(() =>
                    ValidateActive(
                        fixture,
                        DirectorySnapshot.Empty(),
                        ledger));
            }
        }

        [TestMethod]
        public void StandbyRequiresPeerCacheHashAndForbidsLedgerAndCaKey()
        {
            using (ActiveFixture fixture = ActiveFixture.Create())
            {
                var standbyState = new CertificateAuthorityState(
                    fixture.State.SiteId,
                    ActiveInstanceId,
                    CertificateAuthorityRole.Standby,
                    fixture.State.CaSerialNumber,
                    fixture.State.GetCaSpkiSha256(),
                    fixture.State.NotBeforeUtc,
                    fixture.State.NotAfterUtc,
                    1,
                    1,
                    null);
                var cache = new PeerPkiCacheSnapshot(
                    ActiveInstanceId,
                    1,
                    1,
                    ComputeSha256(fixture.CrlDer),
                    new PeerPkiCacheCertificate[0]);
                ServiceDirectoryConfiguration configuration =
                    ServiceDirectoryConfiguration.CreateInitial(
                        PkiTestData.DirectoryIdentity(),
                        new Guid(
                            "11111111-2222-4333-8444-555555555555"));

                StorageV1ConsistencyValidator.Validate(
                    DirectorySnapshot.Empty(),
                    configuration,
                    standbyState,
                    null,
                    cache,
                    fixture.CaCertificateDer,
                    fixture.CrlDer,
                    null,
                    null,
                    UtcNow);

                var wrongHashCache = new PeerPkiCacheSnapshot(
                    ActiveInstanceId,
                    1,
                    1,
                    Hash(0x55),
                    new PeerPkiCacheCertificate[0]);
                Assert.ThrowsExactly<InvalidDataException>(() =>
                    StorageV1ConsistencyValidator.Validate(
                        DirectorySnapshot.Empty(),
                        configuration,
                        standbyState,
                        null,
                        wrongHashCache,
                        fixture.CaCertificateDer,
                        fixture.CrlDer,
                        null,
                        null,
                        UtcNow));
                Assert.ThrowsExactly<InvalidDataException>(() =>
                    StorageV1ConsistencyValidator.Validate(
                        DirectorySnapshot.Empty(),
                        configuration,
                        standbyState,
                        fixture.Ledger,
                        cache,
                        fixture.CaCertificateDer,
                        fixture.CrlDer,
                        fixture.CaPrivateKeyPkcs8,
                        null,
                        UtcNow));
            }
        }

        private static void ValidateActive(
            ActiveFixture fixture,
            DirectorySnapshot directory,
            CertificateLedgerSnapshot ledger)
        {
            StorageV1ConsistencyValidator.Validate(
                directory,
                fixture.Configuration,
                fixture.State,
                ledger,
                null,
                fixture.CaCertificateDer,
                fixture.CrlDer,
                fixture.CaPrivateKeyPkcs8,
                null,
                UtcNow);
        }

        private static CertificateLedgerEntry CreateEntry(
            CertificateSerialNumber serialNumber,
            CertificateSerialNumber issuerCaSerialNumber,
            ServiceDefinition definition,
            byte[] leafCertificateDer,
            byte[] spkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc)
        {
            return CertificateLedgerEntry.CreateIssued(
                serialNumber,
                issuerCaSerialNumber,
                definition,
                new Guid("77777777-8888-4999-aaaa-bbbbbbbbbbbb"),
                CertificateIssuanceKind.Registration,
                Hash(0x11),
                Hash(0x22),
                spkiSha256,
                leafCertificateDer,
                UtcNow,
                notBeforeUtc,
                notAfterUtc);
        }

        private static byte[] ComputeSha256(byte[] value)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(value);
            }
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

        private sealed class ActiveFixture : IDisposable
        {
            private ActiveFixture(
                ServiceDirectoryConfiguration configuration,
                DirectorySnapshot directory,
                CertificateAuthorityState state,
                CertificateLedgerEntry entry,
                CertificateLedgerSnapshot ledger,
                byte[] caCertificateDer,
                byte[] crlDer,
                byte[] caPrivateKeyPkcs8)
            {
                Configuration = configuration;
                Directory = directory;
                State = state;
                Entry = entry;
                Ledger = ledger;
                CaCertificateDer = caCertificateDer;
                CrlDer = crlDer;
                CaPrivateKeyPkcs8 = caPrivateKeyPkcs8;
            }

            internal ServiceDirectoryConfiguration Configuration { get; }

            internal DirectorySnapshot Directory { get; }

            internal CertificateAuthorityState State { get; }

            internal CertificateLedgerEntry Entry { get; }

            internal CertificateLedgerSnapshot Ledger { get; }

            internal byte[] CaCertificateDer { get; }

            internal byte[] CrlDer { get; }

            internal byte[] CaPrivateKeyPkcs8 { get; }

            internal static ActiveFixture Create()
            {
                var random = new SecureRandom();
                SiteCertificateAuthority authority =
                    SiteCertificateAuthority.Create(
                        new Guid(
                            "12345678-1234-4567-89ab-123456789abc"),
                        UtcNow,
                        random);
                ServiceDefinition definition = TestData.Definition(
                    "VMS Bridge",
                    "AB12",
                    "vms-bridge.example.local",
                    "10.20.30.40",
                    21500);
                PkiTestSigningRequest request =
                    PkiTestData.CreateEcdsaP256SigningRequest(
                        definition.ServiceEndpointIdentity);
                ValidatedCertificateSigningRequest validated;
                CertificateSigningRequestValidationError validationError;
                Assert.IsTrue(CertificateSigningRequestValidator.TryValidate(
                    request.DerBytes,
                    definition.ServiceEndpointIdentity,
                    out validated,
                    out validationError));
                PkiSerialNumber serialNumber = PkiSerialNumber.CreateRandom(
                    random,
                    value => StringComparer.Ordinal.Equals(
                        value,
                        authority.SerialNumber.Hex));
                byte[] leafCertificateDer;
                DateTime notBeforeUtc;
                DateTime notAfterUtc;
                using (IssuedCertificateArtifact issued =
                    authority.IssueServiceLeaf(
                        validated,
                        PkiTestData.DirectoryIdentity(),
                        serialNumber,
                        UtcNow,
                        random))
                {
                    leafCertificateDer = issued.GetCertificateDer();
                    notBeforeUtc = issued.NotBeforeUtc;
                    notAfterUtc = issued.NotAfterUtc;
                }

                CertificateLedgerEntry entry = CreateEntry(
                    serialNumber.ToLedgerSerialNumber(),
                    authority.SerialNumber,
                    definition,
                    leafCertificateDer,
                    validated.GetSubjectPublicKeyInfoSha256(),
                    notBeforeUtc,
                    notAfterUtc);
                var ledger = new CertificateLedgerSnapshot(
                    new[] { entry },
                    1,
                    1);
                var directory = new DirectorySnapshot(
                    new[]
                    {
                        ServiceRecord.CreateActive(
                            definition,
                            UtcNow,
                            1,
                            ActiveInstanceId)
                    },
                    new PendingRegistration[0],
                    1);
                byte[] caCertificateDer = authority.GetCertificateDer();
                byte[] caPrivateKeyPkcs8 =
                    authority.ExportPrivateKeyPkcs8();
                CertificateRevocationListArtifact crl =
                    authority.CreateRevocationList(
                        1,
                        new RevokedCertificateEntry[0],
                        UtcNow,
                        UtcNow.AddDays(1),
                        random);
                var state = new CertificateAuthorityState(
                    authority.SiteId,
                    ActiveInstanceId,
                    CertificateAuthorityRole.ActiveIssuer,
                    authority.SerialNumber,
                    authority.GetSpkiSha256(),
                    authority.NotBeforeUtc,
                    authority.NotAfterUtc,
                    1,
                    1,
                    null);
                ServiceDirectoryConfiguration configuration =
                    ServiceDirectoryConfiguration.CreateInitial(
                        PkiTestData.DirectoryIdentity(),
                        ActiveInstanceId);
                byte[] crlDer = crl.GetDerBytes();
                Array.Clear(
                    leafCertificateDer,
                    0,
                    leafCertificateDer.Length);
                return new ActiveFixture(
                    configuration,
                    directory,
                    state,
                    entry,
                    ledger,
                    caCertificateDer,
                    crlDer,
                    caPrivateKeyPkcs8);
            }

            public void Dispose()
            {
                Array.Clear(
                    CaCertificateDer,
                    0,
                    CaCertificateDer.Length);
                Array.Clear(CrlDer, 0, CrlDer.Length);
                Array.Clear(
                    CaPrivateKeyPkcs8,
                    0,
                    CaPrivateKeyPkcs8.Length);
            }
        }
    }
}
