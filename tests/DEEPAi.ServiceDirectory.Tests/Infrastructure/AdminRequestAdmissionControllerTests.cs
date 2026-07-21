using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Principal;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class AdminRequestAdmissionControllerTests
    {
        private static readonly SecurityIdentifier ActorSid =
            new SecurityIdentifier("S-1-5-21-1-2-3-1001");

        [TestMethod]
        public void ConcurrentRequestLimitIsEightAndHasNoRetryTime()
        {
            long now = 0;
            var controller = new AdminRequestAdmissionController(
                () => now,
                1L);
            var leases = new List<AdminRequestAdmissionLease>();
            for (int index = 0;
                index < AdminRequestAdmissionController
                    .MaximumConcurrentRequests;
                index++)
            {
                AdminRequestAdmissionResult granted = controller.TryAcquire(
                    AdminHttpOperation.GetServices,
                    ActorSid);
                Assert.IsTrue(granted.IsGranted);
                leases.Add(granted.Lease);
            }

            AdminRequestAdmissionResult denied = controller.TryAcquire(
                AdminHttpOperation.GetServices,
                ActorSid);

            Assert.IsFalse(denied.IsGranted);
            Assert.IsFalse(denied.RetryAfterSeconds.HasValue);

            leases[0].Dispose();
            AdminRequestAdmissionResult afterRelease = controller.TryAcquire(
                AdminHttpOperation.GetServices,
                ActorSid);
            Assert.IsTrue(afterRelease.IsGranted);
            afterRelease.Lease.Dispose();
            for (int index = 1; index < leases.Count; index++)
            {
                leases[index].Dispose();
            }
        }

        [TestMethod]
        public void ReadBurstIsFifteenWithinSixtyPerMinuteWindow()
        {
            long now = 0;
            var controller = new AdminRequestAdmissionController(
                () => now,
                1L);
            for (int index = 0; index < 15; index++)
            {
                AcquireAndRelease(
                    controller,
                    AdminHttpOperation.GetRegistrationMode);
            }

            AdminRequestAdmissionResult denied = controller.TryAcquire(
                AdminHttpOperation.GetRegistrationMode,
                ActorSid);
            Assert.IsFalse(denied.IsGranted);
            Assert.AreEqual(1, denied.RetryAfterSeconds.Value);

            now = 1;
            AcquireAndRelease(
                controller,
                AdminHttpOperation.GetRegistrationMode);

            for (now = 2; now <= 45; now++)
            {
                AcquireAndRelease(
                    controller,
                    AdminHttpOperation.GetRegistrationMode);
            }

            now = 46;
            denied = controller.TryAcquire(
                AdminHttpOperation.GetRegistrationMode,
                ActorSid);
            Assert.IsFalse(denied.IsGranted);
            Assert.AreEqual(14, denied.RetryAfterSeconds.Value);

            now = 60;
            AcquireAndRelease(
                controller,
                AdminHttpOperation.GetRegistrationMode);
        }

        [TestMethod]
        public void ChangeLimitAllowsTenThenWaitsForMovingWindow()
        {
            long now = 0;
            var controller = new AdminRequestAdmissionController(
                () => now,
                1L);
            for (int index = 0;
                index < AdminRequestAdmissionController
                    .ChangeRequestsPerMinute;
                index++)
            {
                AcquireAndRelease(
                    controller,
                    AdminHttpOperation.OpenRegistrationMode);
            }

            AdminRequestAdmissionResult denied = controller.TryAcquire(
                AdminHttpOperation.CloseRegistrationMode,
                ActorSid);
            Assert.IsFalse(denied.IsGranted);
            Assert.AreEqual(60, denied.RetryAfterSeconds.Value);

            now = 60;
            AcquireAndRelease(
                controller,
                AdminHttpOperation.CloseRegistrationMode);
        }

        [TestMethod]
        public void SyncNowAllowsTwoAndAlsoConsumesChangeWindow()
        {
            long now = 0;
            var controller = new AdminRequestAdmissionController(
                () => now,
                1L);
            AcquireAndRelease(controller, AdminHttpOperation.SynchronizeNow);
            AcquireAndRelease(controller, AdminHttpOperation.SynchronizeNow);

            AdminRequestAdmissionResult denied = controller.TryAcquire(
                AdminHttpOperation.SynchronizeNow,
                ActorSid);
            Assert.IsFalse(denied.IsGranted);
            Assert.AreEqual(60, denied.RetryAfterSeconds.Value);

            for (int index = 0; index < 8; index++)
            {
                AcquireAndRelease(
                    controller,
                    AdminHttpOperation.DeleteService);
            }

            denied = controller.TryAcquire(
                AdminHttpOperation.DeleteService,
                ActorSid);
            Assert.IsFalse(denied.IsGranted);
            Assert.AreEqual(60, denied.RetryAfterSeconds.Value);

            now = 60;
            AcquireAndRelease(controller, AdminHttpOperation.SynchronizeNow);
        }

        [TestMethod]
        public void RateStateIsPartitionedByExactActorSid()
        {
            var controller = new AdminRequestAdmissionController(
                () => 0L,
                1L);
            AcquireAndRelease(controller, AdminHttpOperation.DeleteService);

            var otherActor = new SecurityIdentifier(
                "S-1-5-21-1-2-3-1002");
            AdminRequestAdmissionResult other = controller.TryAcquire(
                AdminHttpOperation.DeleteService,
                otherActor);

            Assert.IsTrue(other.IsGranted);
            other.Lease.Dispose();
        }

        [TestMethod]
        public void IdentityTrackingCapFailsClosedAndStaleStateExpires()
        {
            long now = 0;
            var controller = new AdminRequestAdmissionController(
                () => now,
                1L);
            for (int index = 0;
                index < AdminRequestAdmissionController
                    .MaximumTrackedIdentities;
                index++)
            {
                var actor = new SecurityIdentifier(
                    "S-1-5-21-1-2-3-"
                        + (1000 + index).ToString(
                            CultureInfo.InvariantCulture));
                AdminRequestAdmissionResult admitted =
                    controller.TryAcquire(
                        AdminHttpOperation.GetServices,
                        actor);
                Assert.IsTrue(admitted.IsGranted);
                admitted.Lease.Dispose();
            }

            var newActor = new SecurityIdentifier(
                "S-1-5-21-1-2-3-9999");
            AdminRequestAdmissionResult denied = controller.TryAcquire(
                AdminHttpOperation.GetServices,
                newActor);
            Assert.IsFalse(denied.IsGranted);
            Assert.IsFalse(denied.RetryAfterSeconds.HasValue);

            now = AdminRequestAdmissionController.StaleIdentityMinutes * 60;
            AdminRequestAdmissionResult afterExpiry = controller.TryAcquire(
                AdminHttpOperation.GetServices,
                newActor);
            Assert.IsTrue(afterExpiry.IsGranted);
            afterExpiry.Lease.Dispose();
        }

        private static void AcquireAndRelease(
            AdminRequestAdmissionController controller,
            AdminHttpOperation operation)
        {
            AdminRequestAdmissionResult result = controller.TryAcquire(
                operation,
                ActorSid);
            Assert.IsTrue(result.IsGranted);
            result.Lease.Dispose();
        }
    }
}
