using System;
using System.Xml;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using CertificateSerialNumberValue =
    DEEPAi.ServiceDirectory.Domain.Certificates.CertificateSerialNumber;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public enum AdminRegistrationModeState
    {
        Closed = 1,
        Open = 2,
        Claimed = 3
    }

    public enum AdminRegistrationOutcome
    {
        Registered = 1,
        Reregistered = 2,
        Failed = 3
    }

    public sealed class AdminRegistrationModeStatus
    {
        public AdminRegistrationModeStatus(
            AdminRegistrationModeState state,
            DateTime? openedUtc,
            DateTime? expiresUtc,
            int? remainingSeconds)
        {
            if (!Enum.IsDefined(typeof(AdminRegistrationModeState), state))
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            bool isOpen = state == AdminRegistrationModeState.Open;
            if (isOpen != openedUtc.HasValue
                || isOpen != expiresUtc.HasValue
                || isOpen != remainingSeconds.HasValue)
            {
                throw new ArgumentException(
                    "Registration mode timing values must exist only while the mode is open.");
            }

            if (isOpen)
            {
                EnsureUtc(openedUtc.Value, nameof(openedUtc));
                EnsureUtc(expiresUtc.Value, nameof(expiresUtc));
                if (expiresUtc.Value != openedUtc.Value.AddHours(1))
                {
                    throw new ArgumentException(
                        "Registration mode must use the fixed one-hour deadline.",
                        nameof(expiresUtc));
                }

                if (remainingSeconds.Value < 0
                    || remainingSeconds.Value >
                        AdminApiContract.RegistrationModeDurationSeconds)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(remainingSeconds));
                }
            }

            State = state;
            OpenedUtc = openedUtc;
            ExpiresUtc = expiresUtc;
            RemainingSeconds = remainingSeconds;
        }

        public AdminRegistrationModeState State { get; }

        public DateTime? OpenedUtc { get; }

        public DateTime? ExpiresUtc { get; }

        public int? RemainingSeconds { get; }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Registration mode timestamps must use UTC.",
                    parameterName);
            }
        }
    }

    public sealed class AdminLastRegistration
    {
        private AdminLastRegistration(
            DateTime completedUtc,
            AdminRegistrationOutcome outcome,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            string certificateSerialNumber,
            DateTime? certificateNotAfterUtc,
            string failureReason)
        {
            CompletedUtc = completedUtc;
            Outcome = outcome;
            ProductCode = productCode;
            ServiceHostName = serviceHostName;
            ServiceIpv4Address = serviceIpv4Address;
            CertificateSerialNumber = certificateSerialNumber;
            CertificateNotAfterUtc = certificateNotAfterUtc;
            FailureReason = failureReason;
        }

        public DateTime CompletedUtc { get; }

        public AdminRegistrationOutcome Outcome { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public string CertificateSerialNumber { get; }

        public DateTime? CertificateNotAfterUtc { get; }

        public string FailureReason { get; }

        public static AdminLastRegistration CreateSuccess(
            DateTime completedUtc,
            AdminRegistrationOutcome outcome,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            string certificateSerialNumber,
            DateTime certificateNotAfterUtc)
        {
            EnsureUtc(completedUtc, nameof(completedUtc));
            EnsureUtc(
                certificateNotAfterUtc,
                nameof(certificateNotAfterUtc));
            if (certificateNotAfterUtc <= completedUtc)
            {
                throw new ArgumentException(
                    "The registration certificate must expire after completion.",
                    nameof(certificateNotAfterUtc));
            }

            if (outcome != AdminRegistrationOutcome.Registered
                && outcome != AdminRegistrationOutcome.Reregistered)
            {
                throw new ArgumentOutOfRangeException(nameof(outcome));
            }

            DEEPAi.ServiceDirectory.Domain.ProductCode normalizedProductCode;
            if (!DEEPAi.ServiceDirectory.Domain.ProductCode.TryCreate(
                    productCode,
                    out normalizedProductCode)
                || !StringComparer.Ordinal.Equals(
                    productCode,
                    normalizedProductCode.Value))
            {
                throw new ArgumentException(
                    "The last registration ProductCode is not canonical.",
                    nameof(productCode));
            }

            ServiceEndpointIdentity endpointIdentity;
            EndpointIdentityValidationError endpointError;
            if (!ServiceEndpointIdentity.TryCreate(
                    serviceHostName,
                    serviceIpv4Address,
                    out endpointIdentity,
                    out endpointError)
                || !StringComparer.Ordinal.Equals(
                    serviceHostName,
                    endpointIdentity.ServiceHostName)
                || !StringComparer.Ordinal.Equals(
                    serviceIpv4Address,
                    endpointIdentity.ServiceIpv4Address))
            {
                throw new ArgumentException(
                    "The last registration service identity is not canonical: "
                    + endpointError
                    + ".",
                    nameof(serviceHostName));
            }

            CertificateSerialNumberValue serial;
            if (!CertificateSerialNumberValue.TryCreate(
                    certificateSerialNumber,
                    out serial))
            {
                throw new ArgumentException(
                    "The last registration certificate serial is invalid.",
                    nameof(certificateSerialNumber));
            }

            return new AdminLastRegistration(
                completedUtc,
                outcome,
                normalizedProductCode.Value,
                endpointIdentity.ServiceHostName,
                endpointIdentity.ServiceIpv4Address,
                serial.Hex,
                certificateNotAfterUtc,
                null);
        }

        public static AdminLastRegistration CreateFailure(
            DateTime completedUtc,
            string failureReason)
        {
            EnsureUtc(completedUtc, nameof(completedUtc));
            string safeReason = ValidateFailureReason(failureReason);
            return new AdminLastRegistration(
                completedUtc,
                AdminRegistrationOutcome.Failed,
                null,
                null,
                null,
                null,
                null,
                safeReason);
        }

        private static string ValidateFailureReason(string failureReason)
        {
            if (failureReason == null)
            {
                throw new ArgumentNullException(nameof(failureReason));
            }

            string candidate = failureReason.Trim();
            if (candidate.Length == 0
                || candidate.Length >
                    AdminApiContract.MaximumFailureReasonCharacters)
            {
                throw new ArgumentOutOfRangeException(nameof(failureReason));
            }

            try
            {
                XmlConvert.VerifyXmlChars(candidate);
            }
            catch (XmlException exception)
            {
                throw new ArgumentException(
                    "The registration failure reason contains invalid XML characters.",
                    nameof(failureReason),
                    exception);
            }

            return candidate;
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Last registration timestamps must use UTC.",
                    parameterName);
            }
        }
    }

    public sealed class AdminServerRegistrationModeResponse
    {
        public AdminServerRegistrationModeResponse(
            AdminRegistrationModeStatus registrationMode,
            AdminLastRegistration lastRegistration)
        {
            RegistrationMode = registrationMode
                ?? throw new ArgumentNullException(nameof(registrationMode));
            LastRegistration = lastRegistration;
        }

        public AdminRegistrationModeStatus RegistrationMode { get; }

        public AdminLastRegistration LastRegistration { get; }
    }
}
