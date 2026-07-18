using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol
{
    public static class WatchdogPipeCodec
    {
        public const string PipeName = "SvcDirWatchdog";
        public const int MaximumLineBytes = 256;
        public const int TimeoutSeconds = 3;

        private const string SuccessText = "OK";
        private const string StatusSuccessPrefix = "OK: ";
        private const string ErrorPrefix = "ERROR: ";
        private const string LineTerminator = "\r\n";

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeRequest(WatchdogPipeCommand command)
        {
            return EncodeLine(FormatCommand(command));
        }

        public static WatchdogRequestParseResult ParseRequest(byte[] fullLine)
        {
            string text;
            WatchdogPipeParseFailureCode lineFailure;
            if (!TryDecodeLine(fullLine, out text, out lineFailure))
            {
                return WatchdogRequestParseResult.Failure(lineFailure);
            }

            WatchdogPipeCommand command;
            if (TryParseCommand(text, out command))
            {
                return WatchdogRequestParseResult.Success(command);
            }

            return WatchdogRequestParseResult.Failure(
                LooksLikeCommandWithArguments(text)
                    ? WatchdogPipeParseFailureCode.ArgumentsNotAllowed
                    : WatchdogPipeParseFailureCode.UnknownCommand);
        }

        public static byte[] EncodeControlSuccessResponse(
            WatchdogPipeCommand command)
        {
            EnsureControlCommand(command, nameof(command));
            return EncodeLine(SuccessText);
        }

        public static byte[] EncodeStatusSuccessResponse(
            WatchdogPipeCommand command,
            WatchdogStatusSnapshot statusSnapshot)
        {
            EnsureStatusCommand(command, nameof(command));
            if (statusSnapshot == null)
            {
                throw new ArgumentNullException(nameof(statusSnapshot));
            }

            return EncodeLine(FormatStatusResponse(statusSnapshot));
        }

        public static byte[] EncodeErrorResponse(string userReason)
        {
            ValidateErrorReason(userReason, nameof(userReason));
            return EncodeLine(ErrorPrefix + userReason);
        }

        public static WatchdogResponseParseResult ParseResponse(
            byte[] fullLine,
            WatchdogPipeCommand expectedCommand)
        {
            EnsureDefinedCommand(expectedCommand, nameof(expectedCommand));

            string text;
            WatchdogPipeParseFailureCode lineFailure;
            if (!TryDecodeLine(fullLine, out text, out lineFailure))
            {
                return WatchdogResponseParseResult.Failure(lineFailure);
            }

            if (text.StartsWith(ErrorPrefix, StringComparison.Ordinal))
            {
                string reason = text.Substring(ErrorPrefix.Length);
                if (!IsValidErrorReason(reason))
                {
                    return WatchdogResponseParseResult.Failure(
                        WatchdogPipeParseFailureCode.InvalidErrorReason);
                }

                return WatchdogResponseParseResult.Error(reason);
            }

            if (StringComparer.Ordinal.Equals(text, SuccessText))
            {
                return expectedCommand == WatchdogPipeCommand.Status
                    ? WatchdogResponseParseResult.Failure(
                        WatchdogPipeParseFailureCode.UnexpectedSuccessShape)
                    : WatchdogResponseParseResult.SuccessWithoutStatus();
            }

            if (text.StartsWith(StatusSuccessPrefix, StringComparison.Ordinal))
            {
                if (expectedCommand != WatchdogPipeCommand.Status)
                {
                    return WatchdogResponseParseResult.Failure(
                        WatchdogPipeParseFailureCode.UnexpectedSuccessShape);
                }

                WatchdogStatusSnapshot statusSnapshot;
                WatchdogPipeParseFailureCode statusFailure;
                if (!TryParseStatusSnapshot(
                    text.Substring(StatusSuccessPrefix.Length),
                    out statusSnapshot,
                    out statusFailure))
                {
                    return WatchdogResponseParseResult.Failure(
                        statusFailure);
                }

                return WatchdogResponseParseResult.SuccessWithStatus(
                    statusSnapshot);
            }

            return WatchdogResponseParseResult.Failure(
                WatchdogPipeParseFailureCode.UnexpectedResponse);
        }

        private static bool TryDecodeLine(
            byte[] fullLine,
            out string text,
            out WatchdogPipeParseFailureCode failureCode)
        {
            text = null;
            failureCode = WatchdogPipeParseFailureCode.None;

            if (fullLine == null || fullLine.Length == 0)
            {
                failureCode = WatchdogPipeParseFailureCode.EmptyInput;
                return false;
            }

            if (fullLine.Length > MaximumLineBytes)
            {
                failureCode = WatchdogPipeParseFailureCode.MaximumLineBytesExceeded;
                return false;
            }

            if (fullLine[fullLine.Length - 1] != (byte)'\n')
            {
                failureCode = WatchdogPipeParseFailureCode.MissingLineTerminator;
                return false;
            }

            int contentLength = fullLine.Length - 1;
            if (contentLength > 0 && fullLine[contentLength - 1] == (byte)'\r')
            {
                contentLength--;
            }

            if (contentLength == 0)
            {
                failureCode = WatchdogPipeParseFailureCode.EmptyLine;
                return false;
            }

            for (int index = 0; index < contentLength; index++)
            {
                byte current = fullLine[index];
                if (current == (byte)'\r' || current == (byte)'\n')
                {
                    failureCode = WatchdogPipeParseFailureCode.InvalidLineFraming;
                    return false;
                }

                if (current == 0)
                {
                    failureCode = WatchdogPipeParseFailureCode.NulNotAllowed;
                    return false;
                }
            }

            if (contentLength >= 3
                && fullLine[0] == 0xEF
                && fullLine[1] == 0xBB
                && fullLine[2] == 0xBF)
            {
                failureCode = WatchdogPipeParseFailureCode.Utf8BomNotAllowed;
                return false;
            }

            try
            {
                text = StrictUtf8.GetString(fullLine, 0, contentLength);
                return true;
            }
            catch (DecoderFallbackException)
            {
                failureCode = WatchdogPipeParseFailureCode.InvalidUtf8;
                return false;
            }
        }

        private static byte[] EncodeLine(string text)
        {
            byte[] encoded;
            try
            {
                encoded = StrictUtf8.GetBytes(text + LineTerminator);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException(
                    "The watchdog pipe line contains invalid Unicode text.",
                    nameof(text),
                    exception);
            }

            if (encoded.Length > MaximumLineBytes)
            {
                throw new ArgumentException(
                    "The encoded watchdog pipe line exceeds the maximum byte length.",
                    nameof(text));
            }

            return encoded;
        }

        private static string FormatCommand(WatchdogPipeCommand command)
        {
            switch (command)
            {
                case WatchdogPipeCommand.Start:
                    return "START";
                case WatchdogPipeCommand.Stop:
                    return "STOP";
                case WatchdogPipeCommand.Restart:
                    return "RESTART";
                case WatchdogPipeCommand.Status:
                    return "STATUS";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(command),
                        command,
                        "A defined watchdog pipe command is required.");
            }
        }

        private static bool TryParseCommand(
            string text,
            out WatchdogPipeCommand command)
        {
            if (StringComparer.Ordinal.Equals(text, "START"))
            {
                command = WatchdogPipeCommand.Start;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "STOP"))
            {
                command = WatchdogPipeCommand.Stop;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "RESTART"))
            {
                command = WatchdogPipeCommand.Restart;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "STATUS"))
            {
                command = WatchdogPipeCommand.Status;
                return true;
            }

            command = default(WatchdogPipeCommand);
            return false;
        }

        private static bool LooksLikeCommandWithArguments(string text)
        {
            return HasArgumentSeparatorAfter(text, "START")
                || HasArgumentSeparatorAfter(text, "STOP")
                || HasArgumentSeparatorAfter(text, "RESTART")
                || HasArgumentSeparatorAfter(text, "STATUS");
        }

        private static bool HasArgumentSeparatorAfter(
            string text,
            string commandText)
        {
            return text.Length > commandText.Length
                && text.StartsWith(commandText, StringComparison.Ordinal)
                && char.IsWhiteSpace(text[commandText.Length]);
        }

        private static void EnsureDefinedCommand(
            WatchdogPipeCommand command,
            string parameterName)
        {
            switch (command)
            {
                case WatchdogPipeCommand.Start:
                case WatchdogPipeCommand.Stop:
                case WatchdogPipeCommand.Restart:
                case WatchdogPipeCommand.Status:
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        parameterName,
                        command,
                        "A defined watchdog pipe command is required.");
            }
        }

        private static void EnsureControlCommand(
            WatchdogPipeCommand command,
            string parameterName)
        {
            EnsureDefinedCommand(command, parameterName);
            if (command == WatchdogPipeCommand.Status)
            {
                throw new ArgumentException(
                    "STATUS success requires a service status response.",
                    parameterName);
            }
        }

        private static void EnsureStatusCommand(
            WatchdogPipeCommand command,
            string parameterName)
        {
            EnsureDefinedCommand(command, parameterName);
            if (command != WatchdogPipeCommand.Status)
            {
                throw new ArgumentException(
                    "A service status can be returned only for STATUS.",
                    parameterName);
            }
        }

        private static string FormatStatus(WatchdogServiceStatus serviceStatus)
        {
            switch (serviceStatus)
            {
                case WatchdogServiceStatus.Stopped:
                    return "STOPPED";
                case WatchdogServiceStatus.StartPending:
                    return "START_PENDING";
                case WatchdogServiceStatus.StopPending:
                    return "STOP_PENDING";
                case WatchdogServiceStatus.Running:
                    return "RUNNING";
                case WatchdogServiceStatus.ContinuePending:
                    return "CONTINUE_PENDING";
                case WatchdogServiceStatus.PausePending:
                    return "PAUSE_PENDING";
                case WatchdogServiceStatus.Paused:
                    return "PAUSED";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(serviceStatus),
                        serviceStatus,
                        "A defined watchdog service status is required.");
            }
        }

        private static string FormatStatusResponse(
            WatchdogStatusSnapshot statusSnapshot)
        {
            var builder = new StringBuilder();
            builder.Append(StatusSuccessPrefix);
            builder.Append(FormatStatus(statusSnapshot.ServiceStatus));
            builder.Append(";HEALTH=");
            builder.Append(FormatHealthStatus(statusSnapshot.HealthStatus));
            builder.Append(";FAILURES=");
            builder.Append(statusSnapshot.ConsecutiveFailures.ToString(
                CultureInfo.InvariantCulture));
            builder.Append(";RESTARTS_10M=");
            builder.Append(statusSnapshot.RestartCountInTenMinutes.ToString(
                CultureInfo.InvariantCulture));
            builder.Append(";AUTO_RESTART=");
            builder.Append(FormatAutoRestartStatus(
                statusSnapshot.AutoRestartStatus));

            if (statusSnapshot.LastHealthUtc.HasValue)
            {
                builder.Append(";LAST_HEALTH_UTC=");
                builder.Append(statusSnapshot.LastHealthUtc.Value.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string FormatHealthStatus(
            WatchdogHealthStatus healthStatus)
        {
            switch (healthStatus)
            {
                case WatchdogHealthStatus.NotRun:
                    return "NOT_RUN";
                case WatchdogHealthStatus.Ok:
                    return "OK";
                case WatchdogHealthStatus.Failed:
                    return "FAILED";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(healthStatus),
                        healthStatus,
                        "A defined watchdog health status is required.");
            }
        }

        private static string FormatAutoRestartStatus(
            WatchdogAutoRestartStatus autoRestartStatus)
        {
            switch (autoRestartStatus)
            {
                case WatchdogAutoRestartStatus.Enabled:
                    return "ENABLED";
                case WatchdogAutoRestartStatus.Suppressed:
                    return "SUPPRESSED";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(autoRestartStatus),
                        autoRestartStatus,
                        "A defined automatic restart status is required.");
            }
        }

        private static bool TryParseStatus(
            string text,
            out WatchdogServiceStatus serviceStatus)
        {
            if (StringComparer.Ordinal.Equals(text, "STOPPED"))
            {
                serviceStatus = WatchdogServiceStatus.Stopped;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "START_PENDING"))
            {
                serviceStatus = WatchdogServiceStatus.StartPending;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "STOP_PENDING"))
            {
                serviceStatus = WatchdogServiceStatus.StopPending;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "RUNNING"))
            {
                serviceStatus = WatchdogServiceStatus.Running;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "CONTINUE_PENDING"))
            {
                serviceStatus = WatchdogServiceStatus.ContinuePending;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "PAUSE_PENDING"))
            {
                serviceStatus = WatchdogServiceStatus.PausePending;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "PAUSED"))
            {
                serviceStatus = WatchdogServiceStatus.Paused;
                return true;
            }

            serviceStatus = default(WatchdogServiceStatus);
            return false;
        }

        private static bool TryParseStatusSnapshot(
            string text,
            out WatchdogStatusSnapshot statusSnapshot,
            out WatchdogPipeParseFailureCode failureCode)
        {
            statusSnapshot = null;
            failureCode = WatchdogPipeParseFailureCode.None;

            string[] segments = text.Split(';');
            WatchdogServiceStatus serviceStatus;
            if (segments.Length == 0
                || !TryParseStatus(segments[0], out serviceStatus))
            {
                failureCode = WatchdogPipeParseFailureCode.InvalidStatus;
                return false;
            }

            WatchdogHealthStatus? healthStatus = null;
            int? consecutiveFailures = null;
            int? restartCount = null;
            WatchdogAutoRestartStatus? autoRestartStatus = null;
            DateTimeOffset? lastHealthUtc = null;
            bool lastHealthSeen = false;
            int nextKnownFieldIndex = 0;
            bool extensionFieldsStarted = false;
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            for (int index = 1; index < segments.Length; index++)
            {
                string segment = segments[index];
                int separatorIndex = segment.IndexOf('=');
                if (separatorIndex <= 0
                    || separatorIndex == segment.Length - 1)
                {
                    failureCode =
                        WatchdogPipeParseFailureCode.InvalidStatusExtension;
                    return false;
                }

                string key = segment.Substring(0, separatorIndex);
                string value = segment.Substring(separatorIndex + 1);
                if (!IsValidStatusExtension(key, value))
                {
                    failureCode =
                        WatchdogPipeParseFailureCode.InvalidStatusExtension;
                    return false;
                }

                if (!seenKeys.Add(key))
                {
                    failureCode =
                        WatchdogPipeParseFailureCode.DuplicateStatusField;
                    return false;
                }

                switch (key)
                {
                    case "HEALTH":
                        if (extensionFieldsStarted || nextKnownFieldIndex != 0)
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidStatusFieldOrder;
                            return false;
                        }

                        WatchdogHealthStatus parsedHealthStatus;
                        if (!TryParseHealthStatus(value, out parsedHealthStatus))
                        {
                            failureCode =
                                WatchdogPipeParseFailureCode.InvalidHealthStatus;
                            return false;
                        }

                        healthStatus = parsedHealthStatus;
                        nextKnownFieldIndex++;
                        break;
                    case "FAILURES":
                        if (extensionFieldsStarted || nextKnownFieldIndex != 1)
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidStatusFieldOrder;
                            return false;
                        }

                        int parsedFailureCount;
                        if (!TryParseNonNegativeCounter(
                            value,
                            int.MaxValue,
                            out parsedFailureCount))
                        {
                            failureCode =
                                WatchdogPipeParseFailureCode.InvalidStatusCounter;
                            return false;
                        }

                        consecutiveFailures = parsedFailureCount;
                        nextKnownFieldIndex++;
                        break;
                    case "RESTARTS_10M":
                        if (extensionFieldsStarted || nextKnownFieldIndex != 2)
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidStatusFieldOrder;
                            return false;
                        }

                        int parsedRestartCount;
                        if (!TryParseNonNegativeCounter(
                            value,
                            3,
                            out parsedRestartCount))
                        {
                            failureCode =
                                WatchdogPipeParseFailureCode.InvalidStatusCounter;
                            return false;
                        }

                        restartCount = parsedRestartCount;
                        nextKnownFieldIndex++;
                        break;
                    case "AUTO_RESTART":
                        if (extensionFieldsStarted || nextKnownFieldIndex != 3)
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidStatusFieldOrder;
                            return false;
                        }

                        WatchdogAutoRestartStatus parsedAutoRestartStatus;
                        if (!TryParseAutoRestartStatus(
                            value,
                            out parsedAutoRestartStatus))
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidAutoRestartStatus;
                            return false;
                        }

                        autoRestartStatus = parsedAutoRestartStatus;
                        nextKnownFieldIndex++;
                        break;
                    case "LAST_HEALTH_UTC":
                        if (extensionFieldsStarted || nextKnownFieldIndex != 4)
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidStatusFieldOrder;
                            return false;
                        }

                        DateTimeOffset parsedLastHealthUtc;
                        if (!DateTimeOffset.TryParseExact(
                            value,
                            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal
                                | DateTimeStyles.AdjustToUniversal,
                            out parsedLastHealthUtc))
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidStatusTimestamp;
                            return false;
                        }

                        lastHealthSeen = true;
                        lastHealthUtc = parsedLastHealthUtc;
                        nextKnownFieldIndex++;
                        break;
                    default:
                        bool knownFieldsComplete = nextKnownFieldIndex == 5
                            || (nextKnownFieldIndex == 4
                                && healthStatus == WatchdogHealthStatus.NotRun);
                        if (!knownFieldsComplete)
                        {
                            failureCode = WatchdogPipeParseFailureCode
                                .InvalidStatusFieldOrder;
                            return false;
                        }

                        // Future diagnostic keys are ignored only after all
                        // conditionally required known fields.
                        extensionFieldsStarted = true;
                        break;
                }
            }

            if (!healthStatus.HasValue
                || !consecutiveFailures.HasValue
                || !restartCount.HasValue
                || !autoRestartStatus.HasValue)
            {
                failureCode = WatchdogPipeParseFailureCode.MissingStatusField;
                return false;
            }

            if ((healthStatus.Value == WatchdogHealthStatus.NotRun
                    && lastHealthSeen)
                || (healthStatus.Value != WatchdogHealthStatus.NotRun
                    && !lastHealthSeen))
            {
                failureCode =
                    WatchdogPipeParseFailureCode.InvalidStatusTimestamp;
                return false;
            }

            try
            {
                statusSnapshot = new WatchdogStatusSnapshot(
                    serviceStatus,
                    healthStatus.Value,
                    consecutiveFailures.Value,
                    restartCount.Value,
                    autoRestartStatus.Value,
                    lastHealthUtc);
                return true;
            }
            catch (ArgumentException)
            {
                failureCode =
                    WatchdogPipeParseFailureCode.InvalidStatusCombination;
                return false;
            }
        }

        private static bool TryParseHealthStatus(
            string text,
            out WatchdogHealthStatus healthStatus)
        {
            if (StringComparer.Ordinal.Equals(text, "NOT_RUN"))
            {
                healthStatus = WatchdogHealthStatus.NotRun;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "OK"))
            {
                healthStatus = WatchdogHealthStatus.Ok;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "FAILED"))
            {
                healthStatus = WatchdogHealthStatus.Failed;
                return true;
            }

            healthStatus = default(WatchdogHealthStatus);
            return false;
        }

        private static bool TryParseAutoRestartStatus(
            string text,
            out WatchdogAutoRestartStatus autoRestartStatus)
        {
            if (StringComparer.Ordinal.Equals(text, "ENABLED"))
            {
                autoRestartStatus = WatchdogAutoRestartStatus.Enabled;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "SUPPRESSED"))
            {
                autoRestartStatus = WatchdogAutoRestartStatus.Suppressed;
                return true;
            }

            autoRestartStatus = default(WatchdogAutoRestartStatus);
            return false;
        }

        private static bool TryParseNonNegativeCounter(
            string text,
            int maximum,
            out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)
                || (text.Length > 1 && text[0] == '0'))
            {
                return false;
            }

            return int.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                && value >= 0
                && value <= maximum;
        }

        private static bool IsValidStatusExtension(
            string key,
            string value)
        {
            if (string.IsNullOrEmpty(key)
                || key.Length > 32
                || string.IsNullOrEmpty(value)
                || value.Length > 96)
            {
                return false;
            }

            for (int index = 0; index < key.Length; index++)
            {
                char current = key[index];
                if (!((current >= 'A' && current <= 'Z')
                    || (index > 0 && current >= '0' && current <= '9')
                    || (index > 0 && current == '_')))
                {
                    return false;
                }
            }

            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current < '!' || current > '~' || current == ';')
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateErrorReason(
            string reason,
            string parameterName)
        {
            if (!IsValidErrorReason(reason))
            {
                throw new ArgumentException(
                    "The watchdog error reason must be non-empty, single-line valid Unicode text.",
                    parameterName);
            }
        }

        private static bool IsValidErrorReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            for (int index = 0; index < reason.Length; index++)
            {
                char current = reason[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= reason.Length
                        || !char.IsLowSurrogate(reason[index + 1]))
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (char.IsLowSurrogate(current)
                    || char.IsControl(current)
                    || current == '\u2028'
                    || current == '\u2029')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
