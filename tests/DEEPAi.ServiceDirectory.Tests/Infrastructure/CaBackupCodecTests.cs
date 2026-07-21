using System;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CaBackupCodecTests
    {
        private const string Password = "correct horse battery staple";

        [TestMethod]
        public void EncryptedBackupRoundTripsEveryComponent()
        {
            var codec = new CaBackupCodec();
            using (CaBackupPayload source = CreatePayload())
            {
                byte[] encrypted = codec.Encrypt(source, Password);
                try
                {
                    Assert.IsTrue(encrypted.Length > 32);
                    using (CaBackupPayload restored = codec.Decrypt(
                        encrypted,
                        Password))
                    {
                        CollectionAssert.AreEqual(
                            source.Metadata,
                            restored.Metadata);
                        CollectionAssert.AreEqual(
                            source.Ledger,
                            restored.Ledger);
                        CollectionAssert.AreEqual(
                            source.CaCertificateDer,
                            restored.CaCertificateDer);
                        CollectionAssert.AreEqual(
                            source.CrlDer,
                            restored.CrlDer);
                        CollectionAssert.AreEqual(
                            source.PrivateKeyPkcs8,
                            restored.PrivateKeyPkcs8);
                    }
                }
                finally
                {
                    Array.Clear(encrypted, 0, encrypted.Length);
                }
            }
        }

        [TestMethod]
        public void EncryptedBackupRoundTripsBothAuthoritySlots()
        {
            var codec = new CaBackupCodec();
            using (var source = new CaBackupPayload(
                new byte[] { 1, 2, 3 },
                new byte[] { 4, 5, 6, 7 },
                new byte[] { 8, 9 },
                new byte[] { 10, 11, 12 },
                new byte[] { 13, 14, 15, 16 },
                new byte[] { 17, 18 },
                new byte[] { 19, 20, 21 },
                new byte[] { 22, 23, 24, 25 }))
            {
                byte[] encrypted = codec.Encrypt(source, Password);
                try
                {
                    using (CaBackupPayload restored = codec.Decrypt(
                        encrypted,
                        Password))
                    {
                        Assert.IsTrue(restored.HasOtherAuthority);
                        CollectionAssert.AreEqual(
                            source.OtherCaCertificateDer,
                            restored.OtherCaCertificateDer);
                        CollectionAssert.AreEqual(
                            source.OtherCrlDer,
                            restored.OtherCrlDer);
                        CollectionAssert.AreEqual(
                            source.OtherPrivateKeyPkcs8,
                            restored.OtherPrivateKeyPkcs8);
                    }
                }
                finally
                {
                    Array.Clear(encrypted, 0, encrypted.Length);
                }
            }
        }

        [TestMethod]
        public void EncryptedBackupRejectsTamperingBeforePayloadParsing()
        {
            var codec = new CaBackupCodec();
            using (CaBackupPayload source = CreatePayload())
            {
                byte[] encrypted = codec.Encrypt(source, Password);
                try
                {
                    encrypted[encrypted.Length / 2] ^= 0x01;
                    Assert.ThrowsExactly<CryptographicException>(
                        () => codec.Decrypt(encrypted, Password));
                }
                finally
                {
                    Array.Clear(encrypted, 0, encrypted.Length);
                }
            }
        }

        [TestMethod]
        public void BackupPasswordEnforcesScalarAndUtf8Bounds()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => CaBackupCodec.ValidatePassword("short"));
            CaBackupCodec.ValidatePassword(new string('가', 128));
            Assert.ThrowsExactly<ArgumentException>(
                () => CaBackupCodec.ValidatePassword(new string('가', 129)));
            Assert.ThrowsExactly<ArgumentException>(
                () => CaBackupCodec.ValidatePassword(
                    "valid-length\npassword"));
        }

        private static CaBackupPayload CreatePayload()
        {
            return new CaBackupPayload(
                new byte[] { 1, 2, 3 },
                new byte[] { 4, 5, 6, 7 },
                new byte[] { 8, 9 },
                new byte[] { 10, 11, 12 },
                new byte[] { 13, 14, 15, 16 });
        }
    }
}
