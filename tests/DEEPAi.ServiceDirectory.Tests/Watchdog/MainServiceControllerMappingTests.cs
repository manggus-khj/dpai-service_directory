using System;
using System.ServiceProcess;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;
using DEEPAi.ServiceDirectory.Watchdog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Watchdog
{
    [TestClass]
    public sealed class MainServiceControllerMappingTests
    {
        [TestMethod]
        public void EveryContractStatusMapsFromServiceControllerStatus()
        {
            Assert.AreEqual(
                WatchdogServiceStatus.Stopped,
                MainServiceController.MapStatus(
                    ServiceControllerStatus.Stopped));
            Assert.AreEqual(
                WatchdogServiceStatus.StartPending,
                MainServiceController.MapStatus(
                    ServiceControllerStatus.StartPending));
            Assert.AreEqual(
                WatchdogServiceStatus.StopPending,
                MainServiceController.MapStatus(
                    ServiceControllerStatus.StopPending));
            Assert.AreEqual(
                WatchdogServiceStatus.Running,
                MainServiceController.MapStatus(
                    ServiceControllerStatus.Running));
            Assert.AreEqual(
                WatchdogServiceStatus.ContinuePending,
                MainServiceController.MapStatus(
                    ServiceControllerStatus.ContinuePending));
            Assert.AreEqual(
                WatchdogServiceStatus.PausePending,
                MainServiceController.MapStatus(
                    ServiceControllerStatus.PausePending));
            Assert.AreEqual(
                WatchdogServiceStatus.Paused,
                MainServiceController.MapStatus(
                    ServiceControllerStatus.Paused));
        }

        [TestMethod]
        public void UndefinedStatusIsRejected()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => MainServiceController.MapStatus(
                    (ServiceControllerStatus)99));
        }
    }
}
