using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.ExternalProtocol
{
    [TestClass]
    public sealed class DailyApiKeyTests
    {
        private static readonly Encoding Ascii = Encoding.ASCII;

        [TestMethod]
        public void CodecMatchesPublishedInteroperabilityVector()
        {
            ProductCode productCode = CreateProductCode("ABCD");
            var localNow = new DateTimeOffset(
                2026,
                7,
                17,
                12,
                0,
                0,
                TimeSpan.FromHours(9));
            byte[] initializationVector =
                CreateSequentialInitializationVector();

            string apiKey = DailyApiKeyCodec.Create(
                productCode,
                localNow,
                initializationVector);
            ProductCode authenticatedProductCode;

            Assert.AreEqual(
                "AAECAwQFBgcICQoLDA0OD37MVf5dYeif4Ss6OjGAC+g=",
                apiKey);
            Assert.IsTrue(
                DailyApiKeyCodec.TryValidate(
                    apiKey,
                    localNow,
                    out authenticatedProductCode));
            Assert.AreEqual("ABCD", authenticatedProductCode.Value);
        }

        [TestMethod]
        public void CreateProducesFortyFourCharacterTokenForCurrentLocalDate()
        {
            ProductCode productCode = CreateProductCode("AB12");
            DateTimeOffset beforeCreate = DateTimeOffset.Now;

            string apiKey = DailyApiKeyCodec.Create(productCode);

            DateTimeOffset afterCreate = DateTimeOffset.Now;
            Assert.AreEqual(DailyApiKeyCodec.EncodedLength, apiKey.Length);
            Assert.AreEqual(32, Convert.FromBase64String(apiKey).Length);
            Assert.IsTrue(
                TokenContainsExpectedPlainText(
                    apiKey,
                    productCode.Value,
                    beforeCreate)
                || TokenContainsExpectedPlainText(
                    apiKey,
                    productCode.Value,
                    afterCreate),
                "The token did not contain either local date observed around creation.");
        }

        [TestMethod]
        public void CreateAndValidateRoundTripReturnsNormalizedProductCode()
        {
            ProductCode productCode = CreateProductCode(" a1b2 ");
            ProductCode authenticatedProductCode;
            string apiKey = CreateCurrentlyValidApiKey(
                productCode,
                out authenticatedProductCode);

            Assert.AreEqual("A1B2", authenticatedProductCode.Value);

            ProductCode requestedProductCode;
            Assert.IsTrue(
                DailyApiKeyAuthenticator.MatchesRequestedProductCode(
                    authenticatedProductCode,
                    " a1b2 ",
                    out requestedProductCode));
            Assert.AreEqual("A1B2", requestedProductCode.Value);
            Assert.IsFalse(
                DailyApiKeyAuthenticator.MatchesRequestedProductCode(
                    authenticatedProductCode,
                    "ZZ99",
                    out requestedProductCode));
            Assert.AreEqual(DailyApiKeyCodec.EncodedLength, apiKey.Length);
        }

        [TestMethod]
        public void TryValidateRejectsTokenForPreviousLocalDate()
        {
            DateTimeOffset previousLocalDate = DateTimeOffset.Now.AddDays(-1);
            string apiKey = CreateSpecifiedDateToken(
                "AB12",
                previousLocalDate,
                CreateSequentialInitializationVector());

            ProductCode authenticatedProductCode;
            Assert.IsFalse(
                DailyApiKeyCodec.TryValidate(
                    apiKey,
                    out authenticatedProductCode));
            Assert.IsFalse(authenticatedProductCode.IsValid);
        }

        [TestMethod]
        public void AuthenticatorRequiresExactlyOneValidHeaderValue()
        {
            ProductCode productCode = CreateProductCode("AB12");
            ProductCode authenticatedProductCode;
            string apiKey = CreateCurrentlyValidApiKey(
                productCode,
                out authenticatedProductCode);

            Assert.IsTrue(
                DailyApiKeyAuthenticator.TryAuthenticate(
                    new[] { apiKey },
                    out authenticatedProductCode));
            Assert.AreEqual(productCode.Value, authenticatedProductCode.Value);

            Assert.IsFalse(
                DailyApiKeyAuthenticator.TryAuthenticate(
                    null,
                    out authenticatedProductCode));
            Assert.IsFalse(authenticatedProductCode.IsValid);
            Assert.IsFalse(
                DailyApiKeyAuthenticator.TryAuthenticate(
                    new string[0],
                    out authenticatedProductCode));
            Assert.IsFalse(authenticatedProductCode.IsValid);
            Assert.IsFalse(
                DailyApiKeyAuthenticator.TryAuthenticate(
                    new[] { apiKey, apiKey },
                    out authenticatedProductCode));
            Assert.IsFalse(authenticatedProductCode.IsValid);
        }

        [TestMethod]
        public void InvalidTokensReturnOnlyAuthenticationFailure()
        {
            string invalidProductToken = CreateSpecifiedDateToken(
                "AB-1",
                DateTimeOffset.Now,
                CreateSequentialInitializationVector());
            string[] invalidApiKeys =
            {
                null,
                string.Empty,
                new string('A', DailyApiKeyCodec.EncodedLength),
                " " + new string('A', DailyApiKeyCodec.EncodedLength - 1),
                Convert.ToBase64String(new byte[32]),
                invalidProductToken
            };

            foreach (string invalidApiKey in invalidApiKeys)
            {
                ProductCode authenticatedProductCode;

                Assert.IsFalse(
                    DailyApiKeyCodec.TryValidate(
                        invalidApiKey,
                        out authenticatedProductCode),
                    "An invalid key was accepted.");
                Assert.IsFalse(authenticatedProductCode.IsValid);
            }
        }

        private static ProductCode CreateProductCode(string rawValue)
        {
            ProductCode productCode;
            Assert.IsTrue(ProductCode.TryCreate(rawValue, out productCode));
            return productCode;
        }

        private static string CreateCurrentlyValidApiKey(
            ProductCode productCode,
            out ProductCode authenticatedProductCode)
        {
            string apiKey = null;
            authenticatedProductCode = default(ProductCode);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                apiKey = DailyApiKeyCodec.Create(productCode);
                if (DailyApiKeyCodec.TryValidate(
                    apiKey,
                    out authenticatedProductCode))
                {
                    return apiKey;
                }
            }

            Assert.Fail(
                "A newly-created daily API key could not be validated.");
            return apiKey;
        }

        private static bool TokenContainsExpectedPlainText(
            string apiKey,
            string productCode,
            DateTimeOffset localNow)
        {
            byte[] tokenBytes = Convert.FromBase64String(apiKey);
            var initializationVector = new byte[16];
            var cipherText = new byte[16];
            Buffer.BlockCopy(tokenBytes, 0, initializationVector, 0, 16);
            Buffer.BlockCopy(tokenBytes, 16, cipherText, 0, 16);

            string localDate = localNow.ToString(
                "yyyyMMdd",
                CultureInfo.InvariantCulture);
            byte[] key;
            using (SHA256 sha256 = SHA256.Create())
            {
                key = sha256.ComputeHash(Ascii.GetBytes(localDate));
            }

            byte[] plainText;
            using (Aes aes = CreateAes(key, initializationVector))
            using (ICryptoTransform decryptor = aes.CreateDecryptor())
            {
                try
                {
                    plainText = decryptor.TransformFinalBlock(
                        cipherText,
                        0,
                        cipherText.Length);
                }
                catch (CryptographicException)
                {
                    return false;
                }
            }

            string expectedPlainText = productCode + localDate;
            return StringComparer.Ordinal.Equals(
                expectedPlainText,
                Ascii.GetString(plainText));
        }

        private static string CreateSpecifiedDateToken(
            string rawProductCode,
            DateTimeOffset localNow,
            byte[] initializationVector)
        {
            string localDate = localNow.ToString(
                "yyyyMMdd",
                CultureInfo.InvariantCulture);
            byte[] key;
            using (SHA256 sha256 = SHA256.Create())
            {
                key = sha256.ComputeHash(Ascii.GetBytes(localDate));
            }

            byte[] plainText = Ascii.GetBytes(rawProductCode + localDate);
            byte[] cipherText;
            using (Aes aes = CreateAes(key, initializationVector))
            using (ICryptoTransform encryptor = aes.CreateEncryptor())
            {
                cipherText = encryptor.TransformFinalBlock(
                    plainText,
                    0,
                    plainText.Length);
            }

            var tokenBytes = new byte[32];
            Buffer.BlockCopy(initializationVector, 0, tokenBytes, 0, 16);
            Buffer.BlockCopy(cipherText, 0, tokenBytes, 16, 16);
            return Convert.ToBase64String(tokenBytes);
        }

        private static Aes CreateAes(
            byte[] key,
            byte[] initializationVector)
        {
            Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = initializationVector;
            return aes;
        }

        private static byte[] CreateSequentialInitializationVector()
        {
            var initializationVector = new byte[16];
            for (int index = 0; index < initializationVector.Length; index++)
            {
                initializationVector[index] = (byte)index;
            }

            return initializationVector;
        }
    }
}
