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
        InvalidErrorReason
    }

    public enum WatchdogPipeResponseOutcome
    {
        Success = 1,
        Error = 2
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
            WatchdogServiceStatus? serviceStatus,
            string errorReason,
            WatchdogPipeParseFailureCode failureCode)
        {
            if (!isValid)
            {
                if (outcome.HasValue
                    || serviceStatus.HasValue
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
                    || (serviceStatus.HasValue
                        && !Enum.IsDefined(
                            typeof(WatchdogServiceStatus),
                            serviceStatus.Value)))
                {
                    throw new ArgumentException(
                        "A successful watchdog response requires a defined optional status and no error reason.");
                }
            }
            else if (outcome.Value == WatchdogPipeResponseOutcome.Error)
            {
                if (serviceStatus.HasValue || errorReason == null)
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
            ServiceStatus = serviceStatus;
            ErrorReason = errorReason;
            FailureCode = failureCode;
        }

        public bool IsValid { get; }

        public WatchdogPipeResponseOutcome? Outcome { get; }

        public WatchdogServiceStatus? ServiceStatus { get; }

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
            WatchdogServiceStatus serviceStatus)
        {
            return new WatchdogResponseParseResult(
                true,
                WatchdogPipeResponseOutcome.Success,
                serviceStatus,
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
