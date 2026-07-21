using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class SiteCertificateAuthority
    {
        internal const int CaKeySizeBits = 3072;
        internal const int DirectoryLeafKeySizeBits = 3072;
        internal const int MaximumCaValidityYears = 20;
        internal const int LeafValidityYears = 1;
        internal const int ActivationBackdateMinutes = 5;
        internal const string CrlRelativePath = "/pki/crl";
        internal const string IssuerCrlRelativePathPrefix = "/pki/crl/";
        internal const int HttpsPort = 21000;

        private const string CaSignatureAlgorithm = "SHA256WITHRSA";

        private readonly AsymmetricCipherKeyPair _keyPair;
        private readonly X509Certificate _certificate;

        private SiteCertificateAuthority(
            Guid siteId,
            AsymmetricCipherKeyPair keyPair,
            X509Certificate certificate,
            CertificateSerialNumber serialNumber)
        {
            SiteId = siteId;
            _keyPair = keyPair ?? throw new ArgumentNullException(nameof(keyPair));
            _certificate = certificate
                ?? throw new ArgumentNullException(nameof(certificate));
            SerialNumber = serialNumber;
        }

        internal Guid SiteId { get; }

        internal CertificateSerialNumber SerialNumber { get; }

        internal DateTime NotBeforeUtc => AsUtc(_certificate.NotBefore);

        internal DateTime NotAfterUtc => AsUtc(_certificate.NotAfter);

        internal string IssuerCrlRelativePath =>
            GetIssuerCrlRelativePath(SerialNumber);

        internal static string GetIssuerCrlRelativePath(
            CertificateSerialNumber caSerialNumber)
        {
            if (!caSerialNumber.IsValid)
            {
                throw new ArgumentException(
                    "CA serial number must be valid.",
                    nameof(caSerialNumber));
            }

            return IssuerCrlRelativePathPrefix + caSerialNumber.Hex;
        }

        internal static SiteCertificateAuthority Create(
            Guid siteId,
            DateTime utcNow,
            SecureRandom random,
            Func<string, bool> isSerialReserved = null)
        {
            EnsureSiteId(siteId);
            EnsureUtc(utcNow, nameof(utcNow));
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            AsymmetricCipherKeyPair keyPair = GenerateRsaKeyPair(
                CaKeySizeBits,
                random);
            PkiSerialNumber serialNumber = PkiSerialNumber.CreateRandom(
                random,
                isSerialReserved);
            DateTime notBeforeUtc = utcNow.AddMinutes(-ActivationBackdateMinutes);
            DateTime notAfterUtc = notBeforeUtc.AddYears(
                MaximumCaValidityYears);
            var subject = new X509Name(
                "CN=DEEPAi Service Directory Site CA "
                + siteId.ToString("D").ToLowerInvariant());

            var generator = new X509V3CertificateGenerator();
            generator.SetSerialNumber(serialNumber.Value);
            generator.SetIssuerDN(subject);
            generator.SetSubjectDN(subject);
            generator.SetNotBefore(notBeforeUtc);
            generator.SetNotAfter(notAfterUtc);
            generator.SetPublicKey(keyPair.Public);
            generator.AddExtension(
                X509Extensions.BasicConstraints,
                true,
                new BasicConstraints(0));
            generator.AddExtension(
                X509Extensions.KeyUsage,
                true,
                new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));
            generator.AddExtension(
                X509Extensions.SubjectKeyIdentifier,
                false,
                X509ExtensionUtilities.CreateSubjectKeyIdentifier(
                    keyPair.Public));
            generator.AddExtension(
                X509Extensions.AuthorityKeyIdentifier,
                false,
                X509ExtensionUtilities.CreateAuthorityKeyIdentifier(
                    keyPair.Public));

            var signatureFactory = new Asn1SignatureFactory(
                CaSignatureAlgorithm,
                keyPair.Private,
                random);
            X509Certificate certificate = generator.Generate(signatureFactory);
            certificate.Verify(keyPair.Public);

            return new SiteCertificateAuthority(
                siteId,
                keyPair,
                certificate,
                serialNumber.ToLedgerSerialNumber());
        }

        internal static SiteCertificateAuthority Restore(
            Guid siteId,
            byte[] certificateDer,
            byte[] privateKeyPkcs8,
            DateTime utcNow)
        {
            EnsureSiteId(siteId);
            EnsureUtc(utcNow, nameof(utcNow));
            if (certificateDer == null || certificateDer.Length == 0)
            {
                throw new ArgumentException(
                    "CA certificate DER must not be empty.",
                    nameof(certificateDer));
            }

            if (privateKeyPkcs8 == null || privateKeyPkcs8.Length == 0)
            {
                throw new ArgumentException(
                    "CA private key PKCS#8 must not be empty.",
                    nameof(privateKeyPkcs8));
            }

            var certificate = new X509Certificate(certificateDer);
            var privateKey = PrivateKeyFactory.CreateKey(privateKeyPkcs8)
                as RsaPrivateCrtKeyParameters;
            if (privateKey == null)
            {
                throw new CryptographicException("The site CA private key must be RSA.");
            }

            var publicKey = new RsaKeyParameters(
                false,
                privateKey.Modulus,
                privateKey.PublicExponent);
            ValidateCaCertificate(siteId, certificate, publicKey, utcNow);

            PkiSerialNumber restoredSerial;
            if (!PkiSerialNumber.TryCreate(
                    certificate.SerialNumber,
                    out restoredSerial))
            {
                throw new CryptographicException(
                    "The restored site CA serial does not use the canonical 16-byte positive format.");
            }

            return new SiteCertificateAuthority(
                siteId,
                new AsymmetricCipherKeyPair(publicKey, privateKey),
                certificate,
                restoredSerial.ToLedgerSerialNumber());
        }

        internal byte[] GetCertificateDer()
        {
            return _certificate.GetEncoded();
        }

        internal byte[] GetSpkiSha256()
        {
            return ComputeSubjectPublicKeyInfoSha256(_keyPair.Public);
        }

        // The caller owns the returned plaintext PKCS#8 buffer and must place it
        // into the dedicated protected store, then clear it in a finally block.
        internal byte[] ExportPrivateKeyPkcs8()
        {
            return PrivateKeyInfoFactory
                .CreatePrivateKeyInfo(_keyPair.Private)
                .GetDerEncoded();
        }

        internal IssuedCertificateArtifact CreateDirectoryLeaf(
            DirectoryEndpointIdentity identity,
            PkiSerialNumber serialNumber,
            DateTime utcNow,
            SecureRandom random)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            AsymmetricCipherKeyPair leafKeyPair = GenerateRsaKeyPair(
                DirectoryLeafKeySizeBits,
                random);
            byte[] privateKeyPkcs8 = null;
            try
            {
                IssuedCertificateArtifact issued = IssueServerCertificate(
                    identity.DirectoryHostName,
                    identity.DirectoryIpv4Address,
                    leafKeyPair.Public,
                    serialNumber,
                    utcNow,
                    random,
                    null,
                    identity);
                privateKeyPkcs8 = PrivateKeyInfoFactory
                    .CreatePrivateKeyInfo(leafKeyPair.Private)
                    .GetDerEncoded();
                byte[] certificateDer = issued.GetCertificateDer();
                try
                {
                    return new IssuedCertificateArtifact(
                        issued.SerialNumber,
                        issued.NotBeforeUtc,
                        issued.NotAfterUtc,
                        certificateDer,
                        privateKeyPkcs8);
                }
                finally
                {
                    Array.Clear(certificateDer, 0, certificateDer.Length);
                    issued.Dispose();
                }
            }
            finally
            {
                if (privateKeyPkcs8 != null)
                {
                    Array.Clear(privateKeyPkcs8, 0, privateKeyPkcs8.Length);
                }
            }
        }

        internal IssuedCertificateArtifact IssueServiceLeaf(
            ValidatedCertificateSigningRequest signingRequest,
            DirectoryEndpointIdentity directoryIdentity,
            PkiSerialNumber serialNumber,
            DateTime utcNow,
            SecureRandom random)
        {
            if (signingRequest == null)
            {
                throw new ArgumentNullException(nameof(signingRequest));
            }

            if (directoryIdentity == null)
            {
                throw new ArgumentNullException(nameof(directoryIdentity));
            }

            return IssueServerCertificate(
                signingRequest.Identity.ServiceHostName,
                signingRequest.Identity.ServiceIpv4Address,
                signingRequest.PublicKey,
                serialNumber,
                utcNow,
                random,
                signingRequest.Identity,
                directoryIdentity);
        }

        internal CertificateRevocationListArtifact CreateRevocationList(
            ulong crlNumber,
            IEnumerable<RevokedCertificateEntry> revokedCertificates,
            DateTime thisUpdateUtc,
            DateTime nextUpdateUtc,
            SecureRandom random)
        {
            if (crlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(crlNumber));
            }

            if (revokedCertificates == null)
            {
                throw new ArgumentNullException(nameof(revokedCertificates));
            }

            EnsureUtc(thisUpdateUtc, nameof(thisUpdateUtc));
            EnsureUtc(nextUpdateUtc, nameof(nextUpdateUtc));
            if (nextUpdateUtc <= thisUpdateUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(nextUpdateUtc));
            }

            if (thisUpdateUtc < NotBeforeUtc || thisUpdateUtc > NotAfterUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(thisUpdateUtc),
                    "CRL thisUpdate must stay within the site CA validity period.");
            }

            if (nextUpdateUtc > NotAfterUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nextUpdateUtc),
                    "CRL nextUpdate must not exceed the site CA expiry.");
            }

            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            RevokedCertificateEntry[] entries = revokedCertificates.ToArray();
            if (entries.Any(entry => entry == null))
            {
                throw new ArgumentException(
                    "Revoked certificate entries cannot contain null.",
                    nameof(revokedCertificates));
            }

            if (entries.Any(entry => entry.RevokedUtc > thisUpdateUtc))
            {
                throw new ArgumentException(
                    "A CRL cannot contain a revocation time after thisUpdate.",
                    nameof(revokedCertificates));
            }

            entries = entries
                .OrderBy(item => item.SerialNumber.Hex, StringComparer.Ordinal)
                .ToArray();
            var seenSerials = new HashSet<string>(StringComparer.Ordinal);
            var generator = new X509V2CrlGenerator();
            generator.SetIssuerDN(_certificate.SubjectDN);
            generator.SetThisUpdate(thisUpdateUtc);
            generator.SetNextUpdate(nextUpdateUtc);
            foreach (RevokedCertificateEntry entry in entries)
            {
                if (!seenSerials.Add(entry.SerialNumber.Hex))
                {
                    throw new ArgumentException(
                        "Revoked certificate entries contain a duplicate serial.",
                        nameof(revokedCertificates));
                }

                generator.AddCrlEntry(
                    entry.SerialNumber.Value,
                    entry.RevokedUtc,
                    (int)entry.Reason);
            }

            generator.AddExtension(
                X509Extensions.CrlNumber,
                false,
                new CrlNumber(new BigInteger(
                    crlNumber.ToString(CultureInfo.InvariantCulture))));
            generator.AddExtension(
                X509Extensions.AuthorityKeyIdentifier,
                false,
                X509ExtensionUtilities.CreateAuthorityKeyIdentifier(
                    _certificate));

            var signatureFactory = new Asn1SignatureFactory(
                CaSignatureAlgorithm,
                _keyPair.Private,
                random);
            X509Crl crl = generator.Generate(signatureFactory);
            crl.Verify(_keyPair.Public);

            byte[] derBytes = crl.GetEncoded();
            byte[] digest = null;
            try
            {
                using (SHA256 sha256 = SHA256.Create())
                {
                    digest = sha256.ComputeHash(derBytes);
                }

                return new CertificateRevocationListArtifact(
                    crlNumber,
                    thisUpdateUtc,
                    nextUpdateUtc,
                    derBytes,
                    digest);
            }
            finally
            {
                Array.Clear(derBytes, 0, derBytes.Length);
                if (digest != null)
                {
                    Array.Clear(digest, 0, digest.Length);
                }
            }
        }

        private IssuedCertificateArtifact IssueServerCertificate(
            string hostName,
            string ipv4Address,
            AsymmetricKeyParameter publicKey,
            PkiSerialNumber serialNumber,
            DateTime utcNow,
            SecureRandom random,
            ServiceEndpointIdentity serviceIdentity,
            DirectoryEndpointIdentity directoryIdentity)
        {
            if (serialNumber == null)
            {
                throw new ArgumentNullException(nameof(serialNumber));
            }

            if (serialNumber.ToLedgerSerialNumber() == SerialNumber)
            {
                throw new ArgumentException(
                    "A leaf certificate cannot reuse the site CA serial number.",
                    nameof(serialNumber));
            }

            if (publicKey == null || publicKey.IsPrivate)
            {
                throw new ArgumentException(
                    "A public key is required for certificate issuance.",
                    nameof(publicKey));
            }

            if (directoryIdentity == null)
            {
                throw new ArgumentNullException(nameof(directoryIdentity));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            DateTime notBeforeUtc = utcNow.AddMinutes(-ActivationBackdateMinutes);
            DateTime notAfterUtc = notBeforeUtc.AddYears(LeafValidityYears);
            if (notAfterUtc > NotAfterUtc)
            {
                throw new InvalidOperationException(
                    "The site CA expires before the requested leaf validity period.");
            }

            var generator = new X509V3CertificateGenerator();
            generator.SetSerialNumber(serialNumber.Value);
            generator.SetIssuerDN(_certificate.SubjectDN);
            generator.SetSubjectDN(new X509Name("CN=" + hostName));
            generator.SetNotBefore(notBeforeUtc);
            generator.SetNotAfter(notAfterUtc);
            generator.SetPublicKey(publicKey);
            generator.AddExtension(
                X509Extensions.BasicConstraints,
                true,
                new BasicConstraints(false));
            generator.AddExtension(
                X509Extensions.KeyUsage,
                true,
                new KeyUsage(KeyUsage.DigitalSignature));
            generator.AddExtension(
                X509Extensions.ExtendedKeyUsage,
                false,
                new ExtendedKeyUsage(
                    new[] { KeyPurposeID.id_kp_serverAuth }));
            generator.AddExtension(
                X509Extensions.SubjectAlternativeName,
                false,
                new GeneralNames(new[]
                {
                    new GeneralName(GeneralName.DnsName, hostName),
                    new GeneralName(GeneralName.IPAddress, ipv4Address)
                }));
            generator.AddExtension(
                X509Extensions.SubjectKeyIdentifier,
                false,
                X509ExtensionUtilities.CreateSubjectKeyIdentifier(
                    publicKey));
            generator.AddExtension(
                X509Extensions.AuthorityKeyIdentifier,
                false,
                X509ExtensionUtilities.CreateAuthorityKeyIdentifier(
                    _certificate));
            generator.AddExtension(
                X509Extensions.CrlDistributionPoints,
                false,
                CreateCrlDistributionPoint(directoryIdentity));

            var signatureFactory = new Asn1SignatureFactory(
                CaSignatureAlgorithm,
                _keyPair.Private,
                random);
            X509Certificate certificate = generator.Generate(signatureFactory);
            certificate.Verify(_keyPair.Public);

            if (serviceIdentity != null)
            {
                ValidateIssuedServiceIdentity(certificate, serviceIdentity);
            }

            return new IssuedCertificateArtifact(
                serialNumber,
                notBeforeUtc,
                notAfterUtc,
                certificate.GetEncoded(),
                null);
        }

        private CrlDistPoint CreateCrlDistributionPoint(
            DirectoryEndpointIdentity directoryIdentity)
        {
            var distributionPointName = new DistributionPointName(
                new GeneralNames(new[]
                {
                    new GeneralName(
                        GeneralName.UniformResourceIdentifier,
                        CreateCrlUri(directoryIdentity.DirectoryHostName)),
                    new GeneralName(
                        GeneralName.UniformResourceIdentifier,
                        CreateCrlUri(directoryIdentity.DirectoryIpv4Address))
                }));
            return new CrlDistPoint(new[]
            {
                new DistributionPoint(distributionPointName, null, null)
            });
        }

        private string CreateCrlUri(string host)
        {
            return "https://"
                + host
                + ":"
                + HttpsPort.ToString(CultureInfo.InvariantCulture)
                + IssuerCrlRelativePath;
        }

        private static AsymmetricCipherKeyPair GenerateRsaKeyPair(
            int keySizeBits,
            SecureRandom random)
        {
            var generator = new RsaKeyPairGenerator();
            generator.Init(new RsaKeyGenerationParameters(
                BigInteger.ValueOf(65537),
                random,
                keySizeBits,
                64));
            return generator.GenerateKeyPair();
        }

        private static byte[] ComputeSubjectPublicKeyInfoSha256(
            AsymmetricKeyParameter publicKey)
        {
            byte[] subjectPublicKeyInfo = SubjectPublicKeyInfoFactory
                .CreateSubjectPublicKeyInfo(publicKey)
                .GetDerEncoded();
            try
            {
                using (SHA256 sha256 = SHA256.Create())
                {
                    return sha256.ComputeHash(subjectPublicKeyInfo);
                }
            }
            finally
            {
                Array.Clear(subjectPublicKeyInfo, 0, subjectPublicKeyInfo.Length);
            }
        }

        private static void ValidateCaCertificate(
            Guid siteId,
            X509Certificate certificate,
            AsymmetricKeyParameter publicKey,
            DateTime utcNow)
        {
            var rsaPublicKey = publicKey as RsaKeyParameters;
            if (rsaPublicKey == null
                || rsaPublicKey.IsPrivate
                || rsaPublicKey.Modulus.BitLength != CaKeySizeBits)
            {
                throw new CryptographicException(
                    "The restored site CA must use an RSA 3072-bit key.");
            }

            if (certificate.Version != 3
                || !StringComparer.Ordinal.Equals(
                    certificate.SigAlgOid,
                    PkcsObjectIdentifiers.Sha256WithRsaEncryption.Id))
            {
                throw new CryptographicException(
                    "The restored site CA must use SHA-256 with RSA.");
            }

            if (certificate.GetBasicConstraints() != 0)
            {
                throw new CryptographicException(
                    "The restored site certificate is not a pathLen=0 CA.");
            }

            bool[] keyUsage = certificate.GetKeyUsage();
            if (keyUsage == null
                || keyUsage.Length <= 6
                || !keyUsage[5]
                || !keyUsage[6]
                || keyUsage.Where((value, index) => index != 5 && index != 6)
                    .Any(value => value)
                || !HasExactExtensions(
                    certificate,
                    new[]
                    {
                        X509Extensions.BasicConstraints.Id,
                        X509Extensions.KeyUsage.Id
                    },
                    new[]
                    {
                        X509Extensions.SubjectKeyIdentifier.Id,
                        X509Extensions.AuthorityKeyIdentifier.Id
                    }))
            {
                throw new CryptographicException(
                    "The restored site CA extensions do not match the required profile.");
            }

            if (!certificate.SubjectDN.Equivalent(certificate.IssuerDN))
            {
                throw new CryptographicException(
                    "The restored site CA must be self-issued.");
            }

            var expectedSubject = new X509Name(
                "CN=DEEPAi Service Directory Site CA "
                + siteId.ToString("D").ToLowerInvariant());
            if (!certificate.SubjectDN.Equivalent(expectedSubject))
            {
                throw new CryptographicException(
                    "The restored site CA subject does not match the configured Site ID.");
            }

            DateTime notBeforeUtc = AsUtc(certificate.NotBefore);
            DateTime notAfterUtc = AsUtc(certificate.NotAfter);
            if (notBeforeUtc > utcNow
                || notAfterUtc < utcNow
                || notAfterUtc > notBeforeUtc.AddYears(
                    MaximumCaValidityYears))
            {
                throw new CryptographicException(
                    "The restored site CA validity period is invalid.");
            }

            byte[] certificateSpki = certificate.SubjectPublicKeyInfo.GetDerEncoded();
            byte[] suppliedSpki = SubjectPublicKeyInfoFactory
                .CreateSubjectPublicKeyInfo(publicKey)
                .GetDerEncoded();
            try
            {
                if (!AreEqual(certificateSpki, suppliedSpki))
                {
                    throw new CryptographicException(
                        "The restored site CA certificate and private key do not match.");
                }
            }
            finally
            {
                Array.Clear(certificateSpki, 0, certificateSpki.Length);
                Array.Clear(suppliedSpki, 0, suppliedSpki.Length);
            }

            if (certificate.GetSubjectAlternativeNameExtension() != null)
            {
                throw new CryptographicException(
                    "The site CA certificate must not contain endpoint SAN values.");
            }

            certificate.Verify(publicKey);
        }

        private static void ValidateIssuedServiceIdentity(
            X509Certificate certificate,
            ServiceEndpointIdentity expectedIdentity)
        {
            GeneralNames subjectAlternativeNames =
                certificate.GetSubjectAlternativeNameExtension();
            if (subjectAlternativeNames == null)
            {
                throw new CryptographicException(
                    "The issued service certificate is missing its SAN pair.");
            }

            GeneralName[] names = subjectAlternativeNames.GetNames();
            if (names.Length != 2)
            {
                throw new CryptographicException(
                    "The issued service certificate does not contain the exact SAN pair.");
            }

            bool hasDns = names.Any(name =>
                name.TagNo == GeneralName.DnsName
                && StringComparer.Ordinal.Equals(
                    Org.BouncyCastle.Asn1.DerIA5String
                        .GetInstance(name.Name)
                        .GetString(),
                    expectedIdentity.ServiceHostName));
            bool hasIpv4 = names.Any(name =>
                name.TagNo == GeneralName.IPAddress
                && StringComparer.Ordinal.Equals(
                    FormatIpv4(
                        Org.BouncyCastle.Asn1.Asn1OctetString
                            .GetInstance(name.Name)
                            .GetOctets()),
                    expectedIdentity.ServiceIpv4Address));
            if (!hasDns || !hasIpv4)
            {
                throw new CryptographicException(
                    "The issued service certificate SAN pair does not match the validated request.");
            }
        }

        private static string FormatIpv4(byte[] value)
        {
            if (value == null || value.Length != 4)
            {
                return null;
            }

            return string.Join(".", value[0], value[1], value[2], value[3]);
        }

        private static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static bool AreEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static void EnsureSiteId(Guid siteId)
        {
            if (siteId == Guid.Empty)
            {
                throw new ArgumentException("Site ID must not be empty.", nameof(siteId));
            }
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "PKI timestamps must use DateTimeKind.Utc.",
                    parameterName);
            }
        }
    }
}
