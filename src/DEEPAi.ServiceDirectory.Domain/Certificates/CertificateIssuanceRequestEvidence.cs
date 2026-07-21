using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DEEPAi.ServiceDirectory.Domain.Certificates
{
    public enum CertificateIssuanceReplayStatus
    {
        NewRequest = 0,
        ExactReplay = 1,
        Conflict = 2
    }

    public sealed class CertificateIssuanceRequestEvidence
    {
        private const string RegistrationLabel =
            "DPAI-SD-REGISTRATION-PAYLOAD-V1";
        private const string RenewalLabel =
            "DPAI-SD-RENEWAL-PAYLOAD-V1";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        private readonly byte[] _csrSha256;
        private readonly byte[] _requestPayloadSha256;

        private CertificateIssuanceRequestEvidence(
            Guid requestId,
            CertificateIssuanceKind issuanceKind,
            ServiceDefinition serviceDefinition,
            CertificateSerialNumber? currentSerialNumber,
            byte[] csrSha256,
            byte[] requestPayloadSha256)
        {
            RequestId = requestId;
            IssuanceKind = issuanceKind;
            ServiceDefinition = serviceDefinition;
            CurrentSerialNumber = currentSerialNumber;
            _csrSha256 = CloneSha256(csrSha256, nameof(csrSha256));
            _requestPayloadSha256 = CloneSha256(
                requestPayloadSha256,
                nameof(requestPayloadSha256));
        }

        public Guid RequestId { get; }

        public CertificateIssuanceKind IssuanceKind { get; }

        public ServiceDefinition ServiceDefinition { get; }

        public CertificateSerialNumber? CurrentSerialNumber { get; }

        public static CertificateIssuanceRequestEvidence CreateRegistration(
            Guid requestId,
            ServiceDefinition serviceDefinition,
            byte[] certificateSigningRequestDer)
        {
            ValidateCommon(
                requestId,
                serviceDefinition,
                certificateSigningRequestDer);
            byte[] csrSha256 = ComputeSha256(certificateSigningRequestDer);
            byte[] payloadSha256 = ComputePayloadSha256(
                RegistrationLabel,
                serviceDefinition,
                null,
                csrSha256);
            try
            {
                return new CertificateIssuanceRequestEvidence(
                    requestId,
                    CertificateIssuanceKind.Registration,
                    serviceDefinition,
                    null,
                    csrSha256,
                    payloadSha256);
            }
            finally
            {
                Array.Clear(csrSha256, 0, csrSha256.Length);
                Array.Clear(payloadSha256, 0, payloadSha256.Length);
            }
        }

        public static CertificateIssuanceRequestEvidence CreateRenewal(
            Guid requestId,
            CertificateSerialNumber currentSerialNumber,
            ServiceDefinition serviceDefinition,
            byte[] certificateSigningRequestDer)
        {
            ValidateCommon(
                requestId,
                serviceDefinition,
                certificateSigningRequestDer);
            if (!currentSerialNumber.IsValid)
            {
                throw new ArgumentException(
                    "Current certificate serial number must be valid.",
                    nameof(currentSerialNumber));
            }

            byte[] csrSha256 = ComputeSha256(certificateSigningRequestDer);
            byte[] payloadSha256 = ComputePayloadSha256(
                RenewalLabel,
                serviceDefinition,
                currentSerialNumber,
                csrSha256);
            try
            {
                return new CertificateIssuanceRequestEvidence(
                    requestId,
                    CertificateIssuanceKind.Renewal,
                    serviceDefinition,
                    currentSerialNumber,
                    csrSha256,
                    payloadSha256);
            }
            finally
            {
                Array.Clear(csrSha256, 0, csrSha256.Length);
                Array.Clear(payloadSha256, 0, payloadSha256.Length);
            }
        }

        public byte[] GetCsrSha256()
        {
            return (byte[])_csrSha256.Clone();
        }

        public byte[] GetRequestPayloadSha256()
        {
            return (byte[])_requestPayloadSha256.Clone();
        }

        private static byte[] ComputePayloadSha256(
            string label,
            ServiceDefinition serviceDefinition,
            CertificateSerialNumber? currentSerialNumber,
            byte[] csrSha256)
        {
            using (var stream = new MemoryStream())
            {
                byte[] labelBytes = Encoding.ASCII.GetBytes(label);
                stream.Write(labelBytes, 0, labelBytes.Length);
                WriteLengthPrefixedAscii(
                    stream,
                    serviceDefinition.ProductCode.Value);
                if (currentSerialNumber.HasValue)
                {
                    byte[] serialBytes = currentSerialNumber.Value
                        .ToByteArray();
                    try
                    {
                        WriteLengthPrefixed(stream, serialBytes);
                    }
                    finally
                    {
                        Array.Clear(serialBytes, 0, serialBytes.Length);
                    }
                }

                WriteLengthPrefixedUtf8(stream, serviceDefinition.Name);
                WriteLengthPrefixedAscii(
                    stream,
                    serviceDefinition.ServiceEndpointIdentity.ServiceHostName);
                WriteLengthPrefixedAscii(
                    stream,
                    serviceDefinition.ServiceEndpointIdentity
                        .ServiceIpv4Address);
                WriteLengthPrefixed(
                    stream,
                    new[]
                    {
                        (byte)(serviceDefinition.Port >> 8),
                        (byte)serviceDefinition.Port
                    });
                WriteLengthPrefixed(stream, csrSha256);
                stream.Position = 0;
                using (SHA256 sha256 = SHA256.Create())
                {
                    return sha256.ComputeHash(stream);
                }
            }
        }

        private static void WriteLengthPrefixedAscii(
            Stream stream,
            string value)
        {
            WriteLengthPrefixedEncoded(stream, Encoding.ASCII, value);
        }

        private static void WriteLengthPrefixedUtf8(
            Stream stream,
            string value)
        {
            WriteLengthPrefixedEncoded(stream, StrictUtf8, value);
        }

        private static void WriteLengthPrefixedEncoded(
            Stream stream,
            Encoding encoding,
            string value)
        {
            byte[] bytes = encoding.GetBytes(value);
            try
            {
                WriteLengthPrefixed(stream, bytes);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private static void WriteLengthPrefixed(Stream stream, byte[] value)
        {
            uint length = checked((uint)value.Length);
            stream.WriteByte((byte)(length >> 24));
            stream.WriteByte((byte)(length >> 16));
            stream.WriteByte((byte)(length >> 8));
            stream.WriteByte((byte)length);
            stream.Write(value, 0, value.Length);
        }

        private static byte[] ComputeSha256(byte[] value)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(value);
            }
        }

        private static byte[] CloneSha256(byte[] value, string parameterName)
        {
            if (value == null
                || value.Length != CertificateLedgerEntry.Sha256Length)
            {
                throw new ArgumentException(
                    "SHA-256 values must be exactly 32 bytes.",
                    parameterName);
            }

            return (byte[])value.Clone();
        }

        private static void ValidateCommon(
            Guid requestId,
            ServiceDefinition serviceDefinition,
            byte[] certificateSigningRequestDer)
        {
            if (requestId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Issuance request ID must not be empty.",
                    nameof(requestId));
            }

            if (serviceDefinition == null)
            {
                throw new ArgumentNullException(nameof(serviceDefinition));
            }

            if (certificateSigningRequestDer == null
                || certificateSigningRequestDer.Length == 0)
            {
                throw new ArgumentException(
                    "Certificate signing request DER must not be empty.",
                    nameof(certificateSigningRequestDer));
            }
        }
    }
}
