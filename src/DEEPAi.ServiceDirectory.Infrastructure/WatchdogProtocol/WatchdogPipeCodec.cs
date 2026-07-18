using System;
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
            WatchdogServiceStatus serviceStatus)
        {
            EnsureStatusCommand(command, nameof(command));
            return EncodeLine(StatusSuccessPrefix + FormatStatus(serviceStatus));
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

                WatchdogServiceStatus serviceStatus;
                if (!TryParseStatus(
                    text.Substring(StatusSuccessPrefix.Length),
                    out serviceStatus))
                {
                    return WatchdogResponseParseResult.Failure(
                        WatchdogPipeParseFailureCode.InvalidStatus);
                }

                return WatchdogResponseParseResult.SuccessWithStatus(serviceStatus);
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
