using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class SiteCertificateAuthority
    {
        internal static void ValidateStoredCaCertificate(
            CertificateAuthorityState state,
            byte[] certificateDer,
            DateTime utcNow)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (certificateDer == null)
            {
                throw new ArgumentNullException(nameof(certificateDer));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            X509Certificate certificate;
            try
            {
                certificate = new X509Certificate(certificateDer);
                ValidateCaCertificate(
                    state.SiteId,
                    certificate,
                    certificate.GetPublicKey(),
                    utcNow);
            }
            catch (Exception exception)
                when (exception is GeneralSecurityException
                    || exception is IOException
                    || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    "Stored CA certificate validation failed.",
                    exception);
            }

            byte[] canonicalDer = certificate.GetEncoded();
            byte[] spkiSha256 = ComputeSubjectPublicKeyInfoSha256(
                certificate.GetPublicKey());
            byte[] expectedSpkiSha256 = state.GetCaSpkiSha256();
            try
            {
                string serialHex = certificate.SerialNumber.ToString(16)
                    .PadLeft(32, '0')
                    .ToUpperInvariant();
                if (!AreEqual(canonicalDer, certificateDer)
                    || !StringComparer.Ordinal.Equals(
                        serialHex,
                        state.CaSerialNumber.Hex)
                    || AsUtc(certificate.NotBefore) != state.NotBeforeUtc
                    || AsUtc(certificate.NotAfter) != state.NotAfterUtc
                    || !AreEqual(spkiSha256, expectedSpkiSha256))
                {
                    throw new InvalidDataException(
                        "Stored CA certificate fields do not match PKI state.");
                }
            }
            finally
            {
                Array.Clear(canonicalDer, 0, canonicalDer.Length);
                Array.Clear(spkiSha256, 0, spkiSha256.Length);
                Array.Clear(
                    expectedSpkiSha256,
                    0,
                    expectedSpkiSha256.Length);
            }
        }

        internal static void ValidateDirectoryLeaf(
            byte[] leafCertificateDer,
            byte[] caCertificateDer,
            DirectoryEndpointIdentity identity,
            DateTime utcNow,
            bool requireCurrent = true)
        {
            if (leafCertificateDer == null)
            {
                throw new ArgumentNullException(nameof(leafCertificateDer));
            }

            if (caCertificateDer == null)
            {
                throw new ArgumentNullException(nameof(caCertificateDer));
            }

            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            X509Certificate leaf;
            X509Certificate authority;
            try
            {
                leaf = new X509Certificate(leafCertificateDer);
                authority = new X509Certificate(caCertificateDer);
                leaf.Verify(authority.GetPublicKey());
            }
            catch (Exception exception)
                when (exception is GeneralSecurityException
                    || exception is IOException
                    || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    "Directory leaf certificate validation failed.",
                    exception);
            }

            byte[] canonicalDer = leaf.GetEncoded();
            try
            {
                bool[] keyUsage = leaf.GetKeyUsage();
                IList<DerObjectIdentifier> extendedKeyUsage =
                    leaf.GetExtendedKeyUsage();
                bool exactKeyUsage = keyUsage != null
                    && keyUsage.Length > 0
                    && keyUsage[0]
                    && !keyUsage.Skip(1).Any(value => value);
                bool exactExtendedKeyUsage = extendedKeyUsage != null
                    && extendedKeyUsage.Count == 1
                    && extendedKeyUsage[0].Equals(
                        KeyPurposeID.id_kp_serverAuth);
                DateTime notBeforeUtc = AsUtc(leaf.NotBefore);
                DateTime notAfterUtc = AsUtc(leaf.NotAfter);
                PkiSerialNumber serialNumber;
                PkiSerialNumber caSerialNumber;
                if (!AreEqual(canonicalDer, leafCertificateDer)
                    || leaf.Version != 3
                    || !StringComparer.Ordinal.Equals(
                        leaf.SigAlgOid,
                        PkcsObjectIdentifiers.Sha256WithRsaEncryption.Id)
                    || !HasExactExtensions(
                        leaf,
                        new[]
                        {
                            X509Extensions.BasicConstraints.Id,
                            X509Extensions.KeyUsage.Id
                        },
                        new[]
                        {
                            X509Extensions.ExtendedKeyUsage.Id,
                            X509Extensions.SubjectAlternativeName.Id,
                            X509Extensions.SubjectKeyIdentifier.Id,
                            X509Extensions.AuthorityKeyIdentifier.Id,
                            X509Extensions.CrlDistributionPoints.Id
                        })
                    || !PkiSerialNumber.TryCreate(
                        leaf.SerialNumber,
                        out serialNumber)
                    || !PkiSerialNumber.TryCreate(
                        authority.SerialNumber,
                        out caSerialNumber)
                    || serialNumber.Equals(caSerialNumber)
                    || !leaf.IssuerDN.Equivalent(authority.SubjectDN)
                    || !leaf.SubjectDN.Equivalent(
                        new X509Name(
                            "CN=" + identity.DirectoryHostName))
                    || leaf.GetBasicConstraints() != -1
                    || !exactKeyUsage
                    || !exactExtendedKeyUsage
                    || (requireCurrent && notBeforeUtc > utcNow)
                    || (requireCurrent && notAfterUtc <= utcNow)
                    || notAfterUtc <= notBeforeUtc
                    || notAfterUtc > notBeforeUtc.AddYears(
                        LeafValidityYears)
                    || notAfterUtc > AsUtc(authority.NotAfter))
                {
                    throw new InvalidDataException(
                        "Directory leaf certificate fields do not match the required profile.");
                }

                ServiceEndpointIdentity serviceShape;
                EndpointIdentityValidationError identityError;
                if (!ServiceEndpointIdentity.TryCreate(
                        identity.DirectoryHostName,
                        identity.DirectoryIpv4Address,
                        out serviceShape,
                        out identityError))
                {
                    throw new InvalidDataException(
                        "Directory endpoint identity is invalid.");
                }

                ValidateIssuedServiceIdentity(leaf, serviceShape);
                ValidateCrlDistributionPoints(
                    leaf,
                    identity,
                    caSerialNumber.Hex);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "Directory leaf certificate identity is invalid.",
                    exception);
            }
            finally
            {
                Array.Clear(canonicalDer, 0, canonicalDer.Length);
            }
        }

        internal static void ValidateStoredServiceCertificate(
            byte[] leafCertificateDer,
            byte[] caCertificateDer,
            CertificateLedgerEntry entry,
            DirectoryEndpointIdentity directoryIdentity = null)
        {
            if (leafCertificateDer == null)
            {
                throw new ArgumentNullException(nameof(leafCertificateDer));
            }

            if (caCertificateDer == null)
            {
                throw new ArgumentNullException(nameof(caCertificateDer));
            }

            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            X509Certificate leaf;
            X509Certificate authority;
            try
            {
                leaf = new X509Certificate(leafCertificateDer);
                authority = new X509Certificate(caCertificateDer);
                leaf.Verify(authority.GetPublicKey());
            }
            catch (Exception exception)
                when (exception is GeneralSecurityException
                    || exception is IOException
                    || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    "Stored leaf certificate validation failed.",
                    exception);
            }

            byte[] canonicalDer = leaf.GetEncoded();
            byte[] spkiSha256 = ComputeSubjectPublicKeyInfoSha256(
                leaf.GetPublicKey());
            byte[] expectedSpkiSha256 =
                entry.GetSubjectPublicKeyInfoSha256();
            try
            {
                string serialHex = leaf.SerialNumber.ToString(16)
                    .PadLeft(32, '0')
                    .ToUpperInvariant();
                PkiSerialNumber authoritySerial;
                bool[] keyUsage = leaf.GetKeyUsage();
                IList<DerObjectIdentifier> extendedKeyUsage =
                    leaf.GetExtendedKeyUsage();
                bool exactKeyUsage = keyUsage != null
                    && keyUsage.Length > 0
                    && keyUsage[0]
                    && !keyUsage.Skip(1).Any(value => value);
                bool exactExtendedKeyUsage = extendedKeyUsage != null
                    && extendedKeyUsage.Count == 1
                    && extendedKeyUsage[0].Equals(
                        KeyPurposeID.id_kp_serverAuth);
                DateTime notBeforeUtc = AsUtc(leaf.NotBefore);
                DateTime notAfterUtc = AsUtc(leaf.NotAfter);
                if (!AreEqual(canonicalDer, leafCertificateDer)
                    || leaf.Version != 3
                    || !StringComparer.Ordinal.Equals(
                        leaf.SigAlgOid,
                        PkcsObjectIdentifiers.Sha256WithRsaEncryption.Id)
                    || !HasExactExtensions(
                        leaf,
                        new[]
                        {
                            X509Extensions.BasicConstraints.Id,
                            X509Extensions.KeyUsage.Id
                        },
                        new[]
                        {
                            X509Extensions.ExtendedKeyUsage.Id,
                            X509Extensions.SubjectAlternativeName.Id,
                            X509Extensions.SubjectKeyIdentifier.Id,
                            X509Extensions.AuthorityKeyIdentifier.Id,
                            X509Extensions.CrlDistributionPoints.Id
                        })
                    || !StringComparer.Ordinal.Equals(
                        serialHex,
                        entry.SerialNumber.Hex)
                    || !PkiSerialNumber.TryCreate(
                        authority.SerialNumber,
                        out authoritySerial)
                    || !leaf.IssuerDN.Equivalent(authority.SubjectDN)
                    || !leaf.SubjectDN.Equivalent(
                        new X509Name(
                            "CN="
                            + entry.ServiceIdentity.ServiceHostName))
                    || leaf.GetBasicConstraints() != -1
                    || !exactKeyUsage
                    || !exactExtendedKeyUsage
                    || notAfterUtc <= notBeforeUtc
                    || notAfterUtc > notBeforeUtc.AddYears(
                        LeafValidityYears)
                    || notBeforeUtc != entry.NotBeforeUtc
                    || notAfterUtc != entry.NotAfterUtc
                    || !AreEqual(spkiSha256, expectedSpkiSha256))
                {
                    throw new InvalidDataException(
                        "Stored leaf certificate fields do not match the ledger.");
                }

                ValidateIssuedServiceIdentity(
                    leaf,
                    entry.ServiceIdentity);
                ValidateCrlDistributionPoints(
                    leaf,
                    directoryIdentity,
                    authoritySerial.Hex);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "Stored leaf certificate service identity is invalid.",
                    exception);
            }
            finally
            {
                Array.Clear(canonicalDer, 0, canonicalDer.Length);
                Array.Clear(spkiSha256, 0, spkiSha256.Length);
                Array.Clear(
                    expectedSpkiSha256,
                    0,
                    expectedSpkiSha256.Length);
            }
        }

        private static void ValidateCrlDistributionPoints(
            X509Certificate certificate,
            DirectoryEndpointIdentity directoryIdentity,
            string issuerCaSerialNumber)
        {
            CrlDistPoint distributionPoints;
            try
            {
                distributionPoints = CrlDistPoint.GetInstance(
                    certificate.GetExtensionParsedValue(
                        X509Extensions.CrlDistributionPoints));
            }
            catch (ArgumentException exception)
            {
                throw new CryptographicException(
                    "The issued service certificate CRL distribution points are invalid.",
                    exception);
            }

            if (distributionPoints == null)
            {
                throw new CryptographicException(
                    "The issued service certificate is missing CRL distribution points.");
            }

            DistributionPoint[] points =
                distributionPoints.GetDistributionPoints();
            if (points.Length != 1
                || points[0].DistributionPointName == null)
            {
                throw new CryptographicException(
                    "The issued service certificate must contain one CRL distribution point.");
            }

            GeneralName[] names;
            try
            {
                names = GeneralNames.GetInstance(
                        points[0].DistributionPointName.Name)
                    .GetNames();
            }
            catch (ArgumentException exception)
            {
                throw new CryptographicException(
                    "The issued service certificate CRL URI set is invalid.",
                    exception);
            }

            if (names.Length != 2
                || names.Any(name =>
                    name.TagNo
                        != GeneralName.UniformResourceIdentifier))
            {
                throw new CryptographicException(
                    "The issued service certificate must contain the exact CRL URI pair.");
            }

            var uris = new HashSet<string>(StringComparer.Ordinal);
            foreach (GeneralName name in names)
            {
                uris.Add(DerIA5String.GetInstance(name.Name).GetString());
            }

            if (uris.Count != 2)
            {
                throw new CryptographicException(
                    "The issued service certificate contains duplicate CRL URIs.");
            }

            if (directoryIdentity == null)
            {
                if (uris.Any(value => !IsCanonicalCrlUri(
                        value,
                        issuerCaSerialNumber)))
                {
                    throw new CryptographicException(
                        "The issued service certificate CRL URI is not canonical.");
                }

                return;
            }

            string expectedDns = "https://"
                + directoryIdentity.DirectoryHostName
                + ":21000"
                + IssuerCrlRelativePathPrefix
                + issuerCaSerialNumber;
            string expectedIpv4 = "https://"
                + directoryIdentity.DirectoryIpv4Address
                + ":21000"
                + IssuerCrlRelativePathPrefix
                + issuerCaSerialNumber;
            if (!uris.SetEquals(new[] { expectedDns, expectedIpv4 }))
            {
                throw new CryptographicException(
                    "The issued service certificate CRL URI pair does not match config.xml.");
            }
        }

        private static bool IsCanonicalCrlUri(
            string value,
            string issuerCaSerialNumber)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && StringComparer.Ordinal.Equals(uri.Scheme, "https")
                && uri.Port == HttpsPort
                && StringComparer.Ordinal.Equals(
                    uri.AbsolutePath,
                    IssuerCrlRelativePathPrefix
                        + issuerCaSerialNumber)
                && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment)
                && StringComparer.Ordinal.Equals(uri.AbsoluteUri, value);
        }

        private static bool HasExactExtensions(
            X509Certificate certificate,
            IEnumerable<string> expectedCritical,
            IEnumerable<string> expectedNonCritical)
        {
            ISet<string> critical = certificate.GetCriticalExtensionOids();
            ISet<string> nonCritical =
                certificate.GetNonCriticalExtensionOids();
            return critical != null
                && nonCritical != null
                && critical.SetEquals(expectedCritical)
                && nonCritical.SetEquals(expectedNonCritical);
        }
    }
}
