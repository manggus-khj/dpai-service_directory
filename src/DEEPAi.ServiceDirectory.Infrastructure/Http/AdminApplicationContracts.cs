using System;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public enum AdminConfigurationUpdateStatus
    {
        Completed = 0,
        PersistenceFailed = 1,
        RecoveryRequired = 2,
        BlockedForRecovery = 3
    }

    public sealed class AdminConfigurationUpdateResult
    {
        private AdminConfigurationUpdateResult(
            AdminConfigurationUpdateStatus status,
            ServiceDirectoryConfiguration configuration)
        {
            if (!Enum.IsDefined(
                    typeof(AdminConfigurationUpdateStatus),
                    status))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            bool completed =
                status == AdminConfigurationUpdateStatus.Completed;
            if (completed != (configuration != null))
            {
                throw new ArgumentException(
                    "A completed Admin configuration update must return "
                    + "the durably published configuration.",
                    nameof(configuration));
            }

            Status = status;
            Configuration = configuration;
        }

        public AdminConfigurationUpdateStatus Status { get; }

        public bool IsSuccess =>
            Status == AdminConfigurationUpdateStatus.Completed;

        public ServiceDirectoryConfiguration Configuration { get; }

        public static AdminConfigurationUpdateResult Success(
            ServiceDirectoryConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            return new AdminConfigurationUpdateResult(
                AdminConfigurationUpdateStatus.Completed,
                configuration);
        }

        public static AdminConfigurationUpdateResult Failure(
            AdminConfigurationUpdateStatus status)
        {
            if (status == AdminConfigurationUpdateStatus.Completed)
            {
                throw new ArgumentException(
                    "A failure status is required.",
                    nameof(status));
            }

            return new AdminConfigurationUpdateResult(status, null);
        }
    }

    // The runtime composition implements this interface over its one
    // authoritative config.xml + peer.dat transaction owner. Implementations
    // must not run an independent config.xml baseline beside that owner.
    public interface IAdminConfigurationState
    {
        // Returns the currently published immutable configuration. A runtime
        // unable to prove its current configuration must throw or stop serving
        // Admin requests rather than return a guessed value.
        ServiceDirectoryConfiguration GetCurrent();

        // Success is returned only after LogRetentionDays has been durably
        // committed and the resulting immutable configuration has been
        // published. Reapplying the current value may succeed without a write;
        // this permits retrying retention cleanup after an earlier failure.
        AdminConfigurationUpdateResult SetLogRetentionDays(
            int logRetentionDays);
    }

    // Pairing, peer credential persistence, notification and sync scheduling
    // remain owned by the runtime synchronization component. Its returned
    // errors use the already-defined Admin contract and contain no secrets.
    public interface IAdminSynchronizationController
    {
        AdminHandlerResult<AdminServerSyncStatusResponse> GetStatus();

        AdminHandlerResult<AdminServerUnitResponse> Enable(
            AdminEnableSyncRequest request);

        AdminHandlerResult<AdminServerUnitResponse> ConfirmPairing(
            AdminPairingConfirmationRequest request);

        AdminHandlerResult<AdminServerUnitResponse> CancelPairing(
            AdminPairingCancellationRequest request);

        AdminHandlerResult<AdminServerSyncDisableResponse> Disable(
            AdminDisableSyncRequest request);

        AdminHandlerResult<AdminServerUnitResponse> SynchronizeNow();

        // Accepts a post-commit notification only. Sync execution failures are
        // reported through GetStatus and never roll back the directory commit.
        void ScheduleDirectoryChanged();
    }

    internal interface IAdminSystemLogSink
    {
        void WriteRegisteredServiceCreated(
            DEEPAi.ServiceDirectory.Domain.ProductCode productCode,
            int retentionDays);

        void WriteRegisteredServiceUpdated(
            DEEPAi.ServiceDirectory.Domain.ProductCode productCode,
            int retentionDays);

        void WriteRegisteredServiceDeleted(
            DEEPAi.ServiceDirectory.Domain.ProductCode productCode,
            int retentionDays);

        void ApplyRetention(int retentionDays);
    }

    internal sealed class AdminSystemFileLogSink : IAdminSystemLogSink
    {
        private readonly SystemFileLogger _logger;

        internal AdminSystemFileLogSink(SystemFileLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void WriteRegisteredServiceCreated(
            DEEPAi.ServiceDirectory.Domain.ProductCode productCode,
            int retentionDays)
        {
            _logger.WriteRegisteredServiceCreated(
                productCode,
                retentionDays);
        }

        public void WriteRegisteredServiceUpdated(
            DEEPAi.ServiceDirectory.Domain.ProductCode productCode,
            int retentionDays)
        {
            _logger.WriteRegisteredServiceUpdated(
                productCode,
                retentionDays);
        }

        public void WriteRegisteredServiceDeleted(
            DEEPAi.ServiceDirectory.Domain.ProductCode productCode,
            int retentionDays)
        {
            _logger.WriteRegisteredServiceDeleted(
                productCode,
                retentionDays);
        }

        public void ApplyRetention(int retentionDays)
        {
            _logger.ApplyRetention(retentionDays);
        }
    }
}
