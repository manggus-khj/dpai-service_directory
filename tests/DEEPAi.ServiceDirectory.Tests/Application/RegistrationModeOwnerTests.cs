using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Application.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Application
{
    [TestClass]
    public sealed class RegistrationModeOwnerTests
    {
        [TestMethod]
        public void NewProcessOwnerAlwaysStartsClosed()
        {
            var clock = new FakeClock();
            var first = new RegistrationModeOwner(
                new StateMutationGate(),
                clock);
            first.Open();

            var restarted = new RegistrationModeOwner(
                new StateMutationGate(),
                clock);

            Assert.AreEqual(
                RegistrationModeState.Closed,
                restarted.GetSnapshot().State);
        }

        [TestMethod]
        public void ReopeningCurrentWindowDoesNotExtendDeadline()
        {
            var clock = new FakeClock();
            var owner = CreateOwner(clock);
            RegistrationModeSnapshot opened = owner.Open();
            clock.Advance(TimeSpan.FromMinutes(10));

            RegistrationModeSnapshot reopened = owner.Open();

            Assert.AreEqual(RegistrationModeState.Open, reopened.State);
            Assert.AreEqual(opened.OpenedUtc, reopened.OpenedUtc);
            Assert.AreEqual(opened.ExpiresUtc, reopened.ExpiresUtc);
            Assert.AreEqual(3000, reopened.RemainingSeconds);
        }

        [TestMethod]
        public void MonotonicDeadlineClosesWindowWhenUtcMovesBackward()
        {
            var clock = new FakeClock();
            var owner = CreateOwner(clock);
            owner.Open();
            clock.AdvanceUtc(TimeSpan.FromHours(-2));
            clock.AdvanceMonotonic(TimeSpan.FromHours(1));

            RegistrationModeSnapshot snapshot = owner.GetSnapshot();

            Assert.AreEqual(RegistrationModeState.Closed, snapshot.State);
            Assert.IsFalse(snapshot.OpenedUtc.HasValue);
            Assert.IsFalse(snapshot.RemainingSeconds.HasValue);
        }

        [TestMethod]
        public void UtcDeadlineClosesWindowWhenWallClockMovesForward()
        {
            var clock = new FakeClock();
            var owner = CreateOwner(clock);
            owner.Open();
            clock.AdvanceUtc(TimeSpan.FromHours(1));
            clock.AdvanceMonotonic(TimeSpan.FromMinutes(1));

            Assert.AreEqual(
                RegistrationModeState.Closed,
                owner.GetSnapshot().State);
        }

        [TestMethod]
        public void InvalidAdmissionResultsNeverConsumeOpenWindow()
        {
            var clock = new FakeClock();
            var owner = CreateOwner(clock);
            owner.Open();

            for (int index = 0; index < 4; index++)
            {
                RegistrationModeClaimResult invalid =
                    owner.TryClaimValidatedRequest(false);
                Assert.AreEqual(
                    RegistrationModeClaimStatus.InvalidRequest,
                    invalid.Status);
                Assert.IsNull(invalid.Claim);
                Assert.AreEqual(
                    RegistrationModeState.Open,
                    owner.GetSnapshot().State);
            }

            RegistrationModeClaimResult valid =
                owner.TryClaimValidatedRequest(true);

            Assert.AreEqual(
                RegistrationModeClaimStatus.Claimed,
                valid.Status);
            Assert.IsNotNull(valid.Claim);
            valid.Claim.Dispose();
            Assert.AreEqual(
                RegistrationModeState.Closed,
                owner.GetSnapshot().State);
        }

        [TestMethod]
        public void ConcurrentValidRequestsClaimExactlyOnce()
        {
            var owner = CreateOwner(new FakeClock());
            owner.Open();
            const int RequestCount = 32;
            var start = new ManualResetEventSlim(false);
            var results = new RegistrationModeClaimResult[RequestCount];
            Task[] tasks = Enumerable.Range(0, RequestCount)
                .Select(index => Task.Run(() =>
                {
                    start.Wait();
                    results[index] = owner.TryClaimValidatedRequest(true);
                }))
                .ToArray();

            start.Set();
            Task.WaitAll(tasks);

            RegistrationModeClaimResult winner = results.Single(
                result => result.Status
                    == RegistrationModeClaimStatus.Claimed);
            Assert.AreEqual(
                RequestCount - 1,
                results.Count(result => result.Status
                    == RegistrationModeClaimStatus.AlreadyClaimed));
            Assert.AreEqual(
                RegistrationModeState.Claimed,
                owner.GetSnapshot().State);
            winner.Claim.Dispose();
            Assert.AreEqual(
                RegistrationModeState.Closed,
                owner.GetSnapshot().State);
            start.Dispose();
        }

        [TestMethod]
        public void ClaimedWindowCannotBeClosedByAdministrativeRequest()
        {
            var owner = CreateOwner(new FakeClock());
            owner.Open();
            RegistrationModeClaim claim = owner
                .TryClaimValidatedRequest(true)
                .Claim;

            RegistrationModeSnapshot closed = owner.Close();

            Assert.AreEqual(
                RegistrationModeState.Claimed,
                closed.State);
            Assert.AreEqual(
                RegistrationModeState.Claimed,
                owner.GetSnapshot().State);
            claim.Dispose();
        }

        [TestMethod]
        public void ClaimedWindowCannotBeReopenedOrClaimedAgain()
        {
            var owner = CreateOwner(new FakeClock());
            owner.Open();
            RegistrationModeClaimResult first =
                owner.TryClaimValidatedRequest(true);

            RegistrationModeSnapshot reopened = owner.Open();
            RegistrationModeClaimResult second =
                owner.TryClaimValidatedRequest(true);

            Assert.AreEqual(
                RegistrationModeState.Claimed,
                reopened.State);
            Assert.AreEqual(
                RegistrationModeClaimStatus.AlreadyClaimed,
                second.Status);
            first.Claim.Dispose();
        }

        [TestMethod]
        public void CompletedClaimClosesWindowAndPublishesLastResult()
        {
            var clock = new FakeClock();
            var owner = CreateOwner(clock);
            owner.Open();
            RegistrationModeClaim claim = owner
                .TryClaimValidatedRequest(true)
                .Claim;
            DateTime certificateNotAfterUtc =
                clock.UtcNow.AddYears(1);

            claim.Complete(RegistrationModeLastResult.Success(
                clock.UtcNow,
                RegistrationModeCompletionOutcome.Registered,
                "AB12",
                "service.internal",
                "10.20.30.40",
                "1234567890ABCDEF1234567890ABCDEF",
                certificateNotAfterUtc));

            RegistrationModeSnapshot snapshot = owner.GetSnapshot();
            Assert.AreEqual(RegistrationModeState.Closed, snapshot.State);
            Assert.IsNotNull(snapshot.LastResult);
            Assert.AreEqual(
                RegistrationModeCompletionOutcome.Registered,
                snapshot.LastResult.Outcome);
            Assert.AreEqual("AB12", snapshot.LastResult.ProductCode);
            Assert.AreEqual(
                certificateNotAfterUtc,
                snapshot.LastResult.CertificateNotAfterUtc);
        }

        [TestMethod]
        public void InvalidClockValuesFailClosed()
        {
            var nonUtc = new FakeClock
            {
                UtcNow = DateTime.SpecifyKind(
                    DateTime.UtcNow,
                    DateTimeKind.Local)
            };
            Assert.ThrowsExactly<InvalidOperationException>(
                () => CreateOwner(nonUtc).Open());

            var negativeMonotonic = new FakeClock
            {
                MonotonicElapsed = TimeSpan.FromTicks(-1)
            };
            Assert.ThrowsExactly<InvalidOperationException>(
                () => CreateOwner(negativeMonotonic).Open());
        }

        private static RegistrationModeOwner CreateOwner(FakeClock clock)
        {
            return new RegistrationModeOwner(
                new StateMutationGate(),
                clock);
        }

        private sealed class FakeClock : IRegistrationModeClock
        {
            internal FakeClock()
            {
                UtcNow = new DateTime(
                    2026,
                    7,
                    21,
                    9,
                    0,
                    0,
                    DateTimeKind.Utc);
                MonotonicElapsed = TimeSpan.FromHours(100);
            }

            public DateTime UtcNow { get; set; }

            public TimeSpan MonotonicElapsed { get; set; }

            internal void Advance(TimeSpan elapsed)
            {
                AdvanceUtc(elapsed);
                AdvanceMonotonic(elapsed);
            }

            internal void AdvanceUtc(TimeSpan elapsed)
            {
                UtcNow = UtcNow.Add(elapsed);
            }

            internal void AdvanceMonotonic(TimeSpan elapsed)
            {
                MonotonicElapsed = MonotonicElapsed.Add(elapsed);
            }
        }
    }
}
