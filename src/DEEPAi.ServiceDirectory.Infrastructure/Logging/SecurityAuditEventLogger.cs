using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DEEPAi.ServiceDirectory.Infrastructure.Logging
{
    public sealed class SecurityAuditSourceUnavailableException
        : InvalidOperationException
    {
        internal SecurityAuditSourceUnavailableException(string message)
            : base(message)
        {
        }

        internal SecurityAuditSourceUnavailableException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class SecurityAuditWriteException : InvalidOperationException
    {
        internal SecurityAuditWriteException(Exception innerException)
            : base(
                "The security audit event could not be written to Windows Event Log.",
                innerException)
        {
        }
    }

    public sealed class SecurityAuditEventLogger
    {
        public const string EventLogName = "Application";
        public const string EventSourceName =
            "DEEPAi.ServiceDirectory.Security";

        internal const int MaximumMessageBytes = 2048;

        private const string EventSourceRegistryPath =
            @"SYSTEM\CurrentControlSet\Services\EventLog\Application\"
            + EventSourceName;
        private const string UnknownValue = "UNKNOWN";
        private readonly Guid _serviceInstanceId;
        private readonly SecurityAuditRateLimiter _rateLimiter;
        private readonly Func<string> _registeredSourceLogNameProvider;
        private readonly Action<string, EventLogEntryType, int, short>
            _entryWriter;

        public SecurityAuditEventLogger(Guid serviceInstanceId)
            : this(
                serviceInstanceId,
                new SecurityAuditRateLimiter(),
                GetRegisteredSourceLogName,
                WriteEventLogEntry)
        {
        }

        internal SecurityAuditEventLogger(
            Guid serviceInstanceId,
            SecurityAuditRateLimiter rateLimiter,
            Func<string> registeredSourceLogNameProvider,
            Action<string, EventLogEntryType, int, short> entryWriter)
        {
            if (serviceInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Service instance ID must not be empty.",
                    nameof(serviceInstanceId));
            }

            if (rateLimiter == null)
            {
                throw new ArgumentNullException(nameof(rateLimiter));
            }

            if (registeredSourceLogNameProvider == null)
            {
                throw new ArgumentNullException(
                    nameof(registeredSourceLogNameProvider));
            }

            if (entryWriter == null)
            {
                throw new ArgumentNullException(nameof(entryWriter));
            }

            EnsureSourceIsRegistered(registeredSourceLogNameProvider);

            _serviceInstanceId = serviceInstanceId;
            _rateLimiter = rateLimiter;
            _registeredSourceLogNameProvider =
                registeredSourceLogNameProvider;
            _entryWriter = entryWriter;
        }

        public bool WriteFailure(
            SecurityAuditEventId eventId,
            SecurityAuditBoundary boundary,
            SecurityAuditOperation operation,
            SecurityAuditReason reason,
            Guid requestId,
            SecurityIdentifier actorSid,
            IPAddress remoteAddress)
        {
            if (requestId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Request ID must not be empty.",
                    nameof(requestId));
            }

            SecurityAuditContract.Validate(
                eventId,
                boundary,
                operation,
                reason);
            ValidateActorSid(eventId, reason, actorSid);

            string canonicalRemoteAddress = FormatRemoteAddress(
                boundary,
                remoteAddress);
            var rateLimitKey = new SecurityAuditRateLimitKey(
                eventId,
                boundary,
                canonicalRemoteAddress);
            SecurityAuditRateLimitDecision decision = _rateLimiter.Evaluate(
                rateLimitKey);
            if (!decision.ShouldWrite)
            {
                return false;
            }

            string message = BuildMessage(
                eventId,
                boundary,
                operation,
                reason,
                requestId,
                actorSid,
                canonicalRemoteAddress,
                decision.SuppressedCount);
            ValidateMessage(message);
            EnsureSourceIsRegistered(_registeredSourceLogNameProvider);

            try
            {
                _entryWriter(
                    message,
                    EventLogEntryType.FailureAudit,
                    (int)eventId,
                    0);
            }
            catch (Exception exception)
                when (IsEventLogInfrastructureFailure(exception))
            {
                throw new SecurityAuditWriteException(exception);
            }

            return true;
        }

        private string BuildMessage(
            SecurityAuditEventId eventId,
            SecurityAuditBoundary boundary,
            SecurityAuditOperation operation,
            SecurityAuditReason reason,
            Guid requestId,
            SecurityIdentifier actorSid,
            string canonicalRemoteAddress,
            long suppressedCount)
        {
            string actorSidValue = actorSid == null
                ? UnknownValue
                : actorSid.Value;

            return string.Concat(
                "Schema=1",
                " Event=", SecurityAuditContract.FormatEvent(eventId),
                " Boundary=", SecurityAuditContract.FormatBoundary(boundary),
                " Operation=", SecurityAuditContract.FormatOperation(operation),
                " Reason=", SecurityAuditContract.FormatReason(reason),
                " Outcome=REJECTED",
                " ServiceInstanceId=", _serviceInstanceId.ToString("D"),
                " RequestId=", requestId.ToString("D"),
                " ActorSid=", actorSidValue,
                " RemoteAddress=", canonicalRemoteAddress,
                " SuppressedCount=", suppressedCount.ToString(
                    CultureInfo.InvariantCulture));
        }

        private static void EnsureSourceIsRegistered(
            Func<string> registeredSourceLogNameProvider)
        {
            string registeredLogName;
            try
            {
                registeredLogName = registeredSourceLogNameProvider();
            }
            catch (Exception exception)
                when (IsEventLogInfrastructureFailure(exception))
            {
                throw new SecurityAuditSourceUnavailableException(
                    "The security audit event source mapping could not be read.",
                    exception);
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                registeredLogName,
                EventLogName))
            {
                throw new SecurityAuditSourceUnavailableException(
                    "The security audit event source is not registered to the Application log.");
            }
        }

        private static string GetRegisteredSourceLogName()
        {
            using (RegistryKey sourceKey = Registry.LocalMachine.OpenSubKey(
                EventSourceRegistryPath,
                false))
            {
                return sourceKey == null ? null : EventLogName;
            }
        }

        private static void WriteEventLogEntry(
            string message,
            EventLogEntryType entryType,
            int eventId,
            short category)
        {
            using (SafeEventSourceHandle sourceHandle =
                RegisterEventSource(null, EventSourceName))
            {
                if (sourceHandle == null || sourceHandle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                string[] insertionStrings = { message };
                if (!ReportEvent(
                    sourceHandle,
                    checked((ushort)entryType),
                    checked((ushort)category),
                    checked((uint)eventId),
                    IntPtr.Zero,
                    1,
                    0,
                    insertionStrings,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        private static void ValidateActorSid(
            SecurityAuditEventId eventId,
            SecurityAuditReason reason,
            SecurityIdentifier actorSid)
        {
            bool actorSidIsAllowed =
                eventId == SecurityAuditEventId.AdminAuthorizationRejected
                || eventId == SecurityAuditEventId.PipeAuthorizationRejected;
            if (!actorSidIsAllowed && actorSid != null)
            {
                throw new ArgumentException(
                    "An actor SID is not allowed for this security audit event.",
                    nameof(actorSid));
            }

            bool actorSidIsRequired =
                (eventId == SecurityAuditEventId.AdminAuthorizationRejected
                    && reason == SecurityAuditReason.NotInOperatorsGroup)
                || (eventId == SecurityAuditEventId.PipeAuthorizationRejected
                    && reason == SecurityAuditReason.ClientNotAuthorized);
            if (actorSidIsRequired && actorSid == null)
            {
                throw new ArgumentNullException(
                    nameof(actorSid),
                    "An actor SID is required for this security audit event.");
            }
        }

        private static string FormatRemoteAddress(
            SecurityAuditBoundary boundary,
            IPAddress remoteAddress)
        {
            if (boundary == SecurityAuditBoundary.NamedPipe)
            {
                if (remoteAddress != null)
                {
                    throw new ArgumentException(
                        "Named Pipe audit events must not contain a network address.",
                        nameof(remoteAddress));
                }

                return UnknownValue;
            }

            if (remoteAddress == null)
            {
                return UnknownValue;
            }

            byte[] addressBytes = remoteAddress.GetAddressBytes();
            if (remoteAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                return new IPAddress(addressBytes).ToString();
            }

            if (remoteAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return new IPAddress(addressBytes)
                    .ToString()
                    .ToLowerInvariant();
            }

            throw new ArgumentException(
                "Only IPv4 and IPv6 remote addresses are supported.",
                nameof(remoteAddress));
        }

        private static void ValidateMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                throw new InvalidOperationException(
                    "A security audit message must not be empty.");
            }

            for (int index = 0; index < message.Length; index++)
            {
                char current = message[index];
                if (current < 0x20 || current > 0x7e)
                {
                    throw new InvalidOperationException(
                        "A security audit message must contain one line of printable ASCII.");
                }

                if (current == '%'
                    && index + 1 < message.Length
                    && message[index + 1] >= '0'
                    && message[index + 1] <= '9')
                {
                    throw new InvalidOperationException(
                        "A security audit message must not contain Event Log insertion markers.");
                }
            }

            if (Encoding.ASCII.GetByteCount(message) > MaximumMessageBytes)
            {
                throw new InvalidOperationException(
                    "A security audit message must not exceed 2 KiB.");
            }
        }

        private static bool IsEventLogInfrastructureFailure(
            Exception exception)
        {
            return exception is ArgumentException
                || exception is InvalidOperationException
                || exception is Win32Exception
                || exception is IOException
                || exception is UnauthorizedAccessException
                || exception is SecurityException;
        }

        [DllImport(
            "advapi32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeEventSourceHandle RegisterEventSource(
            string serverName,
            string sourceName);

        [DllImport(
            "advapi32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReportEvent(
            SafeEventSourceHandle eventLog,
            ushort eventType,
            ushort category,
            uint eventId,
            IntPtr userSid,
            ushort stringCount,
            uint dataSize,
            [MarshalAs(
                UnmanagedType.LPArray,
                ArraySubType = UnmanagedType.LPWStr,
                SizeParamIndex = 5)]
            string[] insertionStrings,
            IntPtr rawData);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeregisterEventSource(IntPtr eventLog);

        private sealed class SafeEventSourceHandle
            : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeEventSourceHandle()
                : base(true)
            {
            }

            protected override bool ReleaseHandle()
            {
                return DeregisterEventSource(handle);
            }
        }
    }
}
