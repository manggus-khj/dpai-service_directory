using System;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class SystemFileLoggerTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void WritesExactlyNineCanonicalEventsWithLocalOffsetTimestamp()
        {
            string dataRoot = CreateDataRoot();
            try
            {
                var localNow = new DateTimeOffset(
                    2026,
                    7,
                    18,
                    14,
                    5,
                    6,
                    789,
                    TimeSpan.FromHours(9));
                var logger = new SystemFileLogger(dataRoot, () => localNow);
                var instanceId = new Guid(
                    "11111111-1111-1111-1111-111111111111");
                var peerInstanceId = new Guid(
                    "22222222-2222-2222-2222-222222222222");
                ProductCode productCode = CreateProductCode("AB12");

                logger.WriteServiceStarted(instanceId, 30);
                logger.WriteServiceStopped("NORMAL_SHUTDOWN", 30);
                logger.WriteRegisteredServiceCreated(productCode, 30);
                logger.WriteRegisteredServiceUpdated(productCode, 30);
                logger.WriteRegisteredServiceDeleted(productCode, 30);
                logger.WriteSyncInitialStarted(
                    peerInstanceId,
                    "PAIRING_ENABLED",
                    30);
                logger.WriteSyncStarted(peerInstanceId, "PERIODIC", 30);
                logger.WriteSyncStopped(peerInstanceId, "ADMIN_REQUEST", 30);
                logger.WriteSyncSucceeded(peerInstanceId, "PERIODIC", 30);

                string logPath = Path.Combine(
                    GetLogDirectory(dataRoot),
                    "dpai-sd_2026-07-18.log");
                string timestamp = "2026-07-18T14:05:06.789+09:00";
                string[] expectedLines =
                {
                    timestamp
                        + " [SERVICE_STARTED] InstanceId="
                        + instanceId.ToString("D"),
                    timestamp
                        + " [SERVICE_STOPPED] Reason=NORMAL_SHUTDOWN",
                    timestamp
                        + " [REGISTERED_SERVICE_CREATED] ProductCode=AB12",
                    timestamp
                        + " [REGISTERED_SERVICE_UPDATED] ProductCode=AB12",
                    timestamp
                        + " [REGISTERED_SERVICE_DELETED] ProductCode=AB12",
                    timestamp
                        + " [SYNC_INITIAL_STARTED] PeerInstanceId="
                        + peerInstanceId.ToString("D")
                        + " Trigger=PAIRING_ENABLED",
                    timestamp
                        + " [SYNC_STARTED] PeerInstanceId="
                        + peerInstanceId.ToString("D")
                        + " Trigger=PERIODIC",
                    timestamp
                        + " [SYNC_STOPPED] PeerInstanceId="
                        + peerInstanceId.ToString("D")
                        + " Reason=ADMIN_REQUEST",
                    timestamp
                        + " [SYNC_SUCCEEDED] PeerInstanceId="
                        + peerInstanceId.ToString("D")
                        + " Trigger=PERIODIC"
                };

                CollectionAssert.AreEqual(
                    expectedLines,
                    File.ReadAllLines(logPath, StrictUtf8));
                Assert.AreEqual(
                    expectedLines.Length,
                    Enum.GetValues(typeof(SystemLogEventCode)).Length);
            }
            finally
            {
                DeleteDataRoot(dataRoot);
            }
        }

        [TestMethod]
        public void RollsFileAtInjectedLocalMidnightAndUsesCurrentOffset()
        {
            string dataRoot = CreateDataRoot();
            try
            {
                var localNow = new DateTimeOffset(
                    2026,
                    7,
                    18,
                    23,
                    59,
                    59,
                    998,
                    TimeSpan.FromHours(9));
                var logger = new SystemFileLogger(dataRoot, () => localNow);
                var instanceId = new Guid(
                    "33333333-3333-3333-3333-333333333333");

                logger.WriteServiceStarted(instanceId, 30);
                localNow = new DateTimeOffset(
                    2026,
                    7,
                    19,
                    0,
                    0,
                    0,
                    4,
                    TimeSpan.FromHours(8.5));
                logger.WriteServiceStopped("TIMEZONE_CHANGED", 30);

                string logDirectory = GetLogDirectory(dataRoot);
                string[] firstDayLines = File.ReadAllLines(
                    Path.Combine(logDirectory, "dpai-sd_2026-07-18.log"),
                    StrictUtf8);
                string[] secondDayLines = File.ReadAllLines(
                    Path.Combine(logDirectory, "dpai-sd_2026-07-19.log"),
                    StrictUtf8);

                Assert.AreEqual(1, firstDayLines.Length);
                StringAssert.StartsWith(
                    firstDayLines[0],
                    "2026-07-18T23:59:59.998+09:00 [SERVICE_STARTED]");
                Assert.AreEqual(1, secondDayLines.Length);
                Assert.AreEqual(
                    "2026-07-19T00:00:00.004+08:30 "
                        + "[SERVICE_STOPPED] Reason=TIMEZONE_CHANGED",
                    secondDayLines[0]);
            }
            finally
            {
                DeleteDataRoot(dataRoot);
            }
        }

        [TestMethod]
        public void RetentionUsesInclusiveLocalCalendarBoundaryAndOnlyCanonicalTopLevelFiles()
        {
            string dataRoot = CreateDataRoot();
            try
            {
                string logDirectory = GetLogDirectory(dataRoot);
                string expiredCanonical = CreateLogFile(
                    logDirectory,
                    "dpai-sd_2026-07-15.log");
                string retainedBoundary = CreateLogFile(
                    logDirectory,
                    "dpai-sd_2026-07-16.log");
                string retainedYesterday = CreateLogFile(
                    logDirectory,
                    "dpai-sd_2026-07-17.log");
                string nonCanonical = CreateLogFile(
                    logDirectory,
                    "dpai-sd_2026-7-15.log");
                string impossibleDate = CreateLogFile(
                    logDirectory,
                    "dpai-sd_2026-02-30.log");
                string backup = CreateLogFile(
                    logDirectory,
                    "dpai-sd_2026-07-15.log.bak");
                string nestedDirectory = Path.Combine(
                    logDirectory,
                    "archive");
                Directory.CreateDirectory(nestedDirectory);
                string nestedCanonical = CreateLogFile(
                    nestedDirectory,
                    "dpai-sd_2026-07-15.log");
                var localNow = new DateTimeOffset(
                    2026,
                    7,
                    18,
                    12,
                    0,
                    0,
                    TimeSpan.FromHours(9));
                var logger = new SystemFileLogger(dataRoot, () => localNow);

                logger.ApplyRetention(3);

                Assert.IsFalse(File.Exists(expiredCanonical));
                Assert.IsTrue(File.Exists(retainedBoundary));
                Assert.IsTrue(File.Exists(retainedYesterday));
                Assert.IsTrue(File.Exists(nonCanonical));
                Assert.IsTrue(File.Exists(impossibleDate));
                Assert.IsTrue(File.Exists(backup));
                Assert.IsTrue(File.Exists(nestedCanonical));
            }
            finally
            {
                DeleteDataRoot(dataRoot);
            }
        }

        [TestMethod]
        public void RetentionAcceptsOnlyOneThroughOneThousandNinetyFiveDays()
        {
            string dataRoot = CreateDataRoot();
            try
            {
                var logger = new SystemFileLogger(
                    dataRoot,
                    () => new DateTimeOffset(
                        2026,
                        7,
                        18,
                        12,
                        0,
                        0,
                        TimeSpan.FromHours(9)));

                logger.ApplyRetention(SystemFileLogger.MinimumRetentionDays);
                logger.ApplyRetention(SystemFileLogger.MaximumRetentionDays);

                CollectionAssert.AreEqual(
                    new[] { 1, 30, 1095 },
                    new[]
                    {
                        SystemFileLogger.MinimumRetentionDays,
                        SystemFileLogger.DefaultRetentionDays,
                        SystemFileLogger.MaximumRetentionDays
                    });
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => logger.ApplyRetention(0));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => logger.ApplyRetention(1096));
            }
            finally
            {
                DeleteDataRoot(dataRoot);
            }
        }

        [TestMethod]
        public void AppliedRetentionOverridesStaleEventProducerValue()
        {
            string dataRoot = CreateDataRoot();
            try
            {
                string logDirectory = GetLogDirectory(dataRoot);
                string longTermLog = CreateLogFile(
                    logDirectory,
                    "dpai-sd_2026-01-01.log");
                var localNow = new DateTimeOffset(
                    2026,
                    7,
                    18,
                    12,
                    0,
                    0,
                    TimeSpan.FromHours(9));
                var logger = new SystemFileLogger(
                    dataRoot,
                    () => localNow);

                logger.ApplyRetention(1095);
                localNow = localNow.AddDays(1);
                logger.WriteSyncSucceeded(
                    new Guid(
                        "55555555-5555-5555-5555-555555555555"),
                    "PERIODIC",
                    30);

                Assert.IsTrue(File.Exists(longTermLog));
            }
            finally
            {
                DeleteDataRoot(dataRoot);
            }
        }

        [TestMethod]
        public void RejectsLineBreaksAndNonCanonicalDetailTokensBeforeWriting()
        {
            string dataRoot = CreateDataRoot();
            try
            {
                var logger = new SystemFileLogger(
                    dataRoot,
                    () => new DateTimeOffset(
                        2026,
                        7,
                        18,
                        12,
                        0,
                        0,
                        TimeSpan.FromHours(9)));
                var peerInstanceId = new Guid(
                    "44444444-4444-4444-4444-444444444444");

                Assert.ThrowsExactly<ArgumentException>(
                    () => logger.WriteServiceStopped(
                        "NORMAL\r\nFORGED_EVENT",
                        30));
                Assert.ThrowsExactly<ArgumentException>(
                    () => logger.WriteSyncStarted(
                        peerInstanceId,
                        "manual",
                        30));
                Assert.ThrowsExactly<ArgumentException>(
                    () => logger.WriteSyncStopped(
                        peerInstanceId,
                        "ADMIN REQUEST",
                        30));

                Assert.AreEqual(
                    0,
                    Directory.GetFiles(
                        GetLogDirectory(dataRoot),
                        "*.log",
                        SearchOption.TopDirectoryOnly).Length);
            }
            finally
            {
                DeleteDataRoot(dataRoot);
            }
        }

        private static ProductCode CreateProductCode(string rawValue)
        {
            ProductCode productCode;
            Assert.IsTrue(ProductCode.TryCreate(rawValue, out productCode));
            return productCode;
        }

        private static string CreateDataRoot()
        {
            string dataRoot = Path.Combine(
                Path.GetTempPath(),
                "dpai-sd-log-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(GetLogDirectory(dataRoot));
            return dataRoot;
        }

        private static string GetLogDirectory(string dataRoot)
        {
            return Path.Combine(dataRoot, "logs", "system");
        }

        private static string CreateLogFile(
            string directoryPath,
            string fileName)
        {
            string filePath = Path.Combine(directoryPath, fileName);
            File.WriteAllText(filePath, "test", StrictUtf8);
            return filePath;
        }

        private static void DeleteDataRoot(string dataRoot)
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }
}
