using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DEEPAi.ServiceDirectory.Domain;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public sealed class DirectoryCertificateInstallationResult
    {
        internal DirectoryCertificateInstallationResult(
            string thumbprint,
            string serialNumber,
            DateTime notAfterUtc)
        {
            Thumbprint = thumbprint;
            SerialNumber = serialNumber;
            NotAfterUtc = notAfterUtc;
        }

        public string Thumbprint { get; }

        public string SerialNumber { get; }

        public DateTime NotAfterUtc { get; }
    }

    internal static class DirectoryCertificateStore
    {
        private const string CertificateFriendlyName =
            "DEEPAi Service Directory HTTPS";
        private const string Pkcs12Alias = "service-directory-https";

        internal static DirectoryCertificateInstallationResult Install(
            IssuedCertificateArtifact issued,
            byte[] caCertificateDer,
            DirectoryEndpointIdentity identity,
            DateTime utcNow)
        {
            if (issued == null)
            {
                throw new ArgumentNullException(nameof(issued));
            }

            if (caCertificateDer == null || caCertificateDer.Length == 0)
            {
                throw new ArgumentException(
                    "The site CA certificate is required.",
                    nameof(caCertificateDer));
            }

            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            byte[] certificateDer = null;
            byte[] privateKeyPkcs8 = null;
            byte[] pkcs12 = null;
            X509Certificate2 imported = null;
            string privateKeyPath = null;
            string thumbprint = null;
            bool added = false;
            try
            {
                certificateDer = issued.GetCertificateDer();
                privateKeyPkcs8 = issued.ExportPrivateKeyPkcs8();
                SiteCertificateAuthority.ValidateDirectoryLeaf(
                    certificateDer,
                    caCertificateDer,
                    identity,
                    utcNow);
                pkcs12 = BuildPkcs12(
                    certificateDer,
                    caCertificateDer,
                    privateKeyPkcs8);
                imported = new X509Certificate2(
                    pkcs12,
                    string.Empty,
                    X509KeyStorageFlags.MachineKeySet
                        | X509KeyStorageFlags.PersistKeySet);
                if (!imported.HasPrivateKey
                    || !ByteArraysEqual(imported.RawData, certificateDer))
                {
                    throw new CryptographicException(
                        "The imported Directory certificate does not match the issued leaf.");
                }

                imported.FriendlyName = CertificateFriendlyName;
                thumbprint = NormalizeThumbprint(imported.Thumbprint);
                privateKeyPath = DirectoryPrivateKeyAccessPolicy.GetPath(
                    imported);
                DirectoryPrivateKeyAccessPolicy.Apply(privateKeyPath);

                using (var store = OpenMachineStore(OpenFlags.ReadWrite))
                {
                    X509Certificate2Collection existing = store.Certificates.Find(
                        X509FindType.FindByThumbprint,
                        thumbprint,
                        false);
                    if (existing.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "The issued Directory certificate thumbprint already exists in LocalMachine\\My.");
                    }

                    store.Add(imported);
                    added = true;
                }

                VerifyInstalled(
                    thumbprint,
                    certificateDer,
                    caCertificateDer,
                    identity,
                    utcNow,
                    privateKeyPath);
                return new DirectoryCertificateInstallationResult(
                    thumbprint,
                    issued.SerialNumber.Hex,
                    issued.NotAfterUtc);
            }
            catch (Exception installationFailure)
            {
                var cleanupFailures = new System.Collections.Generic.List<Exception>();
                if (imported != null)
                {
                    imported.Dispose();
                    imported = null;
                }

                if (added && thumbprint != null)
                {
                    try
                    {
                        RemoveByThumbprintWithoutValidation(thumbprint);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }

                if (privateKeyPath != null)
                {
                    try
                    {
                        DirectoryPrivateKeyAccessPolicy.Delete(
                            privateKeyPath);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }

                if (cleanupFailures.Count != 0)
                {
                    cleanupFailures.Insert(0, installationFailure);
                    throw new AggregateException(
                        "Directory certificate installation failed and cleanup was incomplete.",
                        cleanupFailures);
                }

                throw;
            }
            finally
            {
                if (imported != null)
                {
                    imported.Dispose();
                }

                Clear(certificateDer);
                Clear(privateKeyPkcs8);
                Clear(pkcs12);
            }
        }

        internal static void Remove(
            string thumbprint,
            byte[] caCertificateDer,
            DirectoryEndpointIdentity identity,
            DateTime utcNow)
        {
            string canonicalThumbprint = NormalizeThumbprint(thumbprint);
            EnsureUtc(utcNow, nameof(utcNow));
            string privateKeyPath;
            using (var store = OpenMachineStore(OpenFlags.ReadWrite))
            {
                X509Certificate2Collection matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    canonicalThumbprint,
                    false);
                if (matches.Count == 0)
                {
                    return;
                }

                if (matches.Count != 1)
                {
                    throw new InvalidDataException(
                        "LocalMachine\\My contains duplicate Directory certificate thumbprints.");
                }

                using (X509Certificate2 certificate = matches[0])
                {
                    if (!certificate.HasPrivateKey)
                    {
                        throw new InvalidDataException(
                            "The selected Directory certificate has no private key.");
                    }

                    SiteCertificateAuthority.ValidateDirectoryLeaf(
                        certificate.RawData,
                        caCertificateDer,
                        identity,
                        utcNow,
                        false);
                    privateKeyPath = DirectoryPrivateKeyAccessPolicy.GetPath(
                        certificate);
                    store.Remove(certificate);
                }
            }

            DirectoryPrivateKeyAccessPolicy.Delete(privateKeyPath);
            using (var verificationStore = OpenMachineStore(OpenFlags.ReadOnly))
            {
                if (verificationStore.Certificates.Find(
                        X509FindType.FindByThumbprint,
                        canonicalThumbprint,
                        false).Count != 0)
                {
                    throw new IOException(
                        "The Directory certificate remains in LocalMachine\\My after removal.");
                }
            }
        }

        internal static DirectoryCertificateInstallationResult
            ReadInstalled(
                string thumbprint,
                byte[] caCertificateDer,
                DirectoryEndpointIdentity identity,
                DateTime utcNow)
        {
            string canonicalThumbprint = NormalizeThumbprint(thumbprint);
            EnsureUtc(utcNow, nameof(utcNow));
            using (var store = OpenMachineStore(OpenFlags.ReadOnly))
            {
                X509Certificate2Collection matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    canonicalThumbprint,
                    false);
                if (matches.Count != 1)
                {
                    throw new InvalidDataException(
                        "The bound Directory certificate was not found exactly once.");
                }

                using (X509Certificate2 certificate = matches[0])
                {
                    if (!certificate.HasPrivateKey)
                    {
                        throw new InvalidDataException(
                            "The bound Directory certificate has no private key.");
                    }

                    SiteCertificateAuthority.ValidateDirectoryLeaf(
                        certificate.RawData,
                        caCertificateDer,
                        identity,
                        utcNow);
                    DirectoryPrivateKeyAccessPolicy.Verify(
                        DirectoryPrivateKeyAccessPolicy.GetPath(
                            certificate));
                    string serial = certificate.SerialNumber
                        .PadLeft(32, '0')
                        .ToUpperInvariant();
                    if (serial.Length != 32
                        || serial.Any(character =>
                            (character < '0' || character > '9')
                            && (character < 'A' || character > 'F')))
                    {
                        throw new InvalidDataException(
                            "The bound Directory certificate serial is invalid.");
                    }

                    return new DirectoryCertificateInstallationResult(
                        canonicalThumbprint,
                        serial,
                        certificate.NotAfter.ToUniversalTime());
                }
            }
        }

        internal static string NormalizeThumbprint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "A certificate thumbprint is required.",
                    nameof(value));
            }

            string canonical = new string(value
                .Where(character => character != ' ')
                .ToArray())
                .ToUpperInvariant();
            if (canonical.Length != 40
                || canonical.Any(character =>
                    (character < '0' || character > '9')
                    && (character < 'A' || character > 'F')))
            {
                throw new ArgumentException(
                    "The certificate thumbprint must be canonical SHA-1 hexadecimal.",
                    nameof(value));
            }

            return canonical;
        }

        private static byte[] BuildPkcs12(
            byte[] certificateDer,
            byte[] caCertificateDer,
            byte[] privateKeyPkcs8)
        {
            var parser = new Org.BouncyCastle.X509.X509CertificateParser();
            Org.BouncyCastle.X509.X509Certificate leaf =
                parser.ReadCertificate(certificateDer);
            Org.BouncyCastle.X509.X509Certificate authority =
                parser.ReadCertificate(caCertificateDer);
            AsymmetricKeyParameter privateKey =
                PrivateKeyFactory.CreateKey(privateKeyPkcs8);
            if (!(privateKey is RsaPrivateCrtKeyParameters))
            {
                throw new CryptographicException(
                    "The Directory certificate private key must be RSA.");
            }

            Pkcs12Store store = new Pkcs12StoreBuilder().Build();
            store.SetKeyEntry(
                Pkcs12Alias,
                new AsymmetricKeyEntry(privateKey),
                new[]
                {
                    new X509CertificateEntry(leaf),
                    new X509CertificateEntry(authority)
                });
            using (var stream = new MemoryStream())
            {
                store.Save(stream, new char[0], new SecureRandom());
                return stream.ToArray();
            }
        }

        private static void VerifyInstalled(
            string thumbprint,
            byte[] certificateDer,
            byte[] caCertificateDer,
            DirectoryEndpointIdentity identity,
            DateTime utcNow,
            string expectedPrivateKeyPath)
        {
            using (var store = OpenMachineStore(OpenFlags.ReadOnly))
            {
                X509Certificate2Collection matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    thumbprint,
                    false);
                if (matches.Count != 1)
                {
                    throw new CryptographicException(
                        "The installed Directory certificate could not be read back exactly once.");
                }

                using (X509Certificate2 certificate = matches[0])
                {
                    if (!certificate.HasPrivateKey
                        || !ByteArraysEqual(certificate.RawData, certificateDer)
                        || !StringComparer.OrdinalIgnoreCase.Equals(
                            DirectoryPrivateKeyAccessPolicy.GetPath(
                                certificate),
                            expectedPrivateKeyPath))
                    {
                        throw new CryptographicException(
                            "The installed Directory certificate or private key does not match the issued artifact.");
                    }

                    SiteCertificateAuthority.ValidateDirectoryLeaf(
                        certificate.RawData,
                        caCertificateDer,
                        identity,
                        utcNow);
                }
            }

            DirectoryPrivateKeyAccessPolicy.Verify(expectedPrivateKeyPath);
        }

        private static void RemoveByThumbprintWithoutValidation(
            string thumbprint)
        {
            using (var store = OpenMachineStore(OpenFlags.ReadWrite))
            {
                foreach (X509Certificate2 certificate in store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    thumbprint,
                    false))
                {
                    store.Remove(certificate);
                    certificate.Dispose();
                }
            }
        }

        private static X509Store OpenMachineStore(OpenFlags flags)
        {
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            try
            {
                store.Open(flags);
                return store;
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
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

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Directory certificate time must use UTC.",
                    parameterName);
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
