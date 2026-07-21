using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal static class CertificateRenewalProofValidator
    {
        internal const int MaximumTimestampSkewSeconds = 60;

        private const string ServiceIdentityLabel =
            "DPAI-SD-SERVICE-IDENTITY";
        private const string RenewalProofLabel =
            "DPAI-SD-CERTIFICATE-RENEW";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal static bool TryValidate(
            ExternalCertificateRenewalRequest request,
            ServiceDefinition requestedDefinition,
            CertificateLedgerEntry proofCertificate,
            DateTime utcNow)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (requestedDefinition == null)
            {
                throw new ArgumentNullException(nameof(requestedDefinition));
            }

            if (proofCertificate == null)
            {
                throw new ArgumentNullException(nameof(proofCertificate));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            if (request.TimestampUtc.Ticks < utcNow.Ticks
                    - (TimeSpan.TicksPerSecond
                        * MaximumTimestampSkewSeconds)
                || request.TimestampUtc.Ticks > utcNow.Ticks
                    + (TimeSpan.TicksPerSecond
                        * MaximumTimestampSkewSeconds))
            {
                return false;
            }

            byte[] expectedIdentitySha256 = null;
            byte[] suppliedIdentitySha256 = null;
            byte[] certificateSigningRequest = null;
            byte[] csrSha256 = null;
            byte[] nonce = null;
            byte[] proofSignature = null;
            byte[] canonicalProof = null;
            byte[] leafCertificate = null;
            try
            {
                expectedIdentitySha256 = ComputeServiceIdentitySha256(
                    requestedDefinition);
                suppliedIdentitySha256 =
                    request.ServiceIdentitySha256;
                if (!FixedTimeEquals(
                        expectedIdentitySha256,
                        suppliedIdentitySha256))
                {
                    return false;
                }

                certificateSigningRequest =
                    request.CertificateSigningRequest;
                using (SHA256 sha256 = SHA256.Create())
                {
                    csrSha256 = sha256.ComputeHash(
                        certificateSigningRequest);
                }

                nonce = request.Nonce;
                canonicalProof = BuildCanonicalProof(
                    requestedDefinition.ProductCode.Value,
                    proofCertificate.SerialNumber,
                    request.RenewalRequestId,
                    request.TimestampUtc,
                    nonce,
                    csrSha256,
                    suppliedIdentitySha256);
                proofSignature = request.ProofSignature;
                leafCertificate = proofCertificate.GetLeafCertificate();
                return VerifySignature(
                    leafCertificate,
                    canonicalProof,
                    proofSignature);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is InvalidOperationException
                || exception is CryptographicException
                || exception is CryptoException
                || exception is GeneralSecurityException)
            {
                return false;
            }
            finally
            {
                Clear(expectedIdentitySha256);
                Clear(suppliedIdentitySha256);
                Clear(certificateSigningRequest);
                Clear(csrSha256);
                Clear(nonce);
                Clear(proofSignature);
                Clear(canonicalProof);
                Clear(leafCertificate);
            }
        }

        internal static byte[] ComputeServiceIdentitySha256(
            ServiceDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            string canonical = string.Concat(
                ServiceIdentityLabel,
                "\n",
                definition.Name,
                "\n",
                definition.ProductCode.Value,
                "\n",
                definition.ServiceHostName,
                "\n",
                definition.ServiceIpv4Address,
                "\n",
                definition.Port.ToString(CultureInfo.InvariantCulture),
                "\n");
            byte[] bytes = StrictUtf8.GetBytes(canonical);
            try
            {
                using (SHA256 sha256 = SHA256.Create())
                {
                    return sha256.ComputeHash(bytes);
                }
            }
            finally
            {
                Clear(bytes);
            }
        }

        private static byte[] BuildCanonicalProof(
            string productCode,
            CertificateSerialNumber currentSerialNumber,
            Guid renewalRequestId,
            DateTime timestampUtc,
            byte[] nonce,
            byte[] csrSha256,
            byte[] serviceIdentitySha256)
        {
            string canonical = string.Concat(
                RenewalProofLabel,
                "\n",
                productCode,
                "\n",
                currentSerialNumber.Hex,
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
                Convert.ToBase64String(serviceIdentitySha256),
                "\n");
            return StrictUtf8.GetBytes(canonical);
        }

        private static bool VerifySignature(
            byte[] leafCertificateDer,
            byte[] canonicalProof,
            byte[] proofSignature)
        {
            var certificate = new X509Certificate(leafCertificateDer);
            AsymmetricKeyParameter publicKey = certificate.GetPublicKey();
            string algorithm;
            if (publicKey is RsaKeyParameters)
            {
                algorithm = "SHA256WITHRSA";
            }
            else if (publicKey is ECPublicKeyParameters)
            {
                algorithm = "SHA256WITHECDSA";
            }
            else
            {
                return false;
            }

            ISigner verifier = SignerUtilities.GetSigner(algorithm);
            verifier.Init(false, publicKey);
            verifier.BlockUpdate(
                canonicalProof,
                0,
                canonicalProof.Length);
            return verifier.VerifySignature(proofSignature);
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
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
                    "Certificate renewal timestamps must use UTC.",
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
