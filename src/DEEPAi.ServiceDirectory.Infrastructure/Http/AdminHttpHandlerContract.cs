using System;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed class AdminServicesQuery
    {
        internal AdminServicesQuery(
            bool includeDeleted,
            int pageSize,
            string cursor)
        {
            ValidatePage(pageSize, cursor);
            IncludeDeleted = includeDeleted;
            PageSize = pageSize;
            Cursor = cursor;
        }

        public bool IncludeDeleted { get; }

        public int PageSize { get; }

        public string Cursor { get; }

        private static void ValidatePage(int pageSize, string cursor)
        {
            if (pageSize < 1 || pageSize > AdminApiContract.PageSize)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            if (cursor != null
                && (cursor.Length == 0 || cursor.Length > 2048))
            {
                throw new ArgumentException(
                    "The Admin cursor is invalid.",
                    nameof(cursor));
            }
        }
    }

    public sealed class AdminCertificatesQuery
    {
        internal AdminCertificatesQuery(int pageSize, string cursor)
        {
            if (pageSize < 1 || pageSize > AdminApiContract.PageSize)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            if (cursor != null
                && (cursor.Length == 0 || cursor.Length > 2048))
            {
                throw new ArgumentException(
                    "The Admin cursor is invalid.",
                    nameof(cursor));
            }

            PageSize = pageSize;
            Cursor = cursor;
        }

        public int PageSize { get; }

        public string Cursor { get; }
    }

    public sealed class AdminHandlerResult<T>
        where T : class
    {
        private AdminHandlerResult(
            T value,
            AdminServerErrorCode? errorCode)
        {
            if ((value != null) == errorCode.HasValue)
            {
                throw new ArgumentException(
                    "An Admin handler result must contain one value or one error.");
            }

            if (errorCode.HasValue
                && !Enum.IsDefined(
                    typeof(AdminServerErrorCode),
                    errorCode.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(errorCode));
            }

            Value = value;
            ErrorCode = errorCode;
        }

        public bool IsSuccess => Value != null;

        public T Value { get; }

        public AdminServerErrorCode? ErrorCode { get; }

        public static AdminHandlerResult<T> Success(T value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return new AdminHandlerResult<T>(value, null);
        }

        public static AdminHandlerResult<T> Failure(
            AdminServerErrorCode errorCode)
        {
            return new AdminHandlerResult<T>(null, errorCode);
        }
    }

    public interface IAdminHttpRequestHandler
    {
        AdminHandlerResult<AdminServerServicesResponse> GetServices(
            AdminServicesQuery query);

        AdminHandlerResult<AdminServerRegistrationModeResponse>
            GetRegistrationMode();

        AdminHandlerResult<AdminServerRegistrationModeResponse>
            OpenRegistrationMode();

        AdminHandlerResult<AdminServerRegistrationModeResponse>
            CloseRegistrationMode();

        AdminHandlerResult<AdminServerUnitResponse> DeleteService(
            string productCode);

        AdminHandlerResult<AdminServerSyncStatusResponse> GetSyncStatus();

        AdminHandlerResult<AdminServerUnitResponse> EnableSync(
            AdminEnableSyncRequest request);

        AdminHandlerResult<AdminServerUnitResponse> ConfirmPairing(
            AdminPairingConfirmationRequest request);

        AdminHandlerResult<AdminServerUnitResponse> CancelPairing(
            AdminPairingCancellationRequest request);

        AdminHandlerResult<AdminServerSyncDisableResponse> DisableSync(
            AdminDisableSyncRequest request);

        AdminHandlerResult<AdminServerUnitResponse> SynchronizeNow();

        AdminHandlerResult<AdminServerLoggingResponse> GetLoggingSettings();

        AdminHandlerResult<AdminServerLoggingResponse> PutLoggingSettings(
            AdminLoggingSettingsRequest request);

        AdminHandlerResult<AdminServerCaStatusResponse> GetCaStatus();

        AdminHandlerResult<AdminServerCaBackupResponse> CreateCaBackup(
            AdminCreateCaBackupRequest request);

        AdminHandlerResult<AdminServerCaRotationResponse>
            GetCaRotation();

        AdminHandlerResult<AdminServerCaRotationResponse>
            PrepareCaRotation();

        AdminHandlerResult<AdminServerCaRotationResponse>
            CancelCaRotation(AdminCancelCaRotationRequest request);

        AdminHandlerResult<AdminServerCertificatesResponse> GetCertificates(
            AdminCertificatesQuery query);

        AdminHandlerResult<AdminServerCertificateRevocationResponse>
            RevokeCertificate(
                string serialNumber,
                AdminRevokeCertificateRequest request);
    }
}
