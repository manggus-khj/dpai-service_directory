using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerOutboundSnapshotLeaseTests
    {
        private static readonly Guid LocalInstanceId =
            new Guid("11111111-1111-1111-1111-111111111111");

        private static readonly Guid OriginInstanceId =
            new Guid("22222222-2222-2222-2222-222222222222");

        private static readonly Guid SnapshotId =
            new Guid("33333333-3333-3333-3333-333333333333");

        [TestMethod]
        public void EmptySnapshotProducesOneCanonicalLastBatch()
        {
            var lease = new PeerOutboundSnapshotLease(
                LocalInstanceId,
                SnapshotId,
                new SynchronizationSnapshot(
                    new ServiceRecord[0],
                    0UL));

            PeerOutboundBatchReadResult result = lease.Read(
                new PeerPullExchangeRequest(SnapshotId, 0U));

            Assert.AreEqual(PeerOutboundBatchReadStatus.Served, result.Status);
            Assert.AreEqual(0, result.Batch.Items.Count);
            Assert.AreEqual(0UL, result.Batch.TotalCount);
            Assert.AreEqual(0U, result.Batch.BatchIndex);
            Assert.IsTrue(result.Batch.IsLastBatch);
            Assert.IsTrue(lease.IsComplete);
        }

        [TestMethod]
        public void SnapshotIsSortedAndSplitAtOneThousandItems()
        {
            var reversed = new List<ServiceRecord>();
            for (int index = 1000; index >= 0; index--)
            {
                reversed.Add(CreateRecord(index));
            }

            var lease = new PeerOutboundSnapshotLease(
                LocalInstanceId,
                SnapshotId,
                new SynchronizationSnapshot(reversed, 1001UL));

            PeerOutboundBatchReadResult first = lease.Read(
                new PeerPullExchangeRequest(SnapshotId, 0U));
            PeerOutboundBatchReadResult second = lease.Read(
                new PeerPullExchangeRequest(SnapshotId, 1U));

            Assert.AreEqual(2, lease.BatchCount);
            Assert.AreEqual(1000, first.Batch.Items.Count);
            Assert.AreEqual(1, second.Batch.Items.Count);
            Assert.AreEqual(1001UL, first.Batch.TotalCount);
            Assert.AreEqual(1001UL, second.Batch.TotalCount);
            Assert.IsFalse(first.Batch.IsLastBatch);
            Assert.IsTrue(second.Batch.IsLastBatch);
            Assert.AreEqual("0000", first.Batch.Items[0].ProductCode);
            Assert.AreEqual("00RR", first.Batch.Items[999].ProductCode);
            Assert.AreEqual("00RS", second.Batch.Items[0].ProductCode);
            Assert.IsTrue(lease.IsComplete);
        }

        [TestMethod]
        public void PreviouslyServedBatchCanBeReadAgainWithoutAdvancing()
        {
            var lease = CreateSmallLease();

            PeerOutboundBatchReadResult first = lease.Read(
                new PeerPullExchangeRequest(SnapshotId, 0U));
            PeerOutboundBatchReadResult replay = lease.Read(
                new PeerPullExchangeRequest(SnapshotId, 0U));

            Assert.AreEqual(PeerOutboundBatchReadStatus.Served, first.Status);
            Assert.AreEqual(
                PeerOutboundBatchReadStatus.ServedReplay,
                replay.Status);
            Assert.AreSame(first.Batch, replay.Batch);
            Assert.AreEqual(1U, lease.NextBatchIndex);
        }

        [TestMethod]
        public void WrongSnapshotAndSkippedIndexFailWithoutAdvancing()
        {
            var lease = CreateSmallLease();

            PeerOutboundBatchReadResult mismatch = lease.Read(
                new PeerPullExchangeRequest(Guid.NewGuid(), 0U));
            PeerOutboundBatchReadResult skipped = lease.Read(
                new PeerPullExchangeRequest(SnapshotId, 1U));

            Assert.AreEqual(
                PeerOutboundBatchReadStatus.SnapshotMismatch,
                mismatch.Status);
            Assert.AreEqual(
                PeerOutboundBatchReadStatus.UnexpectedBatchIndex,
                skipped.Status);
            Assert.AreEqual(0U, lease.NextBatchIndex);
        }

        private static PeerOutboundSnapshotLease CreateSmallLease()
        {
            return new PeerOutboundSnapshotLease(
                LocalInstanceId,
                SnapshotId,
                new SynchronizationSnapshot(
                    new[] { CreateRecord(0) },
                    1UL));
        }

        private static ServiceRecord CreateRecord(int index)
        {
            string productCode = ToProductCode(index);
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;
            Assert.IsTrue(
                ServiceDefinition.TryCreate(
                    "Service " + productCode,
                    productCode,
                    "10.0.0.1",
                    21001,
                    out definition,
                    out error));

            ulong version = checked((ulong)index + 1UL);
            return new ServiceRecord(
                definition,
                new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
                false,
                null,
                version,
                OriginInstanceId);
        }

        private static string ToProductCode(int value)
        {
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var characters = new char[4];
            int remaining = value;
            for (int index = characters.Length - 1; index >= 0; index--)
            {
                characters[index] = alphabet[remaining % alphabet.Length];
                remaining /= alphabet.Length;
            }

            return new string(characters);
        }
    }
}
