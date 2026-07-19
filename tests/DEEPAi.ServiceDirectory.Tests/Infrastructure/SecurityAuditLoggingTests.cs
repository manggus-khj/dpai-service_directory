using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class SecurityAuditLoggingTests
    {
        [TestMethod]
        public void ExternalAuditWriterUsesRequestedFixedOperationAndEventIds()
        {
            var capturedEvents = new List<CapturedEvent>();
            SecurityAuditEventLogger logger = CreateLogger(
                new Guid("45454545-4545-4545-4545-454545454545"),
                () => 0L,
                capturedEvents);
            var writer = new ExternalSecurityAuditWriter(logger);
            var requestId = new Guid(
                "56565656-5656-5656-5656-565656565656");

            writer.WriteApiKeyRejected(
                requestId,
                SecurityAuditOperation.ExternalUnknown,
                IPAddress.Parse("192.0.2.90"));
            writer.WriteNetworkBoundaryRejected(
                requestId,
                SecurityAuditOperation.ExternalHealth,
                ExternalNetworkBoundaryFailure.LocalEndpointUnavailable,
                null);

            Assert.AreEqual(2, capturedEvents.Count);
            Assert.AreEqual(4101, capturedEvents[0].EventId);
            StringAssert.Contains(
                capturedEvents[0].Message,
                " Operation=EXTERNAL_UNKNOWN ");
            Assert.AreEqual(4106, capturedEvents[1].EventId);
            StringAssert.Contains(
                capturedEvents[1].Message,
                " Operation=EXTERNAL_HEALTH ");
            StringAssert.Contains(
                capturedEvents[1].Message,
                " Reason=LOCAL_ENDPOINT_UNAVAILABLE ");
        }

        [TestMethod]
        public void WatchdogHealthAuditWriterUsesFixedBoundaryAndOperation()
        {
            var capturedEvents = new List<CapturedEvent>();
            SecurityAuditEventLogger logger = CreateLogger(
                new Guid("67676767-6767-6767-6767-676767676767"),
                () => 0L,
                capturedEvents);
            var writer = new WatchdogHealthSecurityAuditWriter(logger);
            var requestId = new Guid(
                "78787878-7878-7878-7878-787878787878");

            writer.WriteApiKeyRejected(
                requestId,
                IPAddress.Loopback);
            writer.WriteNetworkBoundaryRejected(
                requestId,
                WatchdogHealthNetworkBoundaryFailure
                    .RemoteEndpointNotLoopback,
                IPAddress.Parse("192.0.2.91"));

            Assert.AreEqual(2, capturedEvents.Count);
            Assert.AreEqual(4101, capturedEvents[0].EventId);
            StringAssert.Contains(
                capturedEvents[0].Message,
                " Boundary=WATCHDOG_HEALTH ");
            StringAssert.Contains(
                capturedEvents[0].Message,
                " Operation=WATCHDOG_HEALTH ");
            StringAssert.Contains(
                capturedEvents[0].Message,
                " Reason=INVALID_API_KEY ");
            Assert.AreEqual(4106, capturedEvents[1].EventId);
            StringAssert.Contains(
                capturedEvents[1].Message,
                " Boundary=WATCHDOG_HEALTH ");
            StringAssert.Contains(
                capturedEvents[1].Message,
                " Operation=WATCHDOG_HEALTH ");
            StringAssert.Contains(
                capturedEvents[1].Message,
                " Reason=REMOTE_ENDPOINT_NOT_LOOPBACK ");
        }

        [TestMethod]
        public void AuthenticationFailureWritesCanonicalSingleLineWithoutRequestSecrets()
        {
            long monotonicTimestamp = 0;
            var capturedEvents = new List<CapturedEvent>();
            var serviceInstanceId = new Guid(
                "55555555-5555-5555-5555-555555555555");
            var requestId = new Guid(
                "66666666-6666-6666-6666-666666666666");
            var logger = CreateLogger(
                serviceInstanceId,
                () => monotonicTimestamp,
                capturedEvents);

            bool written = logger.WriteFailure(
                SecurityAuditEventId.ExternalApiKeyRejected,
                SecurityAuditBoundary.External,
                SecurityAuditOperation.ExternalRegistration,
                SecurityAuditReason.InvalidApiKey,
                requestId,
                null,
                IPAddress.Parse("2001:0DB8:0:0:0:0:0:1"));

            Assert.IsTrue(written);
            Assert.AreEqual(1, capturedEvents.Count);
            CapturedEvent captured = capturedEvents[0];
            Assert.AreEqual(EventLogEntryType.FailureAudit, captured.EntryType);
            Assert.AreEqual(4101, captured.EventId);
            Assert.AreEqual((short)0, captured.Category);
            Assert.AreEqual(
                "Schema=1 Event=EXTERNAL_API_KEY_REJECTED"
                    + " Boundary=EXTERNAL"
                    + " Operation=EXTERNAL_REGISTRATION"
                    + " Reason=INVALID_API_KEY"
                    + " Outcome=REJECTED"
                    + " ServiceInstanceId="
                    + serviceInstanceId.ToString("D")
                    + " RequestId="
                    + requestId.ToString("D")
                    + " ActorSid=UNKNOWN"
                    + " RemoteAddress=2001:db8::1"
                    + " SuppressedCount=0",
                captured.Message);
            Assert.IsTrue(
                Encoding.ASCII.GetByteCount(captured.Message)
                    <= SecurityAuditEventLogger.MaximumMessageBytes);
            Assert.IsFalse(captured.Message.Contains("\0"));
            Assert.IsFalse(captured.Message.Contains("\r"));
            Assert.IsFalse(captured.Message.Contains("\n"));
            Assert.IsFalse(captured.Message.Contains("ABCD"));
            Assert.IsFalse(
                captured.Message.Contains(
                    "AAECAwQFBgcICQoLDA0OD37MVf5dYeif4Ss6OjGAC+g="));

            foreach (char value in captured.Message)
            {
                Assert.IsTrue(value >= 0x20 && value <= 0x7e);
            }
        }

        [TestMethod]
        public void FloodLimiterWritesFirstFiveThenReportsSuppressedCountAfterOneMinute()
        {
            long monotonicTimestamp = 0;
            var capturedEvents = new List<CapturedEvent>();
            var logger = CreateLogger(
                new Guid("77777777-7777-7777-7777-777777777777"),
                () => monotonicTimestamp,
                capturedEvents);
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.10");

            for (int index = 0;
                index < SecurityAuditRateLimiter.PerKeyBurstCapacity;
                index++)
            {
                Assert.IsTrue(WriteExternalLookupFailure(
                    logger,
                    Guid.NewGuid(),
                    remoteAddress));
            }

            Assert.IsFalse(WriteExternalLookupFailure(
                logger,
                Guid.NewGuid(),
                remoteAddress));
            Assert.AreEqual(5, capturedEvents.Count);

            monotonicTimestamp = 60;
            Assert.IsTrue(WriteExternalLookupFailure(
                logger,
                Guid.NewGuid(),
                remoteAddress));

            Assert.AreEqual(6, capturedEvents.Count);
            StringAssert.EndsWith(
                capturedEvents[5].Message,
                " RemoteAddress=192.0.2.10 SuppressedCount=1");
        }

        private static SecurityAuditEventLogger CreateLogger(
            Guid serviceInstanceId,
            Func<long> timestampProvider,
            IList<CapturedEvent> capturedEvents)
        {
            var rateLimiter = new SecurityAuditRateLimiter(
                timestampProvider,
                1);
            return new SecurityAuditEventLogger(
                serviceInstanceId,
                rateLimiter,
                () => SecurityAuditEventLogger.EventLogName,
                (message, entryType, eventId, category) =>
                    capturedEvents.Add(
                        new CapturedEvent(
                            message,
                            entryType,
                            eventId,
                            category)));
        }

        private static bool WriteExternalLookupFailure(
            SecurityAuditEventLogger logger,
            Guid requestId,
            IPAddress remoteAddress)
        {
            return logger.WriteFailure(
                SecurityAuditEventId.ExternalApiKeyRejected,
                SecurityAuditBoundary.External,
                SecurityAuditOperation.ExternalServiceLookup,
                SecurityAuditReason.InvalidApiKey,
                requestId,
                null,
                remoteAddress);
        }

        private sealed class CapturedEvent
        {
            public CapturedEvent(
                string message,
                EventLogEntryType entryType,
                int eventId,
                short category)
            {
                Message = message;
                EntryType = entryType;
                EventId = eventId;
                Category = category;
            }

            public string Message { get; }

            public EventLogEntryType EntryType { get; }

            public int EventId { get; }

            public short Category { get; }
        }
    }
}
