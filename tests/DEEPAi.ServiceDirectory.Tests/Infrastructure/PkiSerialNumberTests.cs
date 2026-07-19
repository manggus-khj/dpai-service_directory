using System;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PkiSerialNumberTests
    {
        [TestMethod]
        public void GeneratedSerialUsesExactPositiveCanonicalWireForm()
        {
            PkiSerialNumber serial = PkiSerialNumber.CreateRandom(
                new SecureRandom(),
                null);

            Assert.AreEqual(32, serial.Hex.Length);
            StringAssert.Matches(
                serial.Hex,
                new System.Text.RegularExpressions.Regex("^[0-9A-F]{32}$"));
            Assert.IsTrue(serial.GetBytes()[0] >= 0x01);
            Assert.IsTrue(serial.GetBytes()[0] <= 0x7f);

            PkiSerialNumber parsed;
            Assert.IsTrue(PkiSerialNumber.TryParse(serial.Hex, out parsed));
            Assert.AreEqual(serial, parsed);
            Assert.AreEqual(serial.Hex, serial.ToLedgerSerialNumber().Hex);
            Assert.IsFalse(PkiSerialNumber.TryParse(serial.Hex.ToLowerInvariant(), out parsed));
            Assert.IsFalse(PkiSerialNumber.TryParse(
                "00" + serial.Hex.Substring(2),
                out parsed));
        }

        [TestMethod]
        public void GeneratorRejectsReservedCandidatesWithinBoundedAttempts()
        {
            int reservationChecks = 0;

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                PkiSerialNumber.CreateRandom(
                    new SecureRandom(),
                    value =>
                    {
                        reservationChecks++;
                        return true;
                    }));

            Assert.IsTrue(reservationChecks > 0);
            Assert.IsTrue(reservationChecks <= 128);
        }
    }
}
