using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.ExternalProtocol
{
    [TestClass]
    public sealed class ExternalRequestConcurrencyLimiterTests
    {
        [TestMethod]
        public void ThirtyTwoLeasesAreAcceptedAndThirtyThirdIsRejected()
        {
            var limiter = new ExternalRequestConcurrencyLimiter();
            var leases = new List<IDisposable>();
            try
            {
                for (int index = 0;
                    index < ExternalRequestConcurrencyLimiter
                        .MaximumConcurrentRequests;
                    index++)
                {
                    Assert.IsTrue(limiter.TryAcquire(out IDisposable lease));
                    Assert.IsNotNull(lease);
                    leases.Add(lease);
                }

                Assert.AreEqual(
                    ExternalRequestConcurrencyLimiter
                        .MaximumConcurrentRequests,
                    limiter.ActiveRequests);
                Assert.IsFalse(limiter.TryAcquire(out IDisposable rejected));
                Assert.IsNull(rejected);
            }
            finally
            {
                foreach (IDisposable lease in leases)
                {
                    lease.Dispose();
                }
            }

            Assert.AreEqual(0, limiter.ActiveRequests);
        }

        [TestMethod]
        public void DisposedLeaseImmediatelyRestoresOneCapacitySlot()
        {
            var limiter = new ExternalRequestConcurrencyLimiter();
            var leases = new List<IDisposable>();
            for (int index = 0;
                index < ExternalRequestConcurrencyLimiter
                    .MaximumConcurrentRequests;
                index++)
            {
                Assert.IsTrue(limiter.TryAcquire(out IDisposable lease));
                leases.Add(lease);
            }

            leases[0].Dispose();
            Assert.AreEqual(
                ExternalRequestConcurrencyLimiter
                    .MaximumConcurrentRequests - 1,
                limiter.ActiveRequests);
            Assert.IsTrue(limiter.TryAcquire(out IDisposable replacement));
            Assert.IsNotNull(replacement);

            replacement.Dispose();
            for (int index = 1; index < leases.Count; index++)
            {
                leases[index].Dispose();
            }

            Assert.AreEqual(0, limiter.ActiveRequests);
        }

        [TestMethod]
        public void DisposingSameLeaseTwiceDoesNotReleaseExtraCapacity()
        {
            var limiter = new ExternalRequestConcurrencyLimiter();
            Assert.IsTrue(limiter.TryAcquire(out IDisposable lease));

            lease.Dispose();
            lease.Dispose();

            Assert.AreEqual(0, limiter.ActiveRequests);
            var acquired = new List<IDisposable>();
            try
            {
                for (int index = 0;
                    index < ExternalRequestConcurrencyLimiter
                        .MaximumConcurrentRequests;
                    index++)
                {
                    Assert.IsTrue(limiter.TryAcquire(
                        out IDisposable current));
                    acquired.Add(current);
                }

                Assert.IsFalse(limiter.TryAcquire(
                    out IDisposable rejected));
                Assert.IsNull(rejected);
            }
            finally
            {
                foreach (IDisposable current in acquired)
                {
                    current.Dispose();
                }
            }
        }
    }
}
