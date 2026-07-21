using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public static class DirectoryHttpsEndpointValidator
    {
        private const int Sha1HashBytes = 20;
        private const int PrivateKeyProofBytes = 32;
        private const int MaximumCaCertificateBytes = 128 * 1024;

        private static readonly Guid InstallerApplicationId =
            new Guid("B44C6547-15D5-421A-88D7-3D2293BEE48C");

        public static void Validate(
            string stateDirectoryPath,
            ServiceDirectoryConfiguration configuration,
            ServiceDirectoryListenerAddress listenerAddress,
            DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath))
            {
                throw new ArgumentException(
                    "The state directory path is required.",
                    nameof(stateDirectoryPath));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (listenerAddress == null)
            {
                throw new ArgumentNullException(nameof(listenerAddress));
            }

            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Directory HTTPS validation time must be UTC.",
                    nameof(utcNow));
            }

            if (!StringComparer.Ordinal.Equals(
                    configuration.ListenAddress,
                    listenerAddress.CanonicalAddress)
                || !StringComparer.Ordinal.Equals(
                    configuration.DirectoryIpv4Address,
                    listenerAddress.CanonicalAddress))
            {
                throw new InvalidDataException(
                    "The Directory HTTPS listener and config identity do not match.");
            }

            IPAddress address;
            if (!IPAddress.TryParse(
                    listenerAddress.CanonicalAddress,
                    out address))
            {
                throw new InvalidDataException(
                    "The Directory HTTPS listener address is invalid.");
            }

            HttpSysSslBinding binding = HttpSysSslBindingReader.Read(
                address,
                ServiceDirectoryListenerAddress.Port);
            byte[] certificateHash = binding.CertificateHash;
            try
            {
                if (certificateHash.Length != Sha1HashBytes
                    || binding.ApplicationId != InstallerApplicationId
                    || !StringComparer.OrdinalIgnoreCase.Equals(
                        binding.CertificateStoreName,
                        "MY")
                    || binding.CertificateCheckMode != 0
                    || binding.RevocationFreshnessTime != 0
                    || binding.RevocationUrlRetrievalTimeout != 0
                    || binding.Flags != 0)
                {
                    throw new InvalidDataException(
                        "The HTTP.sys HTTPS binding is not in the exact installer-owned state.");
                }

                byte[] caCertificateDer = ReadCaCertificate(
                    stateDirectoryPath);
                try
                {
                    ValidateCertificate(
                        binding.GetThumbprint(),
                        certificateHash,
                        caCertificateDer,
                        configuration,
                        utcNow);
                }
                finally
                {
                    Array.Clear(
                        caCertificateDer,
                        0,
                        caCertificateDer.Length);
                }
            }
            finally
            {
                Array.Clear(
                    certificateHash,
                    0,
                    certificateHash.Length);
            }
        }

        private static void ValidateCertificate(
            string thumbprint,
            byte[] expectedCertificateHash,
            byte[] caCertificateDer,
            ServiceDirectoryConfiguration configuration,
            DateTime utcNow)
        {
            using (var store = new X509Store(
                StoreName.My,
                StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    thumbprint,
                    false);
                if (matches.Count != 1)
                {
                    throw new InvalidDataException(
                        "The HTTP.sys Directory certificate was not found exactly once in LocalMachine\\My.");
                }

                using (X509Certificate2 certificate = matches[0])
                {
                    if (!certificate.HasPrivateKey
                        || !ByteArraysEqual(
                            certificate.GetCertHash(),
                            expectedCertificateHash))
                    {
                        throw new InvalidDataException(
                            "The HTTP.sys Directory certificate or private key is unavailable.");
                    }

                    SiteCertificateAuthority.ValidateDirectoryLeaf(
                        certificate.RawData,
                        caCertificateDer,
                        configuration.DirectoryEndpointIdentity,
                        utcNow);
                    VerifyPrivateKey(certificate);
                    DirectoryPrivateKeyAccessPolicy.Verify(
                        DirectoryPrivateKeyAccessPolicy.GetPath(
                            certificate));
                }
            }
        }

        private static void VerifyPrivateKey(
            X509Certificate2 certificate)
        {
            var proof = new byte[PrivateKeyProofBytes];
            byte[] signature = null;
            using (var random = RandomNumberGenerator.Create())
            using (RSA privateKey = certificate.GetRSAPrivateKey())
            using (RSA publicKey = certificate.GetRSAPublicKey())
            {
                try
                {
                    if (privateKey == null || publicKey == null)
                    {
                        throw new CryptographicException(
                            "The Directory RSA key pair is unavailable.");
                    }

                    random.GetBytes(proof);
                    signature = privateKey.SignData(
                        proof,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);
                    if (!publicKey.VerifyData(
                            proof,
                            signature,
                            HashAlgorithmName.SHA256,
                            RSASignaturePadding.Pkcs1))
                    {
                        throw new CryptographicException(
                            "The Directory certificate and private key do not match.");
                    }
                }
                finally
                {
                    Array.Clear(proof, 0, proof.Length);
                    if (signature != null)
                    {
                        Array.Clear(signature, 0, signature.Length);
                    }
                }
            }
        }

        private static byte[] ReadCaCertificate(
            string stateDirectoryPath)
        {
            var writer = new AtomicFileWriter(
                new StateStoragePathPolicy(stateDirectoryPath));
            return writer.Read(
                StateFileTarget.CaCertificate,
                MaximumCaCertificateBytes);
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
    }
}
