using System;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PairingDecisionReplayEntryTests
    {
        [TestMethod]
        public void ExactRequestReturnsStoredSignedResponse()
        {
            byte[] requestBody = Encoding.UTF8.GetBytes("request");
            byte[] requestMac = CreateSequence(0x10);
            byte[] responseBody = Encoding.UTF8.GetBytes("response");
            byte[] responseMac = CreateSequence(0x40);

            using (var entry = new PairingDecisionReplayEntry(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "http://10.0.0.2:21000",
                PeerPairingDecisionValue.Confirmed,
                PairingDecisionReplayEntry.CreateDeadline(
                    TimeSpan.FromMinutes(1)),
                requestBody,
                requestMac,
                200,
                responseBody,
                responseMac))
            {
                byte[] replayBody;
                byte[] replayMac;
                Assert.IsTrue(entry.TryCopyResponse(
                    requestBody,
                    requestMac,
                    out replayBody,
                    out replayMac));
                CollectionAssert.AreEqual(responseBody, replayBody);
                CollectionAssert.AreEqual(responseMac, replayMac);

                replayBody[0] ^= 0xff;
                replayMac[0] ^= 0xff;
                byte[] secondBody;
                byte[] secondMac;
                Assert.IsTrue(entry.TryCopyResponse(
                    requestBody,
                    requestMac,
                    out secondBody,
                    out secondMac));
                CollectionAssert.AreEqual(responseBody, secondBody);
                CollectionAssert.AreEqual(responseMac, secondMac);
            }
        }

        [TestMethod]
        public void ReencodedBodyOrDifferentMacIsRejected()
        {
            byte[] requestBody = Encoding.UTF8.GetBytes("request");
            byte[] requestMac = CreateSequence(0x10);
            using (var entry = new PairingDecisionReplayEntry(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "http://10.0.0.2:21000",
                PeerPairingDecisionValue.Cancelled,
                PairingDecisionReplayEntry.CreateDeadline(
                    TimeSpan.FromMinutes(1)),
                requestBody,
                requestMac,
                200,
                Encoding.UTF8.GetBytes("response"),
                CreateSequence(0x40)))
            {
                byte[] ignoredBody;
                byte[] ignoredMac;
                Assert.IsFalse(entry.TryCopyResponse(
                    Encoding.UTF8.GetBytes("request "),
                    requestMac,
                    out ignoredBody,
                    out ignoredMac));
                Assert.IsNull(ignoredBody);
                Assert.IsNull(ignoredMac);

                byte[] changedMac = (byte[])requestMac.Clone();
                changedMac[31] ^= 0x01;
                Assert.IsFalse(entry.TryCopyResponse(
                    requestBody,
                    changedMac,
                    out ignoredBody,
                    out ignoredMac));
                Assert.IsNull(ignoredBody);
                Assert.IsNull(ignoredMac);
            }
        }

        private static byte[] CreateSequence(byte start)
        {
            var value = new byte[32];
            for (int index = 0; index < value.Length; index++)
            {
                value[index] = checked((byte)(start + index));
            }

            return value;
        }
    }
}
