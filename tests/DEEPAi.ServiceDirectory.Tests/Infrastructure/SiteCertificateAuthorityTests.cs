using System;
using System.Linq;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class SiteCertificateAuthorityTests
    {
        private static readonly DateTime UtcNow = new DateTime(
            2026,
            7,
            19,
            2,
            0,
            0,
            DateTimeKind.Utc);

        [TestMethod]
        public void SiteCaAndDirectoryLeafUseSeparatedExactProfiles()
        {
            var random = new SecureRandom();
            SiteCertificateAuthority authority = SiteCertificateAuthority.Create(
                new Guid("3d8ff138-4e9a-4e52-b108-e3af248b1787"),
                UtcNow,
                random);
            var parser = new X509CertificateParser();
            X509Certificate caCertificate = parser.ReadCertificate(
                authority.GetCertificateDer());

            Assert.AreEqual(0, caCertificate.GetBasicConstraints());
            Assert.IsNull(caCertificate.GetSubjectAlternativeNameExtension());
            Assert.IsTrue(caCertificate.GetKeyUsage()[5]);
            Assert.IsTrue(caCertificate.GetKeyUsage()[6]);
            Assert.AreEqual(3072, ((RsaKeyParameters)caCertificate.GetPublicKey()).Modulus.BitLength);
            Assert.AreEqual(32, authority.GetSpkiSha256().Length);
            Assert.IsTrue(authority.SerialNumber.IsValid);
            Assert.AreEqual(32, authority.SerialNumber.Hex.Length);

            DirectoryEndpointIdentity directoryIdentity = PkiTestData.DirectoryIdentity();
            PkiSerialNumber serial = PkiSerialNumber.CreateRandom(random, null);
            using (IssuedCertificateArtifact directoryLeaf = authority.CreateDirectoryLeaf(
                directoryIdentity,
                serial,
                UtcNow,
                random))
            {
                Assert.IsTrue(directoryLeaf.HasPrivateKey);
                byte[] privateKey = directoryLeaf.ExportPrivateKeyPkcs8();
                try
                {
                    Assert.IsNotNull(PrivateKeyFactory.CreateKey(privateKey));
                }
                finally
                {
                    Array.Clear(privateKey, 0, privateKey.Length);
                }

                X509Certificate leaf = parser.ReadCertificate(
                    directoryLeaf.GetCertificateDer());
                leaf.Verify(caCertificate.GetPublicKey());
                AssertServerProfile(
                    leaf,
                    "management.example.local",
                    "10.20.30.10",
                    directoryIdentity);
                SiteCertificateAuthority.ValidateDirectoryLeaf(
                    leaf.GetEncoded(),
                    caCertificate.GetEncoded(),
                    directoryIdentity,
                    UtcNow);

                DirectoryEndpointIdentity wrongIdentity;
                EndpointIdentityValidationError identityError;
                Assert.IsTrue(DirectoryEndpointIdentity.TryCreate(
                    "other.example.local",
                    "10.20.30.10",
                    out wrongIdentity,
                    out identityError));
                Assert.ThrowsExactly<System.IO.InvalidDataException>(() =>
                    SiteCertificateAuthority.ValidateDirectoryLeaf(
                        leaf.GetEncoded(),
                        caCertificate.GetEncoded(),
                        wrongIdentity,
                        UtcNow));
                Assert.ThrowsExactly<System.IO.InvalidDataException>(() =>
                    SiteCertificateAuthority.ValidateDirectoryLeaf(
                        leaf.GetEncoded(),
                        caCertificate.GetEncoded(),
                        directoryIdentity,
                        directoryLeaf.NotAfterUtc));
                SiteCertificateAuthority.ValidateDirectoryLeaf(
                    leaf.GetEncoded(),
                    caCertificate.GetEncoded(),
                    directoryIdentity,
                    directoryLeaf.NotAfterUtc,
                    false);
            }

            byte[] caPrivateKey = authority.ExportPrivateKeyPkcs8();
            try
            {
                SiteCertificateAuthority restored = SiteCertificateAuthority.Restore(
                    authority.SiteId,
                    authority.GetCertificateDer(),
                    caPrivateKey,
                    UtcNow);
                CollectionAssert.AreEqual(
                    authority.GetSpkiSha256(),
                    restored.GetSpkiSha256());
                Assert.AreEqual(authority.SerialNumber, restored.SerialNumber);
                Assert.ThrowsExactly<System.Security.Cryptography.CryptographicException>(
                    () => SiteCertificateAuthority.Restore(
                        authority.SiteId,
                        authority.GetCertificateDer(),
                        caPrivateKey,
                        UtcNow.AddYears(21)));
            }
            finally
            {
                Array.Clear(caPrivateKey, 0, caPrivateKey.Length);
            }
        }

        [TestMethod]
        public void ServiceLeafUsesValidatedCsrPublicKeyAndExactSanPair()
        {
            var random = new SecureRandom();
            SiteCertificateAuthority authority = SiteCertificateAuthority.Create(
                Guid.NewGuid(),
                UtcNow,
                random);
            ServiceEndpointIdentity identity = PkiTestData.ServiceIdentity();
            PkiTestSigningRequest request = PkiTestData.CreateEcdsaP256SigningRequest(identity);
            ValidatedCertificateSigningRequest validated;
            CertificateSigningRequestValidationError error;
            Assert.IsTrue(CertificateSigningRequestValidator.TryValidate(
                request.DerBytes,
                identity,
                out validated,
                out error));

            PkiSerialNumber serial = PkiSerialNumber.CreateRandom(random, null);
            DirectoryEndpointIdentity directoryIdentity = PkiTestData.DirectoryIdentity();
            using (IssuedCertificateArtifact issued = authority.IssueServiceLeaf(
                validated,
                directoryIdentity,
                serial,
                UtcNow,
                random))
            {
                Assert.IsFalse(issued.HasPrivateKey);
                Assert.AreEqual(serial.Hex, issued.SerialNumber.Hex);
                X509Certificate certificate = new X509CertificateParser()
                    .ReadCertificate(issued.GetCertificateDer());
                X509Certificate caCertificate = new X509CertificateParser()
                    .ReadCertificate(authority.GetCertificateDer());
                certificate.Verify(caCertificate.GetPublicKey());
                AssertServerProfile(
                    certificate,
                    identity.ServiceHostName,
                    identity.ServiceIpv4Address,
                    directoryIdentity);

                CollectionAssert.AreEqual(
                    Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory
                        .CreateSubjectPublicKeyInfo(request.KeyPair.Public)
                        .GetDerEncoded(),
                    Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory
                        .CreateSubjectPublicKeyInfo(certificate.GetPublicKey())
                        .GetDerEncoded());
            }
        }

        [TestMethod]
        public void CrlIsSignedMonotonicArtifactWithCanonicalEntries()
        {
            var random = new SecureRandom();
            SiteCertificateAuthority authority = SiteCertificateAuthority.Create(
                Guid.NewGuid(),
                UtcNow,
                random);
            PkiSerialNumber first = PkiSerialNumber.CreateRandom(random, null);
            PkiSerialNumber second = PkiSerialNumber.CreateRandom(
                random,
                value => StringComparer.Ordinal.Equals(value, first.Hex));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                new RevokedCertificateEntry(
                    first,
                    UtcNow,
                    CertificateRevocationReason.Unspecified));
            Assert.ThrowsExactly<ArgumentException>(() =>
                authority.CreateRevocationList(
                    18,
                    new[]
                    {
                        new RevokedCertificateEntry(
                            first,
                            UtcNow.AddMinutes(1),
                            CertificateRevocationReason.Superseded)
                    },
                    UtcNow,
                    UtcNow.AddDays(1),
                    random));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                authority.CreateRevocationList(
                    18,
                    new RevokedCertificateEntry[0],
                    UtcNow,
                    UtcNow.AddYears(21),
                    random));

            CertificateRevocationListArtifact artifact = authority.CreateRevocationList(
                19,
                new[]
                {
                    new RevokedCertificateEntry(
                        second,
                        UtcNow.AddMinutes(-1),
                        CertificateRevocationReason.Superseded),
                    new RevokedCertificateEntry(
                        first,
                        UtcNow.AddMinutes(-2),
                        CertificateRevocationReason.CessationOfOperation)
                },
                UtcNow,
                UtcNow.AddDays(1),
                random);

            X509Crl crl = new X509CrlParser().ReadCrl(artifact.GetDerBytes());
            X509Certificate caCertificate = new X509CertificateParser()
                .ReadCertificate(authority.GetCertificateDer());
            crl.Verify(caCertificate.GetPublicKey());
            Assert.AreEqual(19L, DerInteger.GetInstance(
                crl.GetExtensionParsedValue(X509Extensions.CrlNumber)).PositiveValue.LongValue);
            Assert.IsNotNull(crl.GetRevokedCertificate(first.Value));
            Assert.IsNotNull(crl.GetRevokedCertificate(second.Value));
            Assert.AreEqual(32, artifact.GetSha256().Length);
            StringAssert.StartsWith(artifact.GetQuotedEtag(), "\"");
            StringAssert.EndsWith(artifact.GetQuotedEtag(), "\"");
        }

        private static void AssertServerProfile(
            X509Certificate certificate,
            string expectedDnsName,
            string expectedIpv4Address,
            DirectoryEndpointIdentity directoryIdentity)
        {
            Assert.AreEqual(-1, certificate.GetBasicConstraints());
            bool[] keyUsage = certificate.GetKeyUsage();
            Assert.IsNotNull(keyUsage);
            Assert.IsTrue(keyUsage[0]);
            Assert.IsTrue(certificate.GetExtendedKeyUsage().Any(
                oid => oid.Equals(KeyPurposeID.id_kp_serverAuth)));
            CrlDistPoint crlDistributionPoints = CrlDistPoint.GetInstance(
                certificate.GetExtensionParsedValue(
                    X509Extensions.CrlDistributionPoints));
            Assert.IsNotNull(crlDistributionPoints);
            DistributionPoint[] points = crlDistributionPoints.GetDistributionPoints();
            Assert.AreEqual(1, points.Length);
            GeneralName[] crlNames = GeneralNames.GetInstance(
                    points[0].DistributionPointName.Name)
                .GetNames();
            Assert.AreEqual(2, crlNames.Length);
            Assert.IsTrue(crlNames.Any(name =>
                name.TagNo == GeneralName.UniformResourceIdentifier
                && StringComparer.Ordinal.Equals(
                    DerIA5String.GetInstance(name.Name).GetString(),
                    "https://"
                        + directoryIdentity.DirectoryHostName
                        + ":21000/pki/crl")));
            Assert.IsTrue(crlNames.Any(name =>
                name.TagNo == GeneralName.UniformResourceIdentifier
                && StringComparer.Ordinal.Equals(
                    DerIA5String.GetInstance(name.Name).GetString(),
                    "https://"
                        + directoryIdentity.DirectoryIpv4Address
                        + ":21000/pki/crl")));

            GeneralName[] names = certificate
                .GetSubjectAlternativeNameExtension()
                .GetNames();
            Assert.AreEqual(2, names.Length);
            Assert.IsTrue(names.Any(name =>
                name.TagNo == GeneralName.DnsName
                && StringComparer.Ordinal.Equals(
                    DerIA5String.GetInstance(name.Name).GetString(),
                    expectedDnsName)));
            Assert.IsTrue(names.Any(name =>
                name.TagNo == GeneralName.IPAddress
                && StringComparer.Ordinal.Equals(
                    FormatIpv4(Asn1OctetString.GetInstance(name.Name).GetOctets()),
                    expectedIpv4Address)));
        }

        private static string FormatIpv4(byte[] value)
        {
            return value == null || value.Length != 4
                ? null
                : string.Join(".", value[0], value[1], value[2], value[3]);
        }
    }
}
