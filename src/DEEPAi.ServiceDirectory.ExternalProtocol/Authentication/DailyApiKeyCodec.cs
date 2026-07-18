using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.Authentication
{
    public static class DailyApiKeyCodec
    {
        public const int EncodedLength = 44;

        private const int InitializationVectorLength = 16;
        private const int CipherTextLength = 16;
        private const int TokenByteLength = InitializationVectorLength + CipherTextLength;
        private const int PlainTextLength = 12;
        private static readonly Encoding StrictAscii = Encoding.GetEncoding(
            "us-ascii",
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

        public static string Create(ProductCode productCode)
        {
            return Create(productCode, DateTimeOffset.Now, null);
        }

        public static bool TryValidate(string apiKey, out ProductCode productCode)
        {
            return TryValidate(apiKey, DateTimeOffset.Now, out productCode);
        }

        internal static string Create(
            ProductCode productCode,
            DateTimeOffset localNow,
            byte[] fixedInitializationVector)
        {
            if (!productCode.IsValid)
            {
                throw new ArgumentException("Product code must be valid.", nameof(productCode));
            }

            if (fixedInitializationVector != null
                && fixedInitializationVector.Length != InitializationVectorLength)
            {
                throw new ArgumentException(
                    "Initialization vector must contain exactly 16 bytes.",
                    nameof(fixedInitializationVector));
            }

            byte[] dateBytes = null;
            byte[] key = null;
            byte[] plainText = null;
            byte[] initializationVector = null;
            byte[] cipherText = null;
            byte[] tokenBytes = null;
            try
            {
                dateBytes = GetLocalDateBytes(localNow);
                key = ComputeDateKey(dateBytes);
                plainText = StrictAscii.GetBytes(productCode.Value + StrictAscii.GetString(dateBytes));

                initializationVector = fixedInitializationVector == null
                    ? CreateRandomInitializationVector()
                    : (byte[])fixedInitializationVector.Clone();
                cipherText = Encrypt(plainText, key, initializationVector);
                if (cipherText.Length != CipherTextLength)
                {
                    throw new CryptographicException("The daily API key ciphertext has an unexpected length.");
                }

                tokenBytes = new byte[TokenByteLength];
                Buffer.BlockCopy(
                    initializationVector,
                    0,
                    tokenBytes,
                    0,
                    InitializationVectorLength);
                Buffer.BlockCopy(
                    cipherText,
                    0,
                    tokenBytes,
                    InitializationVectorLength,
                    CipherTextLength);
                return Convert.ToBase64String(tokenBytes);
            }
            finally
            {
                Clear(dateBytes);
                Clear(key);
                Clear(plainText);
                Clear(initializationVector);
                Clear(cipherText);
                Clear(tokenBytes);
            }
        }

        internal static bool TryValidate(
            string apiKey,
            DateTimeOffset localNow,
            out ProductCode productCode)
        {
            productCode = default(ProductCode);
            if (!IsStrictBase64Token(apiKey))
            {
                return false;
            }

            byte[] tokenBytes;
            try
            {
                tokenBytes = Convert.FromBase64String(apiKey);
            }
            catch (FormatException)
            {
                return false;
            }

            if (tokenBytes.Length != TokenByteLength
                || !StringComparer.Ordinal.Equals(Convert.ToBase64String(tokenBytes), apiKey))
            {
                Clear(tokenBytes);
                return false;
            }

            var initializationVector = new byte[InitializationVectorLength];
            var cipherText = new byte[CipherTextLength];
            Buffer.BlockCopy(tokenBytes, 0, initializationVector, 0, InitializationVectorLength);
            Buffer.BlockCopy(
                tokenBytes,
                InitializationVectorLength,
                cipherText,
                0,
                CipherTextLength);

            byte[] dateBytes = null;
            byte[] key = null;
            byte[] plainText = null;
            try
            {
                dateBytes = GetLocalDateBytes(localNow);
                key = ComputeDateKey(dateBytes);
                if (!TryDecrypt(cipherText, key, initializationVector, out plainText))
                {
                    return false;
                }

                if (plainText.Length != PlainTextLength)
                {
                    return false;
                }

                int difference = 0;
                for (int index = 0; index < dateBytes.Length; index++)
                {
                    difference |= plainText[index + 4] ^ dateBytes[index];
                }

                if (difference != 0)
                {
                    return false;
                }

                for (int index = 0; index < 4; index++)
                {
                    byte current = plainText[index];
                    bool isUpperAsciiLetter = current >= (byte)'A' && current <= (byte)'Z';
                    bool isAsciiDigit = current >= (byte)'0' && current <= (byte)'9';
                    if (!isUpperAsciiLetter && !isAsciiDigit)
                    {
                        return false;
                    }
                }

                string rawProductCode = StrictAscii.GetString(plainText, 0, 4);
                return ProductCode.TryCreate(rawProductCode, out productCode);
            }
            finally
            {
                Clear(tokenBytes);
                Clear(dateBytes);
                Clear(key);
                Clear(plainText);
                Clear(initializationVector);
                Clear(cipherText);
            }
        }

        private static byte[] GetLocalDateBytes(DateTimeOffset localNow)
        {
            string localDate = localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            return StrictAscii.GetBytes(localDate);
        }

        private static byte[] ComputeDateKey(byte[] dateBytes)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(dateBytes);
            }
        }

        private static byte[] CreateRandomInitializationVector()
        {
            var initializationVector = new byte[InitializationVectorLength];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(initializationVector);
            }

            return initializationVector;
        }

        private static byte[] Encrypt(byte[] plainText, byte[] key, byte[] initializationVector)
        {
            using (Aes aes = CreateAes(key, initializationVector))
            using (ICryptoTransform encryptor = aes.CreateEncryptor())
            {
                return encryptor.TransformFinalBlock(plainText, 0, plainText.Length);
            }
        }

        private static bool TryDecrypt(
            byte[] cipherText,
            byte[] key,
            byte[] initializationVector,
            out byte[] plainText)
        {
            plainText = null;
            using (Aes aes = CreateAes(key, initializationVector))
            using (ICryptoTransform decryptor = aes.CreateDecryptor())
            {
                try
                {
                    plainText = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
                    return true;
                }
                catch (CryptographicException)
                {
                    return false;
                }
            }
        }

        private static Aes CreateAes(byte[] key, byte[] initializationVector)
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

        private static bool IsStrictBase64Token(string value)
        {
            if (value == null || value.Length != EncodedLength || value[EncodedLength - 1] != '=')
            {
                return false;
            }

            for (int index = 0; index < EncodedLength - 1; index++)
            {
                char current = value[index];
                bool isUpperAsciiLetter = current >= 'A' && current <= 'Z';
                bool isLowerAsciiLetter = current >= 'a' && current <= 'z';
                bool isAsciiDigit = current >= '0' && current <= '9';
                if (!isUpperAsciiLetter
                    && !isLowerAsciiLetter
                    && !isAsciiDigit
                    && current != '+'
                    && current != '/')
                {
                    return false;
                }
            }

            return true;
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
