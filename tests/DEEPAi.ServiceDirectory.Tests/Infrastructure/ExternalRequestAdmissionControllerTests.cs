using System;
using System.Collections.Generic;
using System.Net;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class ExternalRequestAdmissionControllerTests
    {
        [TestMethod]
        public void RegistrationAppliesProductAndRemoteBucketsTogether()
        {
            long timestamp = 0;
            var controller = CreateController(() => timestamp);
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.10");
            ProductCode productCode = Code("AB12");

            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Registration,
                productCode,
                remoteAddress);
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Registration,
                productCode,
                remoteAddress);

            ExternalRequestAdmissionResult productDenied =
                controller.TryAcquire(
                    ExternalHttpEndpoint.Registration,
                    productCode,
                    remoteAddress);
            AssertTimedRateLimit(productDenied, 20);

            timestamp = 20;
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Registration,
                productCode,
                remoteAddress);

            timestamp = 0;
            controller = CreateController(() => timestamp);
            for (int index = 0; index < 20; index++)
            {
                GrantAndRelease(
                    controller,
                    ExternalHttpEndpoint.Registration,
                    Code(ProductCodeFor(index)),
                    remoteAddress);
            }

            ProductCode twentyFirstCode = Code(ProductCodeFor(20));
            ExternalRequestAdmissionResult remoteDenied =
                controller.TryAcquire(
                    ExternalHttpEndpoint.Registration,
                    twentyFirstCode,
                    remoteAddress);
            AssertTimedRateLimit(remoteDenied, 3);

            timestamp = 3;
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Registration,
                twentyFirstCode,
                remoteAddress);
        }

        [TestMethod]
        public void HealthCombinationHasCapacityFiveAndRefillsThirtyPerMinute()
        {
            long timestamp = 0;
            var controller = CreateController(() => timestamp);
            ProductCode productCode = Code("WDOG");
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.20");

            for (int index = 0; index < 5; index++)
            {
                GrantAndRelease(
                    controller,
                    ExternalHttpEndpoint.Health,
                    productCode,
                    remoteAddress);
            }

            AssertTimedRateLimit(
                controller.TryAcquire(
                    ExternalHttpEndpoint.Health,
                    productCode,
                    remoteAddress),
                2);

            timestamp = 2;
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Health,
                productCode,
                remoteAddress);
        }

        [TestMethod]
        public void ServicesHasOneTokenCapacityForProductAndRemoteBuckets()
        {
            long timestamp = 0;
            var controller = CreateController(() => timestamp);
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.30");
            ProductCode productCode = Code("AB12");

            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Services,
                productCode,
                remoteAddress);
            AssertTimedRateLimit(
                controller.TryAcquire(
                    ExternalHttpEndpoint.Services,
                    productCode,
                    remoteAddress),
                5);

            timestamp = 5;
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Services,
                productCode,
                remoteAddress);

            timestamp = 0;
            controller = CreateController(() => timestamp);
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Services,
                Code("AB12"),
                remoteAddress);
            AssertTimedRateLimit(
                controller.TryAcquire(
                    ExternalHttpEndpoint.Services,
                    Code("CD34"),
                    remoteAddress),
                1);
        }

        [TestMethod]
        public void EndpointPrefixesShareAggregateTrackingMapCapacity()
        {
            var controller = CreateController(() => 0L);
            ProductCode productCode = Code("AB12");
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.40");

            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Health,
                productCode,
                remoteAddress);
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Services,
                productCode,
                remoteAddress);
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Registration,
                productCode,
                remoteAddress);

            Assert.AreEqual(3, controller.TrackedProductCodeKeyCount);
            Assert.AreEqual(2, controller.TrackedRemoteAddressKeyCount);
        }

        [TestMethod]
        public void TrackingMapFailsClosedUntilFullBucketIsIdleTwoMinutes()
        {
            long timestamp = 0;
            var controller = CreateController(() => timestamp);
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.50");

            for (int index = 0;
                index < ExternalRequestAdmissionController
                    .MaximumTrackedProductCodeKeys;
                index++)
            {
                ExternalRequestAdmissionResult result =
                    controller.TryAcquire(
                        ExternalHttpEndpoint.Health,
                        Code(ProductCodeFor(index)),
                        remoteAddress);
                Assert.IsTrue(result.IsGranted);
                result.Lease.Dispose();
            }

            Assert.AreEqual(
                ExternalRequestAdmissionController
                    .MaximumTrackedProductCodeKeys,
                controller.TrackedProductCodeKeyCount);

            timestamp = ExternalRequestAdmissionController
                .IdleCleanupSeconds - 1;
            ExternalRequestAdmissionResult denied = controller.TryAcquire(
                ExternalHttpEndpoint.Health,
                Code("ZZZZ"),
                remoteAddress);
            Assert.IsFalse(denied.IsGranted);
            Assert.AreEqual(
                ExternalRequestAdmissionFailure.TrackingCapacity,
                denied.Failure);
            Assert.IsFalse(denied.RetryAfterSeconds.HasValue);

            timestamp = ExternalRequestAdmissionController
                .IdleCleanupSeconds;
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Registration,
                Code("ZZZZ"),
                remoteAddress);
            Assert.AreEqual(
                ExternalRequestAdmissionController
                    .MaximumTrackedProductCodeKeys,
                controller.TrackedProductCodeKeyCount);
        }

        [TestMethod]
        public void RemoteTrackingMapUsesTheSameFailClosedCleanupContract()
        {
            long timestamp = 0;
            var controller = CreateController(() => timestamp);
            ProductCode productCode = Code("AB12");

            for (int index = 0;
                index < ExternalRequestAdmissionController
                    .MaximumTrackedRemoteAddressKeys;
                index++)
            {
                ExternalRequestAdmissionResult result =
                    controller.TryAcquire(
                        ExternalHttpEndpoint.Services,
                        productCode,
                        RemoteAddressFor(index));
                if (result.IsGranted)
                {
                    result.Lease.Dispose();
                }
                else
                {
                    Assert.AreEqual(
                        ExternalRequestAdmissionFailure.RateLimit,
                        result.Failure);
                }
            }

            Assert.AreEqual(
                ExternalRequestAdmissionController
                    .MaximumTrackedRemoteAddressKeys,
                controller.TrackedRemoteAddressKeyCount);

            timestamp = ExternalRequestAdmissionController
                .IdleCleanupSeconds - 1;
            ExternalRequestAdmissionResult denied = controller.TryAcquire(
                ExternalHttpEndpoint.Services,
                productCode,
                RemoteAddressFor(
                    ExternalRequestAdmissionController
                        .MaximumTrackedRemoteAddressKeys));
            Assert.IsFalse(denied.IsGranted);
            Assert.AreEqual(
                ExternalRequestAdmissionFailure.TrackingCapacity,
                denied.Failure);
            Assert.IsFalse(denied.RetryAfterSeconds.HasValue);

            timestamp = ExternalRequestAdmissionController.IdleCleanupSeconds;
            GrantAndRelease(
                controller,
                ExternalHttpEndpoint.Services,
                productCode,
                RemoteAddressFor(
                    ExternalRequestAdmissionController
                        .MaximumTrackedRemoteAddressKeys));
            Assert.AreEqual(
                ExternalRequestAdmissionController
                    .MaximumTrackedRemoteAddressKeys,
                controller.TrackedRemoteAddressKeyCount);
        }

        [TestMethod]
        public void UndefinedRouteUsesOnlyConcurrencyAndNoRateState()
        {
            var controller = CreateController(() => 0L);
            ProductCode productCode = Code("AB12");
            IPAddress remoteAddress = IPAddress.Parse("192.0.2.60");

            for (int index = 0; index < 100; index++)
            {
                GrantAndRelease(
                    controller,
                    ExternalHttpEndpoint.Undefined,
                    productCode,
                    remoteAddress);
            }

            Assert.AreEqual(0, controller.TrackedProductCodeKeyCount);
            Assert.AreEqual(0, controller.TrackedRemoteAddressKeyCount);
        }

        [TestMethod]
        public void ConcurrencyCapacityOmitsRetryAfterAndDoesNotLeak()
        {
            var limiter = new ExternalRequestConcurrencyLimiter();
            var heldLeases = new List<IDisposable>();
            try
            {
                for (int index = 0;
                    index < ExternalRequestConcurrencyLimiter
                        .MaximumConcurrentRequests;
                    index++)
                {
                    IDisposable heldLease;
                    Assert.IsTrue(limiter.TryAcquire(out heldLease));
                    heldLeases.Add(heldLease);
                }

                var controller = new ExternalRequestAdmissionController(
                    limiter,
                    () => 0L,
                    1L);
                ExternalRequestAdmissionResult denied =
                    controller.TryAcquire(
                        ExternalHttpEndpoint.Registration,
                        Code("AB12"),
                        IPAddress.Parse("192.0.2.70"));

                Assert.IsFalse(denied.IsGranted);
                Assert.AreEqual(
                    ExternalRequestAdmissionFailure.ConcurrencyLimit,
                    denied.Failure);
                Assert.IsFalse(denied.RetryAfterSeconds.HasValue);
            }
            finally
            {
                foreach (IDisposable lease in heldLeases)
                {
                    lease.Dispose();
                }
            }
        }

        [TestMethod]
        public void OnlyTokenShortageMayHavePositiveRetryAfter()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.RateLimit,
                    0));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.ConcurrencyLimit,
                    1));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.TrackingCapacity,
                    120));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.RateLimit));

            ExternalRequestAdmissionResult concurrency =
                ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.ConcurrencyLimit);
            ExternalRequestAdmissionResult tracking =
                ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.TrackingCapacity);
            ExternalRequestAdmissionResult timed =
                ExternalRequestAdmissionResult.Denied(
                    ExternalRequestAdmissionFailure.RateLimit,
                    1);

            Assert.IsFalse(concurrency.RetryAfterSeconds.HasValue);
            Assert.IsFalse(tracking.RetryAfterSeconds.HasValue);
            Assert.AreEqual(1, timed.RetryAfterSeconds);
        }

        private static ExternalRequestAdmissionController CreateController(
            Func<long> timestampProvider)
        {
            return new ExternalRequestAdmissionController(
                new ExternalRequestConcurrencyLimiter(),
                timestampProvider,
                1L);
        }

        private static void GrantAndRelease(
            ExternalRequestAdmissionController controller,
            ExternalHttpEndpoint endpoint,
            ProductCode productCode,
            IPAddress remoteAddress)
        {
            ExternalRequestAdmissionResult result = controller.TryAcquire(
                endpoint,
                productCode,
                remoteAddress);
            Assert.IsTrue(result.IsGranted);
            Assert.AreEqual(
                ExternalRequestAdmissionFailure.None,
                result.Failure);
            Assert.IsFalse(result.RetryAfterSeconds.HasValue);
            result.Lease.Dispose();
        }

        private static void AssertTimedRateLimit(
            ExternalRequestAdmissionResult result,
            int expectedRetryAfterSeconds)
        {
            Assert.IsFalse(result.IsGranted);
            Assert.AreEqual(
                ExternalRequestAdmissionFailure.RateLimit,
                result.Failure);
            Assert.AreEqual(
                expectedRetryAfterSeconds,
                result.RetryAfterSeconds);
        }

        private static ProductCode Code(string rawValue)
        {
            ProductCode productCode;
            Assert.IsTrue(ProductCode.TryCreate(rawValue, out productCode));
            return productCode;
        }

        private static string ProductCodeFor(int value)
        {
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var characters = new char[4];
            for (int index = characters.Length - 1; index >= 0; index--)
            {
                characters[index] = alphabet[value % alphabet.Length];
                value /= alphabet.Length;
            }

            return new string(characters);
        }

        private static IPAddress RemoteAddressFor(int value)
        {
            return new IPAddress(
                new byte[]
                {
                    198,
                    18,
                    (byte)((value >> 8) & 0xff),
                    (byte)(value & 0xff)
                });
        }
    }
}
