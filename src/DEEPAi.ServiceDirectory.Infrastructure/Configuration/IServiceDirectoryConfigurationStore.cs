using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Configuration
{
    public enum ConfigurationLoadFailureCode
    {
        None = 0,
        Missing,
        InvalidData,
        AccessDenied,
        IoFailure,
        RecoveryFailed
    }

    public enum ConfigurationCommitFailureCode
    {
        None = 0,
        AlreadyInitialized,
        AccessDenied,
        DiskFull,
        IoFailure,
        RecoveryRequired
    }

    public sealed class ConfigurationLoadResult
    {
        private ConfigurationLoadResult(
            bool isSuccess,
            ServiceDirectoryConfiguration configuration,
            ConfigurationLoadFailureCode failureCode)
        {
            IsSuccess = isSuccess;
            Configuration = configuration;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public ServiceDirectoryConfiguration Configuration { get; }

        public ConfigurationLoadFailureCode FailureCode { get; }

        public static ConfigurationLoadResult Success(
            ServiceDirectoryConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            return new ConfigurationLoadResult(
                true,
                configuration,
                ConfigurationLoadFailureCode.None);
        }

        public static ConfigurationLoadResult Failure(
            ConfigurationLoadFailureCode failureCode)
        {
            if (failureCode == ConfigurationLoadFailureCode.None
                || !Enum.IsDefined(
                    typeof(ConfigurationLoadFailureCode),
                    failureCode))
            {
                throw new ArgumentException(
                    "A defined configuration load failure is required.",
                    nameof(failureCode));
            }

            return new ConfigurationLoadResult(
                false,
                null,
                failureCode);
        }
    }

    public sealed class ConfigurationCommitResult
    {
        private ConfigurationCommitResult(
            bool isSuccess,
            ConfigurationCommitFailureCode failureCode)
        {
            IsSuccess = isSuccess;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public ConfigurationCommitFailureCode FailureCode { get; }

        public bool RequiresReload =>
            FailureCode == ConfigurationCommitFailureCode.RecoveryRequired;

        public static ConfigurationCommitResult Success()
        {
            return new ConfigurationCommitResult(
                true,
                ConfigurationCommitFailureCode.None);
        }

        public static ConfigurationCommitResult Failure(
            ConfigurationCommitFailureCode failureCode)
        {
            if (failureCode == ConfigurationCommitFailureCode.None
                || !Enum.IsDefined(
                    typeof(ConfigurationCommitFailureCode),
                    failureCode))
            {
                throw new ArgumentException(
                    "A defined configuration commit failure is required.",
                    nameof(failureCode));
            }

            return new ConfigurationCommitResult(
                false,
                failureCode);
        }
    }

    public interface IServiceDirectoryConfigurationStore
    {
        ConfigurationLoadResult Load();

        // Installation initializes config.xml only after provisioning the exact
        // listener address. A missing configuration is never auto-created by the
        // runtime service.
        ConfigurationCommitResult Initialize(
            ServiceDirectoryConfiguration initialConfiguration);

        // Runtime mutations may update logging and durable synchronization state,
        // but must not change ListenAddress or InstanceId.
        ConfigurationCommitResult Commit(
            ServiceDirectoryConfiguration expectedConfiguration,
            ServiceDirectoryConfiguration nextConfiguration);

        // Only the installer repair workflow may call this narrow operation. The
        // caller still owns service stop, interface/profile validation, exact URL
        // ACL and firewall changes, restart, and rollback as one larger workflow.
        ConfigurationCommitResult CommitListenAddressForRepair(
            ServiceDirectoryConfiguration expectedConfiguration,
            string nextListenAddress);
    }
}
