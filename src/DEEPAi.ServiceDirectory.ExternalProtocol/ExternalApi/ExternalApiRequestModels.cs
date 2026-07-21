using System;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    public sealed class ExternalRegistrationRequest
    {
        private readonly byte[] _certificateSigningRequest;

        internal ExternalRegistrationRequest(
            Guid registrationRequestId,
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            int port,
            byte[] certificateSigningRequest)
        {
            ExternalNormalizedServiceDefinition service =
                ExternalApiModelValidation.NormalizeService(
                    name,
                    productCode,
                    serviceHostName,
                    serviceIpv4Address,
                    port,
                    nameof(name));

            RegistrationRequestId =
                ExternalApiModelValidation.RequireNonEmptyGuid(
                    registrationRequestId,
                    nameof(registrationRequestId));
            Name = service.Name;
            ProductCode = service.ProductCode;
            ServiceHostName = service.ServiceHostName;
            ServiceIpv4Address = service.ServiceIpv4Address;
            Port = service.Port;
            _certificateSigningRequest =
                ExternalApiModelValidation.CloneRequiredBytes(
                    certificateSigningRequest,
                    0,
                    ExternalApiContract.MaximumCertificateSigningRequestBytes,
                    nameof(certificateSigningRequest));
        }

        public Guid RegistrationRequestId { get; }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public int Port { get; }

        public byte[] CertificateSigningRequest =>
            (byte[])_certificateSigningRequest.Clone();
    }

    public sealed class ExternalCertificateRenewalRequest
    {
        private readonly byte[] _nonce;
        private readonly byte[] _certificateSigningRequest;
        private readonly byte[] _serviceIdentitySha256;
        private readonly byte[] _proofSignature;

        internal ExternalCertificateRenewalRequest(
            Guid renewalRequestId,
            string productCode,
            string currentSerialNumber,
            DateTime timestampUtc,
            byte[] nonce,
            string name,
            string serviceHostName,
            string serviceIpv4Address,
            int port,
            byte[] certificateSigningRequest,
            byte[] serviceIdentitySha256,
            byte[] proofSignature)
        {
            ExternalNormalizedServiceDefinition service =
                ExternalApiModelValidation.NormalizeService(
                    name,
                    productCode,
                    serviceHostName,
                    serviceIpv4Address,
                    port,
                    nameof(name));

            RenewalRequestId = ExternalApiModelValidation.RequireNonEmptyGuid(
                renewalRequestId,
                nameof(renewalRequestId));
            ProductCode = service.ProductCode;
            CurrentSerialNumber =
                ExternalApiModelValidation.RequireSerialNumber(
                    currentSerialNumber,
                    nameof(currentSerialNumber));
            TimestampUtc = ExternalApiModelValidation.RequireUtc(
                timestampUtc,
                nameof(timestampUtc));
            _nonce = ExternalApiModelValidation.CloneRequiredBytes(
                nonce,
                ExternalApiContract.RenewalNonceBytes,
                0,
                nameof(nonce));
            Name = service.Name;
            ServiceHostName = service.ServiceHostName;
            ServiceIpv4Address = service.ServiceIpv4Address;
            Port = service.Port;
            _certificateSigningRequest =
                ExternalApiModelValidation.CloneRequiredBytes(
                    certificateSigningRequest,
                    0,
                    ExternalApiContract.MaximumCertificateSigningRequestBytes,
                    nameof(certificateSigningRequest));
            _serviceIdentitySha256 =
                ExternalApiModelValidation.CloneRequiredBytes(
                    serviceIdentitySha256,
                    ExternalApiContract.Sha256Bytes,
                    0,
                    nameof(serviceIdentitySha256));
            _proofSignature =
                ExternalApiModelValidation.CloneRequiredBytes(
                    proofSignature,
                    0,
                    ExternalApiContract.MaximumProofSignatureBytes,
                    nameof(proofSignature));
        }

        public Guid RenewalRequestId { get; }

        public string ProductCode { get; }

        public string CurrentSerialNumber { get; }

        public DateTime TimestampUtc { get; }

        public byte[] Nonce => (byte[])_nonce.Clone();

        public string Name { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public int Port { get; }

        public byte[] CertificateSigningRequest =>
            (byte[])_certificateSigningRequest.Clone();

        public byte[] ServiceIdentitySha256 =>
            (byte[])_serviceIdentitySha256.Clone();

        public byte[] ProofSignature => (byte[])_proofSignature.Clone();
    }
}
