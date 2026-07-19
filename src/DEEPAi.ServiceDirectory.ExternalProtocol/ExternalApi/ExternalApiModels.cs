using System;
using System.Xml;
using DEEPAi.ServiceDirectory.Domain;

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
        Internal = 3000
    }

    public enum ExternalRegistrationStatus
    {
        PendingNew = 1,
        PendingModify = 2,
        PendingExists = 3,
        AlreadyRegistered = 4
    }

    public sealed class ExternalRegistrationRequest
    {
        internal ExternalRegistrationRequest(ServiceDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            Definition = definition;
            Name = definition.Name;
            ProductCode = definition.ProductCode.Value;
            ServerAddress = definition.ServerAddress;
            Port = definition.Port;
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServerAddress { get; }

        public int Port { get; }

        internal ServiceDefinition Definition { get; }
    }

    public sealed class ExternalServiceItem
    {
        public ExternalServiceItem(
            string name,
            string productCode,
            string serverAddress,
            int port,
            DateTime lastModifiedUtc)
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError validationError;
            if (!ServiceDefinition.TryCreate(
                    name,
                    productCode,
                    serverAddress,
                    port,
                    out definition,
                    out validationError))
            {
                throw new ArgumentException(
                    "The external service definition is invalid: "
                    + validationError
                    + ".",
                    nameof(name));
            }

            if (lastModifiedUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Last modified time must use DateTimeKind.Utc.",
                    nameof(lastModifiedUtc));
            }

            Name = definition.Name;
            ProductCode = definition.ProductCode.Value;
            ServerAddress = definition.ServerAddress;
            Port = definition.Port;
            LastModifiedUtc = lastModifiedUtc;
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServerAddress { get; }

        public int Port { get; }

        public DateTime LastModifiedUtc { get; }
    }

    public sealed class ExternalResponse
    {
        private ExternalResponse(
            ExternalResponseCode code,
            string message,
            ExternalResponsePayloadKind payloadKind,
            DateTime? utcNow,
            ExternalServiceItem service,
            ExternalRegistrationStatus? registrationStatus,
            Guid? pendingId)
        {
            if (!Enum.IsDefined(typeof(ExternalResponseCode), code))
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            Message = ValidateMessage(message);
            Code = code;
            PayloadKind = payloadKind;
            UtcNow = utcNow;
            Service = service;
            RegistrationStatus = registrationStatus;
            PendingId = pendingId;
        }

        public string Result => Code == ExternalResponseCode.Ok
            ? "OK"
            : "ERROR";

        public ExternalResponseCode Code { get; }

        public int NumericCode => (int)Code;

        public string Message { get; }

        public bool IsSuccess => Code == ExternalResponseCode.Ok;

        public DateTime? UtcNow { get; }

        public ExternalServiceItem Service { get; }

        public ExternalRegistrationStatus? RegistrationStatus { get; }

        public Guid? PendingId { get; }

        internal ExternalResponsePayloadKind PayloadKind { get; }

        public static ExternalResponse CreateHealthSuccess(DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Health time must use DateTimeKind.Utc.",
                    nameof(utcNow));
            }

            return new ExternalResponse(
                ExternalResponseCode.Ok,
                string.Empty,
                ExternalResponsePayloadKind.Health,
                utcNow,
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

            return new ExternalResponse(
                ExternalResponseCode.Ok,
                string.Empty,
                ExternalResponsePayloadKind.Service,
                null,
                service,
                null,
                null);
        }

        public static ExternalResponse CreateRegistrationSuccess(
            ExternalRegistrationStatus status,
            Guid? pendingId)
        {
            if (!Enum.IsDefined(typeof(ExternalRegistrationStatus), status))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            bool requiresPendingId = status !=
                ExternalRegistrationStatus.AlreadyRegistered;
            if (requiresPendingId)
            {
                if (!pendingId.HasValue || pendingId.Value == Guid.Empty)
                {
                    throw new ArgumentException(
                        "Pending registration statuses require a non-empty pending ID.",
                        nameof(pendingId));
                }
            }
            else if (pendingId.HasValue)
            {
                throw new ArgumentException(
                    "ALREADY_REGISTERED must not contain a pending ID.",
                    nameof(pendingId));
            }

            return new ExternalResponse(
                ExternalResponseCode.Ok,
                string.Empty,
                ExternalResponsePayloadKind.Registration,
                null,
                null,
                status,
                pendingId);
        }

        public static ExternalResponse CreateError(
            ExternalResponseCode code)
        {
            if (!Enum.IsDefined(typeof(ExternalResponseCode), code)
                || code == ExternalResponseCode.Ok)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(code),
                    "An external error response requires a defined nonzero code.");
            }

            return new ExternalResponse(
                code,
                GetSafeErrorMessage(code),
                ExternalResponsePayloadKind.None,
                null,
                null,
                null,
                null);
        }

        private static string GetSafeErrorMessage(
            ExternalResponseCode code)
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
        Health = 1,
        Service = 2,
        Registration = 3
    }
}
