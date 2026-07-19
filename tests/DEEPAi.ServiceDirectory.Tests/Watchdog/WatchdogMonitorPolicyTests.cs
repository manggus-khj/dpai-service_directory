using System;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;
using DEEPAi.ServiceDirectory.Watchdog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Watchdog
{
    [TestClass]
    public sealed class WatchdogMonitorPolicyTests
    {
        private static readonly DateTimeOffset CompletedUtc =
            new DateTimeOffset(2026, 7, 18, 4, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void InitialStatusIsNotRunWithAutomaticRestartEnabled()
        {
            var policy = new WatchdogMonitorPolicy();

            WatchdogStatusSnapshot status = policy.CreateStatusSnapshot(
                WatchdogServiceStatus.Stopped,
                TimeSpan.Zero);

            Assert.AreEqual(WatchdogHealthStatus.NotRun, status.HealthStatus);
            Assert.AreEqual(0, status.ConsecutiveFailures);
            Assert.AreEqual(0, status.RestartCountInTenMinutes);
            Assert.AreEqual(
                WatchdogAutoRestartStatus.Enabled,
                status.AutoRestartStatus);
            Assert.IsNull(status.LastHealthUtc);
        }

        [TestMethod]
        public void ThreeConsecutiveCombinedFailuresTriggerOneRestartDecision()
        {
            var policy = new WatchdogMonitorPolicy();

            Assert.IsFalse(Fail(policy, 1).ShouldRestart);
            Assert.IsFalse(Fail(policy, 2).ShouldRestart);
            Assert.IsTrue(Fail(policy, 3).ShouldRestart);
            Assert.IsFalse(Fail(policy, 4).ShouldRestart);
            Assert.IsFalse(Fail(policy, 5).ShouldRestart);
            Assert.IsTrue(Fail(policy, 6).ShouldRestart);

            WatchdogStatusSnapshot status = policy.CreateStatusSnapshot(
                WatchdogServiceStatus.Running,
                TimeSpan.FromSeconds(6));
            Assert.AreEqual(6, status.ConsecutiveFailures);
            Assert.AreEqual(2, status.RestartCountInTenMinutes);
        }

        [TestMethod]
        public void SuccessResetsTheConsecutiveFailureSequence()
        {
            var policy = new WatchdogMonitorPolicy();
            Fail(policy, 1);
            Fail(policy, 2);

            WatchdogMonitorDecision success = policy.RecordObservation(
                true,
                true,
                CompletedUtc.AddSeconds(3),
                TimeSpan.FromSeconds(3));

            Assert.IsFalse(success.ShouldRestart);
            Assert.IsFalse(Fail(policy, 4).ShouldRestart);
            Assert.IsFalse(Fail(policy, 5).ShouldRestart);
            Assert.IsTrue(Fail(policy, 6).ShouldRestart);
        }

        [TestMethod]
        public void ThirdRestartAttemptSuppressesUntilOperatorAction()
        {
            var policy = new WatchdogMonitorPolicy();
            for (int second = 1; second <= 9; second++)
            {
                WatchdogMonitorDecision decision = Fail(policy, second);
                Assert.AreEqual(second % 3 == 0, decision.ShouldRestart);
            }

            WatchdogStatusSnapshot suppressed = policy.CreateStatusSnapshot(
                WatchdogServiceStatus.Running,
                TimeSpan.FromSeconds(9));
            Assert.AreEqual(3, suppressed.RestartCountInTenMinutes);
            Assert.AreEqual(
                WatchdogAutoRestartStatus.Suppressed,
                suppressed.AutoRestartStatus);

            for (int second = 10; second <= 20; second++)
            {
                Assert.IsFalse(Fail(policy, second).ShouldRestart);
            }

            policy.RecordManualStartOrRestart(TimeSpan.FromSeconds(21));
            WatchdogStatusSnapshot released = policy.CreateStatusSnapshot(
                WatchdogServiceStatus.Running,
                TimeSpan.FromSeconds(21));
            Assert.AreEqual(0, released.RestartCountInTenMinutes);
            Assert.AreEqual(
                WatchdogAutoRestartStatus.Enabled,
                released.AutoRestartStatus);
        }

        [TestMethod]
        public void SuppressionLatchRemainsAfterRollingWindowExpires()
        {
            var policy = new WatchdogMonitorPolicy();
            for (int second = 1; second <= 9; second++)
            {
                Fail(policy, second);
            }

            WatchdogStatusSnapshot status = policy.CreateStatusSnapshot(
                WatchdogServiceStatus.Running,
                TimeSpan.FromMinutes(11));

            Assert.AreEqual(0, status.RestartCountInTenMinutes);
            Assert.AreEqual(
                WatchdogAutoRestartStatus.Suppressed,
                status.AutoRestartStatus);
        }

        [TestMethod]
        public void ManualStopPreventsAutomaticRestartUntilManualStart()
        {
            var policy = new WatchdogMonitorPolicy();
            policy.RecordManualStop(TimeSpan.Zero);
            for (int second = 1; second <= 6; second++)
            {
                Assert.IsFalse(Fail(policy, second).ShouldRestart);
            }

            Assert.IsTrue(policy.IsManualStopRequested);
            policy.RecordManualStartOrRestart(TimeSpan.FromSeconds(7));
            Assert.IsFalse(policy.IsManualStopRequested);
            Assert.IsFalse(Fail(policy, 8).ShouldRestart);
            Assert.IsFalse(Fail(policy, 9).ShouldRestart);
            Assert.IsTrue(Fail(policy, 10).ShouldRestart);
        }

        [TestMethod]
        public void MonotonicTimeRegressionIsRejected()
        {
            var policy = new WatchdogMonitorPolicy();
            policy.CreateStatusSnapshot(
                WatchdogServiceStatus.Running,
                TimeSpan.FromSeconds(2));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => policy.CreateStatusSnapshot(
                    WatchdogServiceStatus.Running,
                    TimeSpan.FromSeconds(1)));
        }

        private static WatchdogMonitorDecision Fail(
            WatchdogMonitorPolicy policy,
            int second)
        {
            return policy.RecordObservation(
                true,
                false,
                CompletedUtc.AddSeconds(second),
                TimeSpan.FromSeconds(second));
        }
    }
}
