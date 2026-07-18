using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol
{
    public enum WatchdogPipeCommand
    {
        Start = 1,
        Stop = 2,
        Restart = 3,
        Status = 4
    }

    public enum WatchdogServiceStatus
    {
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4,
        ContinuePending = 5,
        PausePending = 6,
        Paused = 7
    }

    public enum WatchdogHealthStatus
    {
        NotRun = 1,
        Ok = 2,
        Failed = 3
    }

    public enum WatchdogAutoRestartStatus
    {
        Enabled = 1,
        Suppressed = 2
    }

    public enum WatchdogPipeParseFailureCode
    {
        None = 0,
        EmptyInput,
        EmptyLine,
        MaximumLineBytesExceeded,
        MissingLineTerminator,
        InvalidLineFraming,
        Utf8BomNotAllowed,
        InvalidUtf8,
        NulNotAllowed,
        UnknownCommand,
        ArgumentsNotAllowed,
        UnexpectedResponse,
        UnexpectedSuccessShape,
        InvalidStatus,
        InvalidErrorReason,
        MissingStatusField,
        DuplicateStatusField,
        InvalidHealthStatus,
        InvalidStatusCounter,
        InvalidAutoRestartStatus,
        InvalidStatusTimestamp,
        InvalidStatusExtension,
        InvalidStatusCombination,
        InvalidStatusFieldOrder
    }

    public enum WatchdogPipeResponseOutcome
    {
        Success = 1,
        Error = 2
    }

    public sealed class WatchdogStatusSnapshot
    {
        public WatchdogStatusSnapshot(
            WatchdogServiceStatus serviceStatus,
            WatchdogHealthStatus healthStatus,
            int consecutiveFailures,
            int restartCountInTenMinutes,
            WatchdogAutoRestartStatus autoRestartStatus,
            DateTimeOffset? lastHealthUtc)
        {
            if (!Enum.IsDefined(typeof(WatchdogServiceStatus), serviceStatus))
            {
                throw new ArgumentOutOfRangeException(nameof(serviceStatus));
            }

            if (!Enum.IsDefined(typeof(WatchdogHealthStatus), healthStatus))
            {
                throw new ArgumentOutOfRangeException(nameof(healthStatus));
            }

            if (!Enum.IsDefined(
                typeof(WatchdogAutoRestartStatus),
                autoRestartStatus))
            {
                throw new ArgumentOutOfRangeException(nameof(autoRestartStatus));
            }

            if (consecutiveFailures < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
            }

            if (restartCountInTenMinutes < 0 || restartCountInTenMinutes > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(restartCountInTenMinutes));
            }

            if (healthStatus == WatchdogHealthStatus.NotRun)
            {
                if (consecutiveFailures != 0 || lastHealthUtc.HasValue)
                {
                    throw new ArgumentException(
                        "NOT_RUN health requires zero failures and no last health timestamp.");
                }
            }
            else
            {
                if (!lastHealthUtc.HasValue
                    || lastHealthUtc.Value.Offset != TimeSpan.Zero
                    || lastHealthUtc.Value.Ticks % TimeSpan.TicksPerMillisecond != 0)
                {
                    throw new ArgumentException(
                        "A completed health check requires a millisecond-precision UTC timestamp.");
                }

                if (healthStatus == WatchdogHealthStatus.Ok
                    && consecutiveFailures != 0)
                {
                    throw new ArgumentException(
                        "OK health requires zero consecutive failures.");
                }

                if (healthStatus == WatchdogHealthStatus.Failed
                    && consecutiveFailures == 0)
                {
                    throw new ArgumentException(
                        "FAILED health requires at least one consecutive failure.");
                }
            }

            if (autoRestartStatus == WatchdogAutoRestartStatus.Enabled
                && restartCountInTenMinutes == 3)
            {
                throw new ArgumentException(
                    "Three restarts in ten minutes require automatic restart suppression.");
            }

            ServiceStatus = serviceStatus;
            HealthStatus = healthStatus;
            ConsecutiveFailures = consecutiveFailures;
            RestartCountInTenMinutes = restartCountInTenMinutes;
            AutoRestartStatus = autoRestartStatus;
            LastHealthUtc = lastHealthUtc;
        }

        public WatchdogServiceStatus ServiceStatus { get; }

        public WatchdogHealthStatus HealthStatus { get; }

        public int ConsecutiveFailures { get; }

        public int RestartCountInTenMinutes { get; }

        public WatchdogAutoRestartStatus AutoRestartStatus { get; }

        public DateTimeOffset? LastHealthUtc { get; }
    }

    public sealed class WatchdogRequestParseResult
    {
        private WatchdogRequestParseResult(
            bool isSuccess,
            WatchdogPipeCommand? command,
            WatchdogPipeParseFailureCode failureCode)
        {
            if (isSuccess)
            {
                if (!command.HasValue
                    || !Enum.IsDefined(typeof(WatchdogPipeCommand), command.Value)
                    || failureCode != WatchdogPipeParseFailureCode.None)
                {
                    throw new ArgumentException(
                        "A successful watchdog request result requires only a command.");
                }
            }
            else if (command.HasValue
                || failureCode == WatchdogPipeParseFailureCode.None
                || !Enum.IsDefined(typeof(WatchdogPipeParseFailureCode), failureCode))
            {
                throw new ArgumentException(
                    "A failed watchdog request result requires only a defined failure code.");
            }

            IsSuccess = isSuccess;
            Command = command;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public WatchdogPipeCommand? Command { get; }

        public WatchdogPipeParseFailureCode FailureCode { get; }

        internal static WatchdogRequestParseResult Success(
            WatchdogPipeCommand command)
        {
            return new WatchdogRequestParseResult(
                true,
                command,
                WatchdogPipeParseFailureCode.None);
        }

        internal static WatchdogRequestParseResult Failure(
            WatchdogPipeParseFailureCode failureCode)
        {
            return new WatchdogRequestParseResult(false, null, failureCode);
        }
    }

    public sealed class WatchdogResponseParseResult
    {
        private WatchdogResponseParseResult(
            bool isValid,
            WatchdogPipeResponseOutcome? outcome,
            WatchdogStatusSnapshot statusSnapshot,
            string errorReason,
            WatchdogPipeParseFailureCode failureCode)
        {
            if (!isValid)
            {
                if (outcome.HasValue
                    || statusSnapshot != null
                    || errorReason != null
                    || failureCode == WatchdogPipeParseFailureCode.None
                    || !Enum.IsDefined(typeof(WatchdogPipeParseFailureCode), failureCode))
                {
                    throw new ArgumentException(
                        "An invalid watchdog response result requires only a defined failure code.");
                }
            }
            else if (!outcome.HasValue
                || failureCode != WatchdogPipeParseFailureCode.None)
            {
                throw new ArgumentException(
                    "A valid watchdog response result requires an outcome and no failure code.");
            }
            else if (outcome.Value == WatchdogPipeResponseOutcome.Success)
            {
                if (errorReason != null
                    || (statusSnapshot != null
                        && !Enum.IsDefined(
                            typeof(WatchdogServiceStatus),
                            statusSnapshot.ServiceStatus)))
                {
                    throw new ArgumentException(
                        "A successful watchdog response requires a defined optional status and no error reason.");
                }
            }
            else if (outcome.Value == WatchdogPipeResponseOutcome.Error)
            {
                if (statusSnapshot != null || errorReason == null)
                {
                    throw new ArgumentException(
                        "An error watchdog response requires only an error reason.");
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outcome),
                    outcome,
                    "A defined watchdog response outcome is required.");
            }

            IsValid = isValid;
            Outcome = outcome;
            StatusSnapshot = statusSnapshot;
            ErrorReason = errorReason;
            FailureCode = failureCode;
        }

        public bool IsValid { get; }

        public WatchdogPipeResponseOutcome? Outcome { get; }

        public WatchdogStatusSnapshot StatusSnapshot { get; }

        public WatchdogServiceStatus? ServiceStatus => StatusSnapshot == null
            ? (WatchdogServiceStatus?)null
            : StatusSnapshot.ServiceStatus;

        public string ErrorReason { get; }

        public WatchdogPipeParseFailureCode FailureCode { get; }

        internal static WatchdogResponseParseResult SuccessWithoutStatus()
        {
            return new WatchdogResponseParseResult(
                true,
                WatchdogPipeResponseOutcome.Success,
                null,
                null,
                WatchdogPipeParseFailureCode.None);
        }

        internal static WatchdogResponseParseResult SuccessWithStatus(
            WatchdogStatusSnapshot statusSnapshot)
        {
            if (statusSnapshot == null)
            {
                throw new ArgumentNullException(nameof(statusSnapshot));
            }

            return new WatchdogResponseParseResult(
                true,
                WatchdogPipeResponseOutcome.Success,
                statusSnapshot,
                null,
                WatchdogPipeParseFailureCode.None);
        }

        internal static WatchdogResponseParseResult Error(string errorReason)
        {
            if (errorReason == null)
            {
                throw new ArgumentNullException(nameof(errorReason));
            }

            return new WatchdogResponseParseResult(
                true,
                WatchdogPipeResponseOutcome.Error,
                null,
                errorReason,
                WatchdogPipeParseFailureCode.None);
        }

        internal static WatchdogResponseParseResult Failure(
            WatchdogPipeParseFailureCode failureCode)
        {
            return new WatchdogResponseParseResult(
                false,
                null,
                null,
                null,
                failureCode);
        }
    }
}
