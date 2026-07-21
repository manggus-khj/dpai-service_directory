using System;
using System.Xml;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    public enum ExternalResponseCode
    {
        Ok = 0,
        BadRequest = 1000,
        NotFound = 1001,
        Conflict = 1002,
        InvalidApiKey = 1003,
        LimitExceeded = 1004,
        RegistrationModeClosed = 1005,
        CertificateRequestInvalid = 1006,
        CertificateNotRenewable = 1007,
        Internal = 3000
    }

    public enum ExternalCertificateIssuanceStatus
    {
        Registered = 1,
        Reregistered = 2,
        Replayed = 3,
        Renewed = 4
    }

    public sealed class ExternalServiceItem
    {
        public ExternalServiceItem(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            int port,
            DateTime lastModifiedUtc)
        {
            ExternalNormalizedServiceDefinition service =
                ExternalApiModelValidation.NormalizeService(
                    name,
                    productCode,
                    serviceHostName,
                    serviceIpv4Address,
                    port,
                    nameof(name));

            Name = service.Name;
            ProductCode = service.ProductCode;
            ServiceHostName = service.ServiceHostName;
            ServiceIpv4Address = service.ServiceIpv4Address;
            Port = service.Port;
            LastModifiedUtc = ExternalApiModelValidation.RequireUtc(
                lastModifiedUtc,
                nameof(lastModifiedUtc));
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public int Port { get; }

        public DateTime LastModifiedUtc { get; }
    }

    public sealed class ExternalTrustInfo
    {
        private readonly byte[] _caCertificate;
        private readonly byte[] _caSpkiSha256;

        public ExternalTrustInfo(
            Guid siteId,
            byte[] caCertificate,
            byte[] caSpkiSha256,
            string crlUri)
        {
            SiteId = ExternalApiModelValidation.RequireNonEmptyGuid(
                siteId,
                nameof(siteId));
            _caCertificate = ExternalApiModelValidation.CloneRequiredBytes(
                caCertificate,
                0,
                ExternalApiContract.MaximumCaCertificateBytes,
                nameof(caCertificate));
            _caSpkiSha256 = ExternalApiModelValidation.CloneRequiredBytes(
                caSpkiSha256,
                ExternalApiContract.Sha256Bytes,
                0,
                nameof(caSpkiSha256));
            CrlUri = RequireCrlUri(crlUri);
        }

        public Guid SiteId { get; }

        public byte[] CaCertificate => (byte[])_caCertificate.Clone();

        public byte[] CaSpkiSha256 => (byte[])_caSpkiSha256.Clone();

        public string CrlUri { get; }

        private static string RequireCrlUri(string crlUri)
        {
            if (!StringComparer.Ordinal.Equals(
                    crlUri,
                    ExternalApiContract.CrlPath))
            {
                throw new ArgumentException(
                    "The External CRL URI must be the exact current alias path.",
                    nameof(crlUri));
            }

            return crlUri;
        }
    }

    public sealed class ExternalIssuedCertificate
    {
        private readonly byte[] _leafCertificate;
        private readonly byte[] _issuerCertificate;

        public ExternalIssuedCertificate(
            byte[] leafCertificate,
            byte[] issuerCertificate,
            string serialNumber,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            string crlUri)
        {
            _leafCertificate = ExternalApiModelValidation.CloneRequiredBytes(
                leafCertificate,
                0,
                ExternalApiContract.MaximumLeafCertificateBytes,
                nameof(leafCertificate));
            _issuerCertificate = ExternalApiModelValidation.CloneRequiredBytes(
                issuerCertificate,
                0,
                ExternalApiContract.MaximumCaCertificateBytes,
                nameof(issuerCertificate));
            SerialNumber = ExternalApiModelValidation.RequireSerialNumber(
                serialNumber,
                nameof(serialNumber));
            NotBeforeUtc = ExternalApiModelValidation.RequireUtc(
                notBeforeUtc,
                nameof(notBeforeUtc));
            NotAfterUtc = ExternalApiModelValidation.RequireUtc(
                notAfterUtc,
                nameof(notAfterUtc));
            if (NotAfterUtc <= NotBeforeUtc)
            {
                throw new ArgumentException(
                    "The issued certificate validity interval is invalid.",
                    nameof(notAfterUtc));
            }

            if (string.IsNullOrEmpty(crlUri)
                || !crlUri.StartsWith(
                    ExternalApiContract.IssuerCrlPathPrefix,
                    StringComparison.Ordinal)
                || crlUri.Length
                    != ExternalApiContract.IssuerCrlPathPrefix.Length + 32)
            {
                throw new ArgumentException(
                    "The issued certificate CRL URI must contain an issuer serial.",
                    nameof(crlUri));
            }

            ExternalApiModelValidation.RequireSerialNumber(
                crlUri.Substring(
                    ExternalApiContract.IssuerCrlPathPrefix.Length),
                nameof(crlUri));

            CrlUri = crlUri;
        }

        public byte[] LeafCertificate => (byte[])_leafCertificate.Clone();

        public byte[] IssuerCertificate => (byte[])_issuerCertificate.Clone();

        public string SerialNumber { get; }

        public DateTime NotBeforeUtc { get; }

        public DateTime NotAfterUtc { get; }

        public string CrlUri { get; }
    }

    public sealed class ExternalResponse
    {
        private ExternalResponse(
            ExternalResponseCode code,
            string message,
            ExternalResponsePayloadKind payloadKind,
            ExternalTrustInfo trustInfo,
            ExternalTrustBundle trustBundle,
            DateTime? utcNow,
            ExternalServiceItem service,
            ExternalCertificateIssuanceStatus? issuanceStatus,
            Guid? registrationRequestId,
            Guid? renewalRequestId,
            ExternalIssuedCertificate certificate)
        {
            if (!Enum.IsDefined(typeof(ExternalResponseCode), code))
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            Code = code;
            Message = ValidateMessage(message);
            PayloadKind = payloadKind;
            TrustInfo = trustInfo;
            TrustBundle = trustBundle;
            UtcNow = utcNow;
            Service = service;
            IssuanceStatus = issuanceStatus;
            RegistrationRequestId = registrationRequestId;
            RenewalRequestId = renewalRequestId;
            Certificate = certificate;
        }

        public string Result => Code == ExternalResponseCode.Ok
            ? "OK"
            : "ERROR";

        public ExternalResponseCode Code { get; }

        public int NumericCode => (int)Code;

        public string Message { get; }

        public bool IsSuccess => Code == ExternalResponseCode.Ok;

        public ExternalTrustInfo TrustInfo { get; }

        public ExternalTrustBundle TrustBundle { get; }

        public DateTime? UtcNow { get; }

        public ExternalServiceItem Service { get; }

        public ExternalCertificateIssuanceStatus? IssuanceStatus { get; }

        public Guid? RegistrationRequestId { get; }

        public Guid? RenewalRequestId { get; }

        public ExternalIssuedCertificate Certificate { get; }

        internal ExternalResponsePayloadKind PayloadKind { get; }

        public static ExternalResponse CreateTrustInfoSuccess(
            ExternalTrustInfo trustInfo)
        {
            return CreateTrustInfoSuccess(trustInfo, null);
        }

        public static ExternalResponse CreateTrustInfoSuccess(
            ExternalTrustInfo trustInfo,
            ExternalTrustBundle trustBundle)
        {
            if (trustInfo == null)
            {
                throw new ArgumentNullException(nameof(trustInfo));
            }

            if (trustBundle != null)
            {
                ValidateTrustBundleMatchesCurrent(trustInfo, trustBundle);
            }

            return Success(
                ExternalResponsePayloadKind.TrustInfo,
                trustInfo,
                trustBundle,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        public static ExternalResponse CreateHealthSuccess(DateTime utcNow)
        {
            return Success(
                ExternalResponsePayloadKind.Health,
                null,
                null,
                ExternalApiModelValidation.RequireUtc(
                    utcNow,
                    nameof(utcNow)),
                null,
                null,
                null,
                null,
                null);
        }

        public static ExternalResponse CreateServiceSuccess(
            ExternalServiceItem service)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            return Success(
                ExternalResponsePayloadKind.Service,
                null,
                null,
                null,
                service,
                null,
                null,
                null,
                null);
        }

        public static ExternalResponse CreateRegistrationSuccess(
            ExternalCertificateIssuanceStatus status,
            Guid registrationRequestId,
            ExternalServiceItem service,
            ExternalIssuedCertificate certificate)
        {
            if (status != ExternalCertificateIssuanceStatus.Registered
                && status != ExternalCertificateIssuanceStatus.Reregistered
                && status != ExternalCertificateIssuanceStatus.Replayed)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            return CreateIssuanceSuccess(
                status,
                registrationRequestId,
                null,
                service,
                certificate);
        }

        public static ExternalResponse CreateRenewalSuccess(
            Guid renewalRequestId,
            ExternalServiceItem service,
            ExternalIssuedCertificate certificate)
        {
            return CreateIssuanceSuccess(
                ExternalCertificateIssuanceStatus.Renewed,
                null,
                renewalRequestId,
                service,
                certificate);
        }

        public static ExternalResponse CreateError(ExternalResponseCode code)
        {
            if (!Enum.IsDefined(typeof(ExternalResponseCode), code)
                || code == ExternalResponseCode.Ok)
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            return new ExternalResponse(
                code,
                GetSafeErrorMessage(code),
                ExternalResponsePayloadKind.None,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        private static ExternalResponse CreateIssuanceSuccess(
            ExternalCertificateIssuanceStatus status,
            Guid? registrationRequestId,
            Guid? renewalRequestId,
            ExternalServiceItem service,
            ExternalIssuedCertificate certificate)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (certificate == null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }

            if (registrationRequestId.HasValue == renewalRequestId.HasValue)
            {
                throw new ArgumentException(
                    "Exactly one certificate issuance request ID is required.");
            }

            Guid requestId = registrationRequestId ?? renewalRequestId.Value;
            ExternalApiModelValidation.RequireNonEmptyGuid(
                requestId,
                registrationRequestId.HasValue
                    ? nameof(registrationRequestId)
                    : nameof(renewalRequestId));

            return Success(
                ExternalResponsePayloadKind.CertificateIssuance,
                null,
                null,
                null,
                service,
                status,
                registrationRequestId,
                renewalRequestId,
                certificate);
        }

        private static ExternalResponse Success(
            ExternalResponsePayloadKind payloadKind,
            ExternalTrustInfo trustInfo,
            ExternalTrustBundle trustBundle,
            DateTime? utcNow,
            ExternalServiceItem service,
            ExternalCertificateIssuanceStatus? issuanceStatus,
            Guid? registrationRequestId,
            Guid? renewalRequestId,
            ExternalIssuedCertificate certificate)
        {
            return new ExternalResponse(
                ExternalResponseCode.Ok,
                string.Empty,
                payloadKind,
                trustInfo,
                trustBundle,
                utcNow,
                service,
                issuanceStatus,
                registrationRequestId,
                renewalRequestId,
                certificate);
        }

        private static void ValidateTrustBundleMatchesCurrent(
            ExternalTrustInfo trustInfo,
            ExternalTrustBundle trustBundle)
        {
            ExternalTrustAuthority current = trustBundle.Authorities[0];
            if (trustInfo.SiteId != trustBundle.SiteId
                || !BytesEqual(
                    trustInfo.CaCertificate,
                    current.CaCertificate)
                || !BytesEqual(
                    trustInfo.CaSpkiSha256,
                    current.CaSpkiSha256))
            {
                throw new ArgumentException(
                    "TrustInfo must match the CURRENT trust bundle authority.",
                    nameof(trustBundle));
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
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

        private static string GetSafeErrorMessage(ExternalResponseCode code)
        {
            switch (code)
            {
                case ExternalResponseCode.BadRequest:
                    return "The request is invalid.";
                case ExternalResponseCode.NotFound:
                    return "The requested service was not found.";
                case ExternalResponseCode.Conflict:
                    return "The request conflicts with the current state.";
                case ExternalResponseCode.InvalidApiKey:
                    return "The API key is invalid.";
                case ExternalResponseCode.LimitExceeded:
                    return "The request limit was exceeded.";
                case ExternalResponseCode.RegistrationModeClosed:
                    return "Registration mode is closed.";
                case ExternalResponseCode.CertificateRequestInvalid:
                    return "The certificate signing request is invalid.";
                case ExternalResponseCode.CertificateNotRenewable:
                    return "The current certificate cannot be renewed.";
                case ExternalResponseCode.Internal:
                    return "The service directory could not process the request.";
                default:
                    throw new ArgumentOutOfRangeException(nameof(code));
            }
        }

        private static string ValidateMessage(string message)
        {
            string candidate = message ?? string.Empty;
            if (candidate.Length > ExternalApiContract.MaximumMessageCharacters)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    "The external response message exceeds 512 characters.");
            }

            try
            {
                XmlConvert.VerifyXmlChars(candidate);
            }
            catch (XmlException exception)
            {
                throw new ArgumentException(
                    "The external response message contains invalid XML characters.",
                    nameof(message),
                    exception);
            }

            return candidate;
        }
    }

    internal enum ExternalResponsePayloadKind
    {
        None = 0,
        TrustInfo = 1,
        Health = 2,
        Service = 3,
        CertificateIssuance = 4
    }
}
