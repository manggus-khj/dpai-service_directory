using System;
using System.IO;
using System.Security;
using System.Threading;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Domain.Registration;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed partial class AdminApplicationHttpRequestHandler :
        IAdminHttpRequestHandler,
        IDisposable
    {
        private readonly StateMutationCoordinator _stateCoordinator;
        private readonly IAdminConfigurationState _configurationState;
        private readonly IAdminSystemLogSink _systemLog;
        private readonly IAdminSynchronizationController
            _synchronizationController;
        private readonly Func<DateTime> _utcNowProvider;
        private readonly AdminCursorCodec _cursorCodec;
        private readonly ICertificateAuthorityAdministration
            _certificateAuthorityAdministration;
        private readonly RegistrationModeOwner _registrationModeOwner;
        private readonly object _loggingGate = new object();
        private int _disposed;

        public AdminApplicationHttpRequestHandler(
            StateMutationCoordinator stateCoordinator,
            IAdminConfigurationState configurationState,
            SystemFileLogger systemFileLogger,
            IAdminSynchronizationController synchronizationController)
            : this(
                stateCoordinator,
                configurationState,
                new AdminSystemFileLogSink(systemFileLogger),
                synchronizationController,
                () => DateTime.UtcNow,
                new AdminCursorCodec(),
                new UnavailableCertificateAuthorityAdministration(),
                new RegistrationModeOwner(stateCoordinator))
        {
        }

        public AdminApplicationHttpRequestHandler(
            StateMutationCoordinator stateCoordinator,
            IAdminConfigurationState configurationState,
            SystemFileLogger systemFileLogger,
            IAdminSynchronizationController synchronizationController,
            ICertificateAuthorityAdministration
                certificateAuthorityAdministration)
            : this(
                stateCoordinator,
                configurationState,
                new AdminSystemFileLogSink(systemFileLogger),
                synchronizationController,
                () => DateTime.UtcNow,
                new AdminCursorCodec(),
                certificateAuthorityAdministration,
                new RegistrationModeOwner(stateCoordinator))
        {
        }

        public AdminApplicationHttpRequestHandler(
            StateMutationCoordinator stateCoordinator,
            IAdminConfigurationState configurationState,
            SystemFileLogger systemFileLogger,
            IAdminSynchronizationController synchronizationController,
            ICertificateAuthorityAdministration
                certificateAuthorityAdministration,
            RegistrationModeOwner registrationModeOwner)
            : this(
                stateCoordinator,
                configurationState,
                new AdminSystemFileLogSink(systemFileLogger),
                synchronizationController,
                () => DateTime.UtcNow,
                new AdminCursorCodec(),
                certificateAuthorityAdministration,
                registrationModeOwner)
        {
        }

        internal AdminApplicationHttpRequestHandler(
            StateMutationCoordinator stateCoordinator,
            IAdminConfigurationState configurationState,
            IAdminSystemLogSink systemLog,
            IAdminSynchronizationController synchronizationController,
            Func<DateTime> utcNowProvider,
            AdminCursorCodec cursorCodec)
            : this(
                stateCoordinator,
                configurationState,
                systemLog,
                synchronizationController,
                utcNowProvider,
                cursorCodec,
                new UnavailableCertificateAuthorityAdministration(),
                new RegistrationModeOwner(stateCoordinator))
        {
        }

        internal AdminApplicationHttpRequestHandler(
            StateMutationCoordinator stateCoordinator,
            IAdminConfigurationState configurationState,
            IAdminSystemLogSink systemLog,
            IAdminSynchronizationController synchronizationController,
            Func<DateTime> utcNowProvider,
            AdminCursorCodec cursorCodec,
            ICertificateAuthorityAdministration
                certificateAuthorityAdministration)
            : this(
                stateCoordinator,
                configurationState,
                systemLog,
                synchronizationController,
                utcNowProvider,
                cursorCodec,
                certificateAuthorityAdministration,
                new RegistrationModeOwner(stateCoordinator))
        {
        }

        internal AdminApplicationHttpRequestHandler(
            StateMutationCoordinator stateCoordinator,
            IAdminConfigurationState configurationState,
            IAdminSystemLogSink systemLog,
            IAdminSynchronizationController synchronizationController,
            Func<DateTime> utcNowProvider,
            AdminCursorCodec cursorCodec,
            ICertificateAuthorityAdministration
                certificateAuthorityAdministration,
            RegistrationModeOwner registrationModeOwner)
        {
            _stateCoordinator = stateCoordinator
                ?? throw new ArgumentNullException(nameof(stateCoordinator));
            _configurationState = configurationState
                ?? throw new ArgumentNullException(nameof(configurationState));
            _systemLog = systemLog
                ?? throw new ArgumentNullException(nameof(systemLog));
            _synchronizationController = synchronizationController
                ?? throw new ArgumentNullException(
                    nameof(synchronizationController));
            _utcNowProvider = utcNowProvider
                ?? throw new ArgumentNullException(nameof(utcNowProvider));
            _cursorCodec = cursorCodec
                ?? throw new ArgumentNullException(nameof(cursorCodec));
            _certificateAuthorityAdministration =
                certificateAuthorityAdministration
                ?? throw new ArgumentNullException(
                    nameof(certificateAuthorityAdministration));
            _registrationModeOwner = registrationModeOwner
                ?? throw new ArgumentNullException(
                    nameof(registrationModeOwner));
        }

        public AdminHandlerResult<AdminServerUnitResponse> DeleteService(
            string productCode)
        {
            ThrowIfDisposed();
            ProductCode normalizedProductCode;
            if (!ProductCode.TryCreate(
                    productCode,
                    out normalizedProductCode)
                || !StringComparer.Ordinal.Equals(
                    productCode,
                    normalizedProductCode.Value))
            {
                return Failure<AdminServerUnitResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            ProductCode deletedProductCode;
            var certificateServiceAdministration =
                _certificateAuthorityAdministration
                    as ICertificateServiceMutationAdministration;
            if (certificateServiceAdministration != null)
            {
                CertificateServiceDeletionResult deletion =
                    certificateServiceAdministration.DeleteService(
                        normalizedProductCode,
                        GetUtcNow());
                if (deletion == null)
                {
                    return Failure<AdminServerUnitResponse>(
                        AdminServerErrorCode.Internal);
                }

                AdminServerErrorCode? deletionFailure =
                    MapCertificateServiceDeletionStatus(deletion.Status);
                if (deletionFailure.HasValue)
                {
                    return Failure<AdminServerUnitResponse>(
                        deletionFailure.Value);
                }

                deletedProductCode = normalizedProductCode;
            }
            else
            {
                ServiceDirectoryConfiguration configuration =
                    GetCurrentConfiguration();
                StateMutationResult<DeleteResult> mutation =
                    _stateCoordinator.Delete(
                        normalizedProductCode,
                        configuration.InstanceId,
                        GetUtcNow());
                if (!TryGetSuccessfulTransition(
                        mutation,
                        out DeleteResult transition,
                        out AdminServerErrorCode? failure))
                {
                    return Failure<AdminServerUnitResponse>(failure.Value);
                }

                if (!mutation.SnapshotPublished
                    || !mutation.ShouldScheduleSync
                    || !transition.ProductCode.HasValue)
                {
                    throw new InvalidOperationException(
                        "A successful deletion returned an inconsistent result.");
                }

                deletedProductCode = transition.ProductCode.Value;
            }

            bool logSucceeded;
            try
            {
                logSucceeded = WriteDeleteLog(
                    deletedProductCode);
            }
            finally
            {
                _synchronizationController.ScheduleDirectoryChanged();
            }

            return logSucceeded
                ? UnitSuccess()
                : Failure<AdminServerUnitResponse>(
                    AdminServerErrorCode.Internal);
        }

        public AdminHandlerResult<AdminServerSyncStatusResponse>
            GetSyncStatus()
        {
            ThrowIfDisposed();
            return EnsureControllerResult(
                _synchronizationController.GetStatus());
        }

        public AdminHandlerResult<AdminServerUnitResponse> EnableSync(
            AdminEnableSyncRequest request)
        {
            ThrowIfDisposed();
            if (request == null)
            {
                return Failure<AdminServerUnitResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            return EnsureControllerResult(
                _synchronizationController.Enable(request));
        }

        public AdminHandlerResult<AdminServerUnitResponse> ConfirmPairing(
            AdminPairingConfirmationRequest request)
        {
            ThrowIfDisposed();
            if (request == null || !request.Confirmed)
            {
                return Failure<AdminServerUnitResponse>(
                    request == null
                        ? AdminServerErrorCode.BadRequest
                        : AdminServerErrorCode.Conflict);
            }

            return EnsureControllerResult(
                _synchronizationController.ConfirmPairing(request));
        }

        public AdminHandlerResult<AdminServerUnitResponse> CancelPairing(
            AdminPairingCancellationRequest request)
        {
            ThrowIfDisposed();
            if (request == null)
            {
                return Failure<AdminServerUnitResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            return EnsureControllerResult(
                _synchronizationController.CancelPairing(request));
        }

        public AdminHandlerResult<AdminServerSyncDisableResponse> DisableSync(
            AdminDisableSyncRequest request)
        {
            ThrowIfDisposed();
            if (request == null)
            {
                return Failure<AdminServerSyncDisableResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            return EnsureControllerResult(
                _synchronizationController.Disable(request));
        }

        public AdminHandlerResult<AdminServerUnitResponse> SynchronizeNow()
        {
            ThrowIfDisposed();
            return EnsureControllerResult(
                _synchronizationController.SynchronizeNow());
        }

        public AdminHandlerResult<AdminServerLoggingResponse>
            GetLoggingSettings()
        {
            ThrowIfDisposed();
            ServiceDirectoryConfiguration configuration =
                GetCurrentConfiguration();
            return AdminHandlerResult<AdminServerLoggingResponse>.Success(
                new AdminServerLoggingResponse(
                    configuration.LogRetentionDays));
        }

        public AdminHandlerResult<AdminServerLoggingResponse>
            PutLoggingSettings(AdminLoggingSettingsRequest request)
        {
            ThrowIfDisposed();
            if (request == null)
            {
                return Failure<AdminServerLoggingResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            lock (_loggingGate)
            {
                AdminConfigurationUpdateResult update =
                    _configurationState.SetLogRetentionDays(
                        request.LogRetentionDays);
                if (update == null || !update.IsSuccess)
                {
                    return Failure<AdminServerLoggingResponse>(
                        AdminServerErrorCode.Internal);
                }

                if (update.Configuration.LogRetentionDays !=
                    request.LogRetentionDays)
                {
                    throw new InvalidOperationException(
                        "The configuration owner published a different logging setting.");
                }

                try
                {
                    _systemLog.ApplyRetention(request.LogRetentionDays);
                }
                catch (Exception exception) when (IsLogIoFailure(exception))
                {
                    // The durable setting is intentionally retained. A retry
                    // re-applies the same setting and attempts cleanup again.
                    return Failure<AdminServerLoggingResponse>(
                        AdminServerErrorCode.Internal);
                }

                return AdminHandlerResult<AdminServerLoggingResponse>.Success(
                    new AdminServerLoggingResponse(
                        request.LogRetentionDays));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cursorCodec.Dispose();
        }

        private bool WriteDeleteLog(ProductCode productCode)
        {
            try
            {
                lock (_loggingGate)
                {
                    int retentionDays = GetCurrentConfiguration()
                        .LogRetentionDays;
                    _systemLog.WriteRegisteredServiceDeleted(
                        productCode,
                        retentionDays);
                }

                return true;
            }
            catch (SystemLogRetentionAfterWriteException)
            {
                return true;
            }
            catch (Exception exception) when (IsLogIoFailure(exception))
            {
                return false;
            }
        }

        private ServiceDirectoryConfiguration GetCurrentConfiguration()
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            if (configuration == null)
            {
                throw new InvalidOperationException(
                    "The Admin configuration owner returned no current value.");
            }

            return configuration;
        }

        private DateTime GetUtcNow()
        {
            DateTime utcNow = _utcNowProvider();
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new InvalidOperationException(
                    "The Admin clock must return UTC DateTime values.");
            }

            return utcNow;
        }

        private static bool TryGetSuccessfulTransition<TTransition>(
            StateMutationResult<TTransition> mutation,
            out TTransition transition,
            out AdminServerErrorCode? failure)
            where TTransition : StateTransitionResult
        {
            transition = null;
            failure = null;
            if (mutation == null
                || mutation.Status != StateMutationStatus.Completed
                || !mutation.HasDomainTransition)
            {
                failure = AdminServerErrorCode.Internal;
                return false;
            }

            if (!mutation.IsSuccessful)
            {
                failure = mutation.DomainTransition.ErrorCode.HasValue
                    ? MapDomainError(
                        mutation.DomainTransition.ErrorCode.Value)
                    : AdminServerErrorCode.Internal;
                return false;
            }

            transition = mutation.DomainTransition;
            return true;
        }

        private static AdminServerErrorCode MapDomainError(
            DomainErrorCode errorCode)
        {
            switch (errorCode)
            {
                case DomainErrorCode.BadRequest:
                    return AdminServerErrorCode.BadRequest;
                case DomainErrorCode.NotFound:
                    return AdminServerErrorCode.NotFound;
                case DomainErrorCode.Conflict:
                    return AdminServerErrorCode.Conflict;
                case DomainErrorCode.LimitExceeded:
                    return AdminServerErrorCode.LimitExceeded;
                case DomainErrorCode.RevisionCollision:
                    return AdminServerErrorCode.RevisionCollision;
                case DomainErrorCode.DirectoryCapacity:
                    return AdminServerErrorCode.DirectoryCapacity;
                case DomainErrorCode.LogicalClockExhausted:
                    return AdminServerErrorCode.LogicalClockExhausted;
                case DomainErrorCode.Internal:
                default:
                    return AdminServerErrorCode.Internal;
            }
        }

        private static AdminServerErrorCode?
            MapCertificateServiceDeletionStatus(
                CertificateServiceDeletionStatus status)
        {
            switch (status)
            {
                case CertificateServiceDeletionStatus.Deleted:
                    return null;
                case CertificateServiceDeletionStatus.NotFound:
                    return AdminServerErrorCode.NotFound;
                case CertificateServiceDeletionStatus.Conflict:
                    return AdminServerErrorCode.Conflict;
                case CertificateServiceDeletionStatus.LimitExceeded:
                    return AdminServerErrorCode.LimitExceeded;
                default:
                    return AdminServerErrorCode.Internal;
            }
        }

        private static AdminHandlerResult<T> EnsureControllerResult<T>(
            AdminHandlerResult<T> result)
            where T : class
        {
            return result ?? Failure<T>(AdminServerErrorCode.Internal);
        }

        private static AdminHandlerResult<AdminServerUnitResponse>
            UnitSuccess()
        {
            return AdminHandlerResult<AdminServerUnitResponse>.Success(
                AdminServerUnitResponse.Value);
        }

        private static AdminHandlerResult<T> Failure<T>(
            AdminServerErrorCode code)
            where T : class
        {
            return AdminHandlerResult<T>.Failure(code);
        }

        private static bool IsLogIoFailure(Exception exception)
        {
            return exception is IOException
                || exception is UnauthorizedAccessException
                || exception is SecurityException;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(AdminApplicationHttpRequestHandler));
            }
        }

        private sealed class UnavailableCertificateAuthorityAdministration
            : ICertificateAuthorityAdministration
        {
            public CertificateAuthorityStatus GetStatus()
            {
                return new CertificateAuthorityStatus(
                    CertificateAuthorityOperationalState.NotProvisioned);
            }

            public CertificateAuthorityBackupResult CreateBackup(
                string password,
                DateTime createdUtc)
            {
                throw new InvalidOperationException(
                    "The CA administration owner is unavailable.");
            }

            public CertificateLedgerSnapshot GetLedgerSnapshot()
            {
                throw new InvalidOperationException(
                    "The CA administration owner is unavailable.");
            }

            public CertificateRevocationResult Revoke(
                string serialNumber,
                CertificateRevocationReason reason,
                DateTime revokedUtc)
            {
                throw new InvalidOperationException(
                    "The CA administration owner is unavailable.");
            }
        }
    }
}
