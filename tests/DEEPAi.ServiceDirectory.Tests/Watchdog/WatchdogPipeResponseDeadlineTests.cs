using System;
using DEEPAi.ServiceDirectory.Watchdog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Watchdog
{
    [TestClass]
    public sealed class WatchdogPipeResponseDeadlineTests
    {
        [TestMethod]
        public void RemainingUsesOneMonotonicThreeSecondBudget()
        {
            long timestamp = 100;
            var deadline = new WatchdogPipeResponseDeadline(
                TimeSpan.FromSeconds(3),
                () => timestamp,
                10);

            Assert.AreEqual(
                TimeSpan.FromSeconds(3),
                deadline.Remaining);
            timestamp = 115;
            Assert.AreEqual(
                TimeSpan.FromSeconds(1.5),
                deadline.Remaining);
            timestamp = 130;
            Assert.AreEqual(TimeSpan.Zero, deadline.Remaining);
            timestamp = 140;
            Assert.AreEqual(TimeSpan.Zero, deadline.Remaining);
        }

        [TestMethod]
        public void BackwardTimestampFailsClosedAsExpired()
        {
            long timestamp = 100;
            var deadline = new WatchdogPipeResponseDeadline(
                TimeSpan.FromSeconds(3),
                () => timestamp,
                10);
            timestamp = 110;
            Assert.AreEqual(
                TimeSpan.FromSeconds(2),
                deadline.Remaining);

            timestamp = 109;
            Assert.AreEqual(TimeSpan.Zero, deadline.Remaining);
            timestamp = 120;
            Assert.AreEqual(TimeSpan.Zero, deadline.Remaining);
        }

        [TestMethod]
        public void VeryLargeElapsedTimestampExpiresWithoutOverflow()
        {
            long timestamp = 0;
            var deadline = new WatchdogPipeResponseDeadline(
                TimeSpan.FromSeconds(3),
                () => timestamp,
                1);

            timestamp = long.MaxValue;
            Assert.AreEqual(TimeSpan.Zero, deadline.Remaining);
        }

        [TestMethod]
        public void InvalidConstructionIsRejected()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new WatchdogPipeResponseDeadline(
                    TimeSpan.Zero,
                    () => 0,
                    1));
            Assert.ThrowsExactly<ArgumentNullException>(
                () => new WatchdogPipeResponseDeadline(
                    TimeSpan.FromSeconds(3),
                    null,
                    1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new WatchdogPipeResponseDeadline(
                    TimeSpan.FromSeconds(3),
                    () => 0,
                    0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new WatchdogPipeResponseDeadline(
                    TimeSpan.FromSeconds(3),
                    () => -1,
                    1));
        }
    }
}
