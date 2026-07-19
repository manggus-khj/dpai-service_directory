using System;
using System.IO;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerSynchronizationSchedulingTests
    {
        [TestMethod]
        public void InitialStartBeforeWriteFailureRestoresPendingMarker()
        {
            bool restored = false;
            var failure = new IOException("append failed");

            IOException observed = Assert.ThrowsExactly<IOException>(
                () => PeerSynchronizationController
                    .WriteSynchronizationStartEvent(
                        () => throw failure,
                        () => restored = true));

            Assert.AreSame(failure, observed);
            Assert.IsTrue(restored);
        }

        [TestMethod]
        public void InitialStartAfterWriteFailureKeepsMarkerConsumed()
        {
            bool restored = false;

            PeerSynchronizationController.WriteSynchronizationStartEvent(
                () => throw new SystemLogRetentionAfterWriteException(
                    new IOException("retention failed")),
                () => restored = true);

            Assert.IsFalse(restored);
        }
    }
}
