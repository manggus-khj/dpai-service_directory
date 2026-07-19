using System.Text;
using DEEPAi.ServiceDirectory.Watchdog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Watchdog
{
    [TestClass]
    public sealed class WatchdogHealthResponseValidatorTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void CanonicalHealthSuccessIsAccepted()
        {
            Assert.IsTrue(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00.1234567Z</UtcNow>"
                + "</Response>")));
        }

        [TestMethod]
        public void EmptyExtensionsAtTheFinalExpansionPointIsAccepted()
        {
            Assert.IsTrue(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message></Message>"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow>"
                + "<Extensions><Future xmlns=\"urn:test\" /></Extensions>"
                + "</Response>")));
        }

        [TestMethod]
        public void ExtensionsRejectDirectTextAndNonNamespaceAttributes()
        {
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow>"
                + "<Extensions>text</Extensions></Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow>"
                + "<Extensions value=\"x\" /></Response>")));
        }

        [TestMethod]
        public void WrongNamespaceResultCodeAndPayloadShapeAreRejected()
        {
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response><Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow></Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>ERROR</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow></Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>1000</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow></Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<Service /></Response>")));
        }

        [TestMethod]
        public void ContractUtcFractionsAreAcceptedAndUnknownFieldsRejected()
        {
            Assert.IsTrue(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00.120Z</UtcNow></Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result x=\"1\">OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow></Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<Unexpected /><UtcNow>2026-07-18T04:00:00Z</UtcNow>"
                + "</Response>")));
        }

        [TestMethod]
        public void EmptyOrOverlongUtcFractionIsRejected()
        {
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00.Z</UtcNow></Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00.12345678Z</UtcNow>"
                + "</Response>")));
        }

        [TestMethod]
        public void CommentsProcessingInstructionsAndCdataAreRejected()
        {
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>O<!-- hidden -->K</Result><Code>0</Code>"
                + "<Message /><UtcNow>2026-07-18T04:00:00Z</UtcNow>"
                + "</Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code><?value x?>0</Code>"
                + "<Message /><UtcNow>2026-07-18T04:00:00Z</UtcNow>"
                + "</Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result><![CDATA[OK]]></Result><Code>0</Code>"
                + "<Message /><UtcNow>2026-07-18T04:00:00Z</UtcNow>"
                + "</Response>")));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>OK</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:<!-- hidden -->00Z</UtcNow>"
                + "</Response>")));
        }

        [TestMethod]
        public void BomInvalidUtf8AndDtdAreRejected()
        {
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(
                new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<' }));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(
                new byte[] { 0xC3, 0x28 }));
            Assert.IsFalse(WatchdogHealthResponseValidator.IsValid(Bytes(
                "<!DOCTYPE Response [<!ENTITY x \"OK\">]>"
                + "<Response xmlns=\"urn:deepai:service-directory:external\">"
                + "<Result>&x;</Result><Code>0</Code><Message />"
                + "<UtcNow>2026-07-18T04:00:00Z</UtcNow></Response>")));
        }

        private static byte[] Bytes(string value)
        {
            return StrictUtf8.GetBytes(value);
        }
    }
}
