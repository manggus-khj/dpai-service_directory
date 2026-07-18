using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Logging
{
    internal enum SystemLogEventCode
    {
        ServiceStarted = 1,
        ServiceStopped = 2,
        RegisteredServiceCreated = 3,
        RegisteredServiceUpdated = 4,
        RegisteredServiceDeleted = 5,
        SyncInitialStarted = 6,
        SyncStarted = 7,
        SyncStopped = 8,
        SyncSucceeded = 9
    }

    internal static class SystemLogEventCodeFormatter
    {
        public static string ToFileCode(SystemLogEventCode eventCode)
        {
            switch (eventCode)
            {
                case SystemLogEventCode.ServiceStarted:
                    return "SERVICE_STARTED";
                case SystemLogEventCode.ServiceStopped:
                    return "SERVICE_STOPPED";
                case SystemLogEventCode.RegisteredServiceCreated:
                    return "REGISTERED_SERVICE_CREATED";
                case SystemLogEventCode.RegisteredServiceUpdated:
                    return "REGISTERED_SERVICE_UPDATED";
                case SystemLogEventCode.RegisteredServiceDeleted:
                    return "REGISTERED_SERVICE_DELETED";
                case SystemLogEventCode.SyncInitialStarted:
                    return "SYNC_INITIAL_STARTED";
                case SystemLogEventCode.SyncStarted:
                    return "SYNC_STARTED";
                case SystemLogEventCode.SyncStopped:
                    return "SYNC_STOPPED";
                case SystemLogEventCode.SyncSucceeded:
                    return "SYNC_SUCCEEDED";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(eventCode),
                        eventCode,
                        "A defined system log event code is required.");
            }
        }
    }
}
