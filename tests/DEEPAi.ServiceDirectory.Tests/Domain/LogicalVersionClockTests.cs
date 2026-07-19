using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Domain.Time;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class LogicalVersionClockTests
    {
        [TestMethod]
        public void NextIncrementsCurrentValue()
        {
            Assert.AreEqual(1UL, LogicalVersionClock.Next(0UL));
            Assert.AreEqual(42UL, LogicalVersionClock.Next(41UL));
        }

        [TestMethod]
        public void ObserveNeverMovesClockBackward()
        {
            Assert.AreEqual(11UL, LogicalVersionClock.Observe(7UL, 11UL));
            Assert.AreEqual(11UL, LogicalVersionClock.Observe(11UL, 7UL));
        }

        [TestMethod]
        public void NextFailsInsteadOfWrappingAtMaximumValue()
        {
            Assert.ThrowsExactly<LogicalClockExhaustedException>(
                () => LogicalVersionClock.Next(ulong.MaxValue));
        }
    }
}
