using System;
using System.IO;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal static class CertificateSigningRequestValidator
    {
        internal const int MaximumDerLength = 48 * 1024;

        private static readonly BigInteger RequiredRsaPublicExponent =
            BigInteger.ValueOf(65537);

        internal static bool TryValidate(
            byte[] requestDer,
            ServiceEndpointIdentity expectedIdentity,
            out ValidatedCertificateSigningRequest validatedRequest,
            out CertificateSigningRequestValidationError error)
        {
            validatedRequest = null;
            if (requestDer == null || requestDer.Length == 0)
            {
                error = CertificateSigningRequestValidationError.Empty;
                return false;
            }

            if (requestDer.Length > MaximumDerLength)
            {
                error = CertificateSigningRequestValidationError.TooLarge;
                return false;
            }

            if (expectedIdentity == null)
            {
                throw new ArgumentNullException(nameof(expectedIdentity));
            }

            Pkcs10CertificationRequest request;
            try
            {
                request = new Pkcs10CertificationRequest(requestDer);
            }
            catch (Exception exception) when (IsInputParsingException(exception))
            {
                error = CertificateSigningRequestValidationError.Malformed;
                return false;
            }

            byte[] canonicalDer;
            try
            {
                canonicalDer = request.GetDerEncoded();
            }
            catch (Exception exception) when (IsInputParsingException(exception))
            {
                error = CertificateSigningRequestValidationError.Malformed;
                return false;
            }

            if (!AreEqual(requestDer, canonicalDer))
            {
                error = CertificateSigningRequestValidationError.NonCanonicalDer;
                return false;
            }

            AsymmetricKeyParameter publicKey;
            try
            {
                publicKey = request.GetPublicKey();
            }
            catch (Exception exception) when (IsInputParsingException(exception))
            {
                error = CertificateSigningRequestValidationError.Malformed;
                return false;
            }

            if (!IsAllowedSignatureAlgorithm(request.SignatureAlgorithm.Algorithm, publicKey))
            {
                error = CertificateSigningRequestValidationError.SignatureAlgorithmNotAllowed;
                return false;
            }

            try
            {
                if (!request.Verify())
                {
                    error = CertificateSigningRequestValidationError.SignatureInvalid;
                    return false;
                }
            }
            catch (Exception exception) when (IsInputParsingException(exception))
            {
                error = CertificateSigningRequestValidationError.SignatureInvalid;
                return false;
            }

            if (!ValidatePublicKey(publicKey, out error))
            {
                return false;
            }

            X509Extensions requestedExtensions;
            try
            {
                Asn1Set attributes = request
                    .GetCertificationRequestInfo()
                    .Attributes;
                if (attributes == null || attributes.Count == 0)
                {
                    error = CertificateSigningRequestValidationError.RequestedExtensionsMissing;
                    return false;
                }

                if (attributes.Count != 1)
                {
                    error = CertificateSigningRequestValidationError.RequestedExtensionsInvalid;
                    return false;
                }

                foreach (Asn1Encodable encodedAttribute in attributes)
                {
                    AttributePkcs attribute = AttributePkcs.GetInstance(
                        encodedAttribute);
                    if (!attribute.AttrType.Equals(
                            PkcsObjectIdentifiers.Pkcs9AtExtensionRequest))
                    {
                        error = CertificateSigningRequestValidationError.RequestedExtensionsInvalid;
                        return false;
                    }

                    if (attribute.AttrValues == null
                        || attribute.AttrValues.Count != 1)
                    {
                        error = CertificateSigningRequestValidationError.RequestedExtensionsInvalid;
                        return false;
                    }
                }

                requestedExtensions = request.GetRequestedExtensions();
            }
            catch (Exception exception) when (IsInputParsingException(exception))
            {
                error = CertificateSigningRequestValidationError.Malformed;
                return false;
            }

            if (requestedExtensions == null)
            {
                error = CertificateSigningRequestValidationError.RequestedExtensionsMissing;
                return false;
            }

            DerObjectIdentifier[] extensionOids = requestedExtensions.GetExtensionOids();
            if (extensionOids == null || extensionOids.Length != 1)
            {
                error = CertificateSigningRequestValidationError.UnsupportedExtension;
                return false;
            }

            if (!extensionOids[0].Equals(X509Extensions.SubjectAlternativeName))
            {
                error = CertificateSigningRequestValidationError.UnsupportedExtension;
                return false;
            }

            X509Extension subjectAlternativeName = requestedExtensions.GetExtension(
                X509Extensions.SubjectAlternativeName);
            if (subjectAlternativeName == null)
            {
                error = CertificateSigningRequestValidationError.SubjectAlternativeNameInvalid;
                return false;
            }

            if (!ValidateSubjectAlternativeName(
                    subjectAlternativeName,
                    expectedIdentity,
                    out error))
            {
                return false;
            }

            byte[] subjectPublicKeyInfo = null;
            byte[] subjectPublicKeyInfoSha256 = null;
            try
            {
                subjectPublicKeyInfo = SubjectPublicKeyInfoFactory
                    .CreateSubjectPublicKeyInfo(publicKey)
                    .GetDerEncoded();
                using (SHA256 sha256 = SHA256.Create())
                {
                    subjectPublicKeyInfoSha256 = sha256.ComputeHash(subjectPublicKeyInfo);
                }

                validatedRequest = new ValidatedCertificateSigningRequest(
                    requestDer,
                    expectedIdentity,
                    publicKey,
                    subjectPublicKeyInfoSha256);
            }
            finally
            {
                Zero(subjectPublicKeyInfo);
                Zero(subjectPublicKeyInfoSha256);
            }

            error = CertificateSigningRequestValidationError.None;
            return true;
        }

        private static bool ValidatePublicKey(
            AsymmetricKeyParameter publicKey,
            out CertificateSigningRequestValidationError error)
        {
            var rsa = publicKey as RsaKeyParameters;
            if (rsa != null && !rsa.IsPrivate)
            {
                if (rsa.Modulus.BitLength < 2048
                    || !rsa.Exponent.Equals(RequiredRsaPublicExponent))
                {
                    error = CertificateSigningRequestValidationError.PublicKeyTooWeak;
                    return false;
                }

                error = CertificateSigningRequestValidationError.None;
                return true;
            }

            var ec = publicKey as ECPublicKeyParameters;
            if (ec != null && !ec.IsPrivate)
            {
                if (ec.PublicKeyParamSet == null
                    || !ec.PublicKeyParamSet.Equals(X9ObjectIdentifiers.Prime256v1)
                    || ec.Q == null
                    || ec.Q.IsInfinity
                    || !ec.Q.IsValid())
                {
                    error = CertificateSigningRequestValidationError.PublicKeyTooWeak;
                    return false;
                }

                error = CertificateSigningRequestValidationError.None;
                return true;
            }

            error = CertificateSigningRequestValidationError.PublicKeyUnsupported;
            return false;
        }

        private static bool ValidateSubjectAlternativeName(
            X509Extension extension,
            ServiceEndpointIdentity expectedIdentity,
            out CertificateSigningRequestValidationError error)
        {
            GeneralNames generalNames;
            try
            {
                generalNames = GeneralNames.GetInstance(extension.GetParsedValue());
            }
            catch (Exception exception) when (IsInputParsingException(exception))
            {
                error = CertificateSigningRequestValidationError.SubjectAlternativeNameInvalid;
                return false;
            }

            GeneralName[] names = generalNames.GetNames();
            if (names == null || names.Length != 2)
            {
                error = CertificateSigningRequestValidationError.SubjectAlternativeNameInvalid;
                return false;
            }

            string dnsName = null;
            string ipv4Address = null;
            foreach (GeneralName name in names)
            {
                try
                {
                    if (name.TagNo == GeneralName.DnsName && dnsName == null)
                    {
                        dnsName = DerIA5String.GetInstance(name.Name).GetString();
                    }
                    else if (name.TagNo == GeneralName.IPAddress && ipv4Address == null)
                    {
                        byte[] addressBytes = Asn1OctetString
                            .GetInstance(name.Name)
                            .GetOctets();
                        if (addressBytes.Length != 4)
                        {
                            error = CertificateSigningRequestValidationError.SubjectAlternativeNameInvalid;
                            return false;
                        }

                        ipv4Address = string.Join(
                            ".",
                            addressBytes[0],
                            addressBytes[1],
                            addressBytes[2],
                            addressBytes[3]);
                    }
                    else
                    {
                        error = CertificateSigningRequestValidationError.SubjectAlternativeNameInvalid;
                        return false;
                    }
                }
                catch (Exception exception) when (IsInputParsingException(exception))
                {
                    error = CertificateSigningRequestValidationError.SubjectAlternativeNameInvalid;
                    return false;
                }
            }

            if (!StringComparer.Ordinal.Equals(
                    dnsName,
                    expectedIdentity.ServiceHostName)
                || !StringComparer.Ordinal.Equals(
                    ipv4Address,
                    expectedIdentity.ServiceIpv4Address))
            {
                error = CertificateSigningRequestValidationError.SubjectAlternativeNameMismatch;
                return false;
            }

            error = CertificateSigningRequestValidationError.None;
            return true;
        }

        private static bool IsAllowedSignatureAlgorithm(
            DerObjectIdentifier signatureAlgorithm,
            AsymmetricKeyParameter publicKey)
        {
            bool isRsa = publicKey is RsaKeyParameters;
            if (isRsa)
            {
                return signatureAlgorithm.Equals(PkcsObjectIdentifiers.Sha256WithRsaEncryption)
                    || signatureAlgorithm.Equals(PkcsObjectIdentifiers.Sha384WithRsaEncryption)
                    || signatureAlgorithm.Equals(PkcsObjectIdentifiers.Sha512WithRsaEncryption);
            }

            bool isEc = publicKey is ECPublicKeyParameters;
            return isEc
                && (signatureAlgorithm.Equals(X9ObjectIdentifiers.ECDsaWithSha256)
                    || signatureAlgorithm.Equals(X9ObjectIdentifiers.ECDsaWithSha384)
                    || signatureAlgorithm.Equals(X9ObjectIdentifiers.ECDsaWithSha512));
        }

        private static bool IsInputParsingException(Exception exception)
        {
            return exception is ArgumentException
                || exception is IOException
                || exception is InvalidCastException
                || exception is InvalidOperationException
                || exception is Asn1Exception
                || exception is PkcsException
                || exception is GeneralSecurityException;
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

        private static void Zero(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
