using System;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class WatchdogPipeCodecTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void RequestsRoundTripAllFourCommandsWithCanonicalCrlf()
        {
            AssertRequestRoundTrip(WatchdogPipeCommand.Start, "START\r\n");
            AssertRequestRoundTrip(WatchdogPipeCommand.Stop, "STOP\r\n");
            AssertRequestRoundTrip(WatchdogPipeCommand.Restart, "RESTART\r\n");
            AssertRequestRoundTrip(WatchdogPipeCommand.Status, "STATUS\r\n");
        }

        [TestMethod]
        public void RequestParserAcceptsLfButRejectsInvalidFramingAndArguments()
        {
            Assert.AreEqual(
                WatchdogPipeCommand.Status,
                ParseRequest("STATUS\n").Command.Value);
            AssertRequestFailure(
                Bytes("STATUS"),
                WatchdogPipeParseFailureCode.MissingLineTerminator);
            AssertRequestFailure(
                Bytes("STATUS\rSTATUS\n"),
                WatchdogPipeParseFailureCode.InvalidLineFraming);
            AssertRequestFailure(
                Bytes("STATUS\nSTOP\n"),
                WatchdogPipeParseFailureCode.InvalidLineFraming);
            AssertRequestFailure(
                Bytes("STATUS NOW\r\n"),
                WatchdogPipeParseFailureCode.ArgumentsNotAllowed);
            AssertRequestFailure(
                Bytes("status\r\n"),
                WatchdogPipeParseFailureCode.UnknownCommand);
        }

        [TestMethod]
        public void RequestParserRejectsEmptyBomNulInvalidUtf8AndOversize()
        {
            AssertRequestFailure(
                null,
                WatchdogPipeParseFailureCode.EmptyInput);
            AssertRequestFailure(
                new byte[0],
                WatchdogPipeParseFailureCode.EmptyInput);
            AssertRequestFailure(
                Bytes("\r\n"),
                WatchdogPipeParseFailureCode.EmptyLine);
            AssertRequestFailure(
                new byte[] { 0xEF, 0xBB, 0xBF, (byte)'S', (byte)'\n' },
                WatchdogPipeParseFailureCode.Utf8BomNotAllowed);
            AssertRequestFailure(
                new byte[] { (byte)'S', 0, (byte)'\n' },
                WatchdogPipeParseFailureCode.NulNotAllowed);
            AssertRequestFailure(
                new byte[] { 0xC3, 0x28, (byte)'\n' },
                WatchdogPipeParseFailureCode.InvalidUtf8);

            var oversized = new byte[WatchdogPipeCodec.MaximumLineBytes + 1];
            for (int index = 0; index < oversized.Length - 1; index++)
            {
                oversized[index] = (byte)'A';
            }

            oversized[oversized.Length - 1] = (byte)'\n';
            AssertRequestFailure(
                oversized,
                WatchdogPipeParseFailureCode.MaximumLineBytesExceeded);
        }

        [TestMethod]
        public void ControlSuccessResponsesUseOnlyTheFixedOkLine()
        {
            foreach (WatchdogPipeCommand command in new[]
            {
                WatchdogPipeCommand.Start,
                WatchdogPipeCommand.Stop,
                WatchdogPipeCommand.Restart
            })
            {
                byte[] encoded = WatchdogPipeCodec
                    .EncodeControlSuccessResponse(command);
                CollectionAssert.AreEqual(Bytes("OK\r\n"), encoded);

                WatchdogResponseParseResult parsed =
                    WatchdogPipeCodec.ParseResponse(encoded, command);
                Assert.IsTrue(parsed.IsValid);
                Assert.AreEqual(
                    WatchdogPipeResponseOutcome.Success,
                    parsed.Outcome.Value);
                Assert.IsNull(parsed.StatusSnapshot);
                Assert.IsNull(parsed.ErrorReason);
            }

            Assert.ThrowsExactly<ArgumentException>(
                () => WatchdogPipeCodec.EncodeControlSuccessResponse(
                    WatchdogPipeCommand.Status));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => WatchdogPipeCodec.EncodeControlSuccessResponse(
                    (WatchdogPipeCommand)99));
        }

        [TestMethod]
        public void StatusResponseRoundTripsNotRunAndOmitsLastHealthUtc()
        {
            var source = new WatchdogStatusSnapshot(
                WatchdogServiceStatus.Stopped,
                WatchdogHealthStatus.NotRun,
                0,
                0,
                WatchdogAutoRestartStatus.Enabled,
                null);

            byte[] encoded = WatchdogPipeCodec.EncodeStatusSuccessResponse(
                WatchdogPipeCommand.Status,
                source);
            Assert.AreEqual(
                "OK: STOPPED;HEALTH=NOT_RUN;FAILURES=0;"
                    + "RESTARTS_10M=0;AUTO_RESTART=ENABLED\r\n",
                StrictUtf8.GetString(encoded));

            WatchdogStatusSnapshot parsed = ParseStatus(encoded);
            Assert.AreEqual(WatchdogServiceStatus.Stopped, parsed.ServiceStatus);
            Assert.AreEqual(WatchdogHealthStatus.NotRun, parsed.HealthStatus);
            Assert.AreEqual(0, parsed.ConsecutiveFailures);
            Assert.AreEqual(0, parsed.RestartCountInTenMinutes);
            Assert.AreEqual(
                WatchdogAutoRestartStatus.Enabled,
                parsed.AutoRestartStatus);
            Assert.IsNull(parsed.LastHealthUtc);
        }

        [TestMethod]
        public void StatusResponseRoundTripsCompletedHealthAndUtcMilliseconds()
        {
            var lastHealthUtc = new DateTimeOffset(
                2026,
                7,
                18,
                10,
                11,
                12,
                345,
                TimeSpan.Zero);
            var source = new WatchdogStatusSnapshot(
                WatchdogServiceStatus.Running,
                WatchdogHealthStatus.Failed,
                2,
                3,
                WatchdogAutoRestartStatus.Suppressed,
                lastHealthUtc);

            byte[] encoded = WatchdogPipeCodec.EncodeStatusSuccessResponse(
                WatchdogPipeCommand.Status,
                source);
            StringAssert.Contains(
                StrictUtf8.GetString(encoded),
                "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z");

            WatchdogStatusSnapshot parsed = ParseStatus(encoded);
            Assert.AreEqual(WatchdogServiceStatus.Running, parsed.ServiceStatus);
            Assert.AreEqual(WatchdogHealthStatus.Failed, parsed.HealthStatus);
            Assert.AreEqual(2, parsed.ConsecutiveFailures);
            Assert.AreEqual(3, parsed.RestartCountInTenMinutes);
            Assert.AreEqual(
                WatchdogAutoRestartStatus.Suppressed,
                parsed.AutoRestartStatus);
            Assert.AreEqual(lastHealthUtc, parsed.LastHealthUtc.Value);
        }

        [TestMethod]
        public void StatusParserEnforcesKnownFieldOrderAndCardinality()
        {
            AssertStatusFailure(
                "OK: RUNNING;FAILURES=0;HEALTH=OK;RESTARTS_10M=0;"
                    + "AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusFieldOrder);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=OK;HEALTH=OK;FAILURES=0;"
                    + "RESTARTS_10M=0;AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.DuplicateStatusField);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=OK;FAILURES=0;"
                    + "AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusFieldOrder);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=OK;FAILURES=0;RESTARTS_10M=0;"
                    + "AUTO_RESTART=ENABLED;FUTURE=x;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusFieldOrder);
        }

        [TestMethod]
        public void StatusParserRejectsInvalidCountersTimestampsAndCombinations()
        {
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=FAILED;FAILURES=00;"
                    + "RESTARTS_10M=0;AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusCounter);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=FAILED;FAILURES=1;"
                    + "RESTARTS_10M=4;AUTO_RESTART=SUPPRESSED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusCounter);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=OK;FAILURES=0;"
                    + "RESTARTS_10M=0;AUTO_RESTART=ENABLED\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusTimestamp);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=NOT_RUN;FAILURES=0;"
                    + "RESTARTS_10M=0;AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusTimestamp);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=FAILED;FAILURES=0;"
                    + "RESTARTS_10M=0;AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusCombination);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=OK;FAILURES=0;"
                    + "RESTARTS_10M=3;AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusCombination);
        }

        [TestMethod]
        public void StatusParserAllowsBoundedFutureFieldsOnlyAtTheEnd()
        {
            byte[] response = Bytes(
                "OK: RUNNING;HEALTH=OK;FAILURES=0;RESTARTS_10M=0;"
                    + "AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z;"
                    + "FUTURE_STATE=READY\r\n");

            WatchdogStatusSnapshot parsed = ParseStatus(response);
            Assert.AreEqual(WatchdogHealthStatus.Ok, parsed.HealthStatus);

            AssertStatusFailure(
                "OK: RUNNING;HEALTH=OK;FAILURES=0;RESTARTS_10M=0;"
                    + "AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z;"
                    + "bad=READY\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusExtension);
            AssertStatusFailure(
                "OK: RUNNING;HEALTH=OK;FAILURES=0;RESTARTS_10M=0;"
                    + "AUTO_RESTART=ENABLED;"
                    + "LAST_HEALTH_UTC=2026-07-18T10:11:12.345Z;"
                    + "FUTURE_STATE=NOT;READY\r\n",
                WatchdogPipeParseFailureCode.InvalidStatusExtension);
        }

        [TestMethod]
        public void ResponseShapeMustMatchTheExpectedCommand()
        {
            AssertResponseFailure(
                Bytes("OK\r\n"),
                WatchdogPipeCommand.Status,
                WatchdogPipeParseFailureCode.UnexpectedSuccessShape);
            AssertResponseFailure(
                Bytes(
                    "OK: STOPPED;HEALTH=NOT_RUN;FAILURES=0;"
                        + "RESTARTS_10M=0;AUTO_RESTART=ENABLED\r\n"),
                WatchdogPipeCommand.Start,
                WatchdogPipeParseFailureCode.UnexpectedSuccessShape);
            AssertResponseFailure(
                Bytes("UNKNOWN\r\n"),
                WatchdogPipeCommand.Start,
                WatchdogPipeParseFailureCode.UnexpectedResponse);
        }

        [TestMethod]
        public void ErrorResponseRoundTripsSafeSingleLineUnicodeReason()
        {
            const string reason = "서비스 상태를 확인할 수 없습니다.";
            byte[] encoded = WatchdogPipeCodec.EncodeErrorResponse(reason);
            WatchdogResponseParseResult parsed =
                WatchdogPipeCodec.ParseResponse(
                    encoded,
                    WatchdogPipeCommand.Status);

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(
                WatchdogPipeResponseOutcome.Error,
                parsed.Outcome.Value);
            Assert.AreEqual(reason, parsed.ErrorReason);
            Assert.IsNull(parsed.StatusSnapshot);
            Assert.IsTrue(encoded.Length <= WatchdogPipeCodec.MaximumLineBytes);

            Assert.ThrowsExactly<ArgumentException>(
                () => WatchdogPipeCodec.EncodeErrorResponse("line1\nline2"));
            Assert.ThrowsExactly<ArgumentException>(
                () => WatchdogPipeCodec.EncodeErrorResponse(" "));
            Assert.ThrowsExactly<ArgumentException>(
                () => WatchdogPipeCodec.EncodeErrorResponse("\uD800"));
        }

        [TestMethod]
        public void ModelsDefensivelyRejectInvalidStatusShapes()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new WatchdogStatusSnapshot(
                    (WatchdogServiceStatus)0,
                    WatchdogHealthStatus.NotRun,
                    0,
                    0,
                    WatchdogAutoRestartStatus.Enabled,
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => new WatchdogStatusSnapshot(
                    WatchdogServiceStatus.Running,
                    WatchdogHealthStatus.Ok,
                    1,
                    0,
                    WatchdogAutoRestartStatus.Enabled,
                    Utc(0)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new WatchdogStatusSnapshot(
                    WatchdogServiceStatus.Running,
                    WatchdogHealthStatus.Failed,
                    1,
                    0,
                    WatchdogAutoRestartStatus.Enabled,
                    Utc(0).AddTicks(1)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new WatchdogStatusSnapshot(
                    WatchdogServiceStatus.Running,
                    WatchdogHealthStatus.NotRun,
                    1,
                    0,
                    WatchdogAutoRestartStatus.Enabled,
                    null));
        }

        private static void AssertRequestRoundTrip(
            WatchdogPipeCommand command,
            string expected)
        {
            byte[] encoded = WatchdogPipeCodec.EncodeRequest(command);
            CollectionAssert.AreEqual(Bytes(expected), encoded);
            WatchdogRequestParseResult parsed =
                WatchdogPipeCodec.ParseRequest(encoded);
            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(command, parsed.Command.Value);
            Assert.AreEqual(
                WatchdogPipeParseFailureCode.None,
                parsed.FailureCode);
        }

        private static WatchdogRequestParseResult ParseRequest(string value)
        {
            WatchdogRequestParseResult result =
                WatchdogPipeCodec.ParseRequest(Bytes(value));
            Assert.IsTrue(result.IsSuccess);
            return result;
        }

        private static void AssertRequestFailure(
            byte[] value,
            WatchdogPipeParseFailureCode expected)
        {
            WatchdogRequestParseResult result =
                WatchdogPipeCodec.ParseRequest(value);
            Assert.IsFalse(result.IsSuccess);
            Assert.IsNull(result.Command);
            Assert.AreEqual(expected, result.FailureCode);
        }

        private static WatchdogStatusSnapshot ParseStatus(byte[] value)
        {
            WatchdogResponseParseResult result =
                WatchdogPipeCodec.ParseResponse(
                    value,
                    WatchdogPipeCommand.Status);
            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(
                WatchdogPipeResponseOutcome.Success,
                result.Outcome.Value);
            Assert.IsNotNull(result.StatusSnapshot);
            return result.StatusSnapshot;
        }

        private static void AssertStatusFailure(
            string value,
            WatchdogPipeParseFailureCode expected)
        {
            AssertResponseFailure(
                Bytes(value),
                WatchdogPipeCommand.Status,
                expected);
        }

        private static void AssertResponseFailure(
            byte[] value,
            WatchdogPipeCommand command,
            WatchdogPipeParseFailureCode expected)
        {
            WatchdogResponseParseResult result =
                WatchdogPipeCodec.ParseResponse(value, command);
            Assert.IsFalse(result.IsValid);
            Assert.IsNull(result.Outcome);
            Assert.IsNull(result.StatusSnapshot);
            Assert.IsNull(result.ErrorReason);
            Assert.AreEqual(expected, result.FailureCode);
        }

        private static DateTimeOffset Utc(int minute)
        {
            return new DateTimeOffset(
                2026,
                7,
                18,
                0,
                minute,
                0,
                TimeSpan.Zero);
        }

        private static byte[] Bytes(string value)
        {
            return StrictUtf8.GetBytes(value);
        }
    }
}
