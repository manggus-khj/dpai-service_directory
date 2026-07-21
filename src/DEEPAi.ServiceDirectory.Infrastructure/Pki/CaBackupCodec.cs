using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class CaBackupPayload : IDisposable
    {
        private bool _disposed;

        internal CaBackupPayload(
            byte[] metadata,
            byte[] ledger,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] privateKeyPkcs8)
            : this(
                metadata,
                ledger,
                caCertificateDer,
                crlDer,
                privateKeyPkcs8,
                null,
                null,
                null)
        {
        }

        internal CaBackupPayload(
            byte[] metadata,
            byte[] ledger,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] privateKeyPkcs8,
            byte[] otherCaCertificateDer,
            byte[] otherCrlDer,
            byte[] otherPrivateKeyPkcs8)
        {
            Metadata = CloneRequired(metadata, nameof(metadata));
            Ledger = CloneRequired(ledger, nameof(ledger));
            CaCertificateDer = CloneRequired(
                caCertificateDer,
                nameof(caCertificateDer));
            CrlDer = CloneRequired(crlDer, nameof(crlDer));
            PrivateKeyPkcs8 = CloneRequired(
                privateKeyPkcs8,
                nameof(privateKeyPkcs8));
            bool hasOther = otherCaCertificateDer != null
                || otherCrlDer != null
                || otherPrivateKeyPkcs8 != null;
            if (hasOther
                && (otherCaCertificateDer == null
                    || otherCrlDer == null
                    || otherPrivateKeyPkcs8 == null))
            {
                Dispose();
                throw new ArgumentException(
                    "Secondary CA backup components must be supplied together.");
            }

            OtherCaCertificateDer = CloneOptional(otherCaCertificateDer);
            OtherCrlDer = CloneOptional(otherCrlDer);
            OtherPrivateKeyPkcs8 = CloneOptional(otherPrivateKeyPkcs8);
        }

        internal byte[] Metadata { get; private set; }

        internal byte[] Ledger { get; private set; }

        internal byte[] CaCertificateDer { get; private set; }

        internal byte[] CrlDer { get; private set; }

        internal byte[] PrivateKeyPkcs8 { get; private set; }

        internal byte[] OtherCaCertificateDer { get; private set; }

        internal byte[] OtherCrlDer { get; private set; }

        internal byte[] OtherPrivateKeyPkcs8 { get; private set; }

        internal bool HasOtherAuthority => OtherCaCertificateDer != null;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Clear(Metadata);
            Clear(Ledger);
            Clear(CaCertificateDer);
            Clear(CrlDer);
            Clear(PrivateKeyPkcs8);
            Clear(OtherCaCertificateDer);
            Clear(OtherCrlDer);
            Clear(OtherPrivateKeyPkcs8);
            Metadata = null;
            Ledger = null;
            CaCertificateDer = null;
            CrlDer = null;
            PrivateKeyPkcs8 = null;
            OtherCaCertificateDer = null;
            OtherCrlDer = null;
            OtherPrivateKeyPkcs8 = null;
            _disposed = true;
        }

        private static byte[] CloneRequired(byte[] value, string name)
        {
            if (value == null || value.Length == 0)
            {
                throw new ArgumentException(
                    "CA backup components must not be empty.",
                    name);
            }

            return (byte[])value.Clone();
        }

        private static byte[] CloneOptional(byte[] value)
        {
            return value == null ? null : CloneRequired(value, nameof(value));
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }

    internal sealed class CaBackupCodec
    {
        internal const int Pbkdf2Iterations = 600000;
        internal const int MinimumPasswordScalars = 12;
        internal const int MaximumPasswordScalars = 128;
        internal const int MaximumPasswordUtf8Bytes = 512;
        internal const int MaximumBackupBytes = 32 * 1024 * 1024;

        private const int SaltLength = 16;
        private const int IvLength = 16;
        private const int EncryptionKeyLength = 32;
        private const int AuthenticationKeyLength = 32;
        private const int MacLength = 32;
        private const int OuterHeaderLength = 8 + 4 + SaltLength + IvLength + 4;
        private const int InnerHeaderLength = 8 + (8 * 4);

        private static readonly byte[] OuterMagic =
            Encoding.ASCII.GetBytes("DPAICAE2");
        private static readonly byte[] InnerMagic =
            Encoding.ASCII.GetBytes("DPAICAB2");
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal byte[] Encrypt(CaBackupPayload payload, string password)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            ValidatePassword(password);
            byte[] plaintext = null;
            byte[] salt = new byte[SaltLength];
            byte[] iv = new byte[IvLength];
            byte[] keyMaterial = null;
            byte[] ciphertext = null;
            try
            {
                plaintext = SerializePayload(payload);
                using (RandomNumberGenerator random =
                    RandomNumberGenerator.Create())
                {
                    random.GetBytes(salt);
                    random.GetBytes(iv);
                }

                keyMaterial = DeriveKeyMaterial(password, salt);
                ciphertext = EncryptAesCbc(
                    plaintext,
                    keyMaterial,
                    iv);
                int totalLength = checked(
                    OuterHeaderLength + ciphertext.Length + MacLength);
                if (totalLength > MaximumBackupBytes)
                {
                    throw new InvalidDataException(
                        "Encrypted CA backup exceeds its size limit.");
                }

                var output = new byte[totalLength];
                int offset = 0;
                WriteBytes(output, ref offset, OuterMagic);
                WriteInt32(output, ref offset, Pbkdf2Iterations);
                WriteBytes(output, ref offset, salt);
                WriteBytes(output, ref offset, iv);
                WriteInt32(output, ref offset, ciphertext.Length);
                WriteBytes(output, ref offset, ciphertext);
                byte[] mac = ComputeHmac(
                    keyMaterial,
                    EncryptionKeyLength,
                    output,
                    0,
                    offset);
                try
                {
                    WriteBytes(output, ref offset, mac);
                }
                finally
                {
                    Clear(mac);
                }

                if (offset != output.Length)
                {
                    Clear(output);
                    throw new InvalidOperationException(
                        "CA backup output length is inconsistent.");
                }

                return output;
            }
            finally
            {
                Clear(plaintext);
                Clear(salt);
                Clear(iv);
                Clear(keyMaterial);
                Clear(ciphertext);
            }
        }

        internal CaBackupPayload Decrypt(byte[] backup, string password)
        {
            if (backup == null
                || backup.Length < OuterHeaderLength + 16 + MacLength
                || backup.Length > MaximumBackupBytes)
            {
                throw new InvalidDataException(
                    "Encrypted CA backup size is invalid.");
            }

            ValidatePassword(password);
            int offset = 0;
            RequireBytes(backup, ref offset, OuterMagic);
            int iterations = ReadInt32(backup, ref offset);
            if (iterations != Pbkdf2Iterations)
            {
                throw new InvalidDataException(
                    "Encrypted CA backup KDF parameters are unsupported.");
            }

            byte[] salt = ReadBytes(backup, ref offset, SaltLength);
            byte[] iv = ReadBytes(backup, ref offset, IvLength);
            int ciphertextLength = ReadInt32(backup, ref offset);
            if (ciphertextLength < 16
                || (ciphertextLength & 15) != 0
                || ciphertextLength != backup.Length - offset - MacLength)
            {
                Clear(salt);
                Clear(iv);
                throw new InvalidDataException(
                    "Encrypted CA backup ciphertext length is invalid.");
            }

            byte[] keyMaterial = null;
            byte[] ciphertext = null;
            byte[] plaintext = null;
            try
            {
                keyMaterial = DeriveKeyMaterial(password, salt);
                byte[] expectedMac = ComputeHmac(
                    keyMaterial,
                    EncryptionKeyLength,
                    backup,
                    0,
                    backup.Length - MacLength);
                try
                {
                    if (!FixedTimeEquals(
                            expectedMac,
                            0,
                            backup,
                            backup.Length - MacLength,
                            MacLength))
                    {
                        throw new CryptographicException(
                            "Encrypted CA backup authentication failed.");
                    }
                }
                finally
                {
                    Clear(expectedMac);
                }

                ciphertext = ReadBytes(
                    backup,
                    ref offset,
                    ciphertextLength);
                offset += MacLength;
                if (offset != backup.Length)
                {
                    throw new InvalidDataException(
                        "Encrypted CA backup contains trailing bytes.");
                }

                plaintext = DecryptAesCbc(
                    ciphertext,
                    keyMaterial,
                    iv);
                return DeserializePayload(plaintext);
            }
            finally
            {
                Clear(salt);
                Clear(iv);
                Clear(keyMaterial);
                Clear(ciphertext);
                Clear(plaintext);
            }
        }

        internal static void ValidatePassword(string password)
        {
            if (password == null)
            {
                throw new ArgumentNullException(nameof(password));
            }

            int scalarCount = CountUnicodeScalars(password);
            int utf8Bytes;
            try
            {
                utf8Bytes = StrictUtf8.GetByteCount(password);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException(
                    "CA backup password contains invalid Unicode.",
                    nameof(password),
                    exception);
            }

            if (scalarCount < MinimumPasswordScalars
                || scalarCount > MaximumPasswordScalars
                || utf8Bytes > MaximumPasswordUtf8Bytes
                || ContainsControlCharacter(password))
            {
                throw new ArgumentException(
                    "CA backup password does not satisfy the required bounds.",
                    nameof(password));
            }
        }

        private static byte[] SerializePayload(CaBackupPayload payload)
        {
            byte[][] parts =
            {
                payload.Metadata,
                payload.Ledger,
                payload.CaCertificateDer,
                payload.CrlDer,
                payload.PrivateKeyPkcs8,
                payload.OtherCaCertificateDer,
                payload.OtherCrlDer,
                payload.OtherPrivateKeyPkcs8
            };
            int totalLength = InnerHeaderLength;
            for (int index = 0; index < parts.Length; index++)
            {
                byte[] part = parts[index];
                if ((index < 5 && (part == null || part.Length == 0))
                    || (index >= 5 && part != null && part.Length == 0))
                {
                    throw new InvalidDataException(
                        "CA backup payload contains an empty component.");
                }

                totalLength = checked(totalLength + (part == null
                    ? 0
                    : part.Length));
            }

            if (totalLength > MaximumBackupBytes)
            {
                throw new InvalidDataException(
                    "CA backup payload exceeds its size limit.");
            }

            var output = new byte[totalLength];
            int offset = 0;
            WriteBytes(output, ref offset, InnerMagic);
            for (int index = 0; index < parts.Length; index++)
            {
                byte[] part = parts[index];
                WriteInt32(
                    output,
                    ref offset,
                    part == null ? 0 : part.Length);
            }

            foreach (byte[] part in parts)
            {
                if (part != null)
                {
                    WriteBytes(output, ref offset, part);
                }
            }

            return output;
        }

        private static CaBackupPayload DeserializePayload(byte[] plaintext)
        {
            int offset = 0;
            RequireBytes(plaintext, ref offset, InnerMagic);
            var lengths = new int[8];
            int expectedLength = InnerHeaderLength;
            for (int index = 0; index < lengths.Length; index++)
            {
                lengths[index] = ReadInt32(plaintext, ref offset);
                if ((index < 5 && lengths[index] <= 0)
                    || (index >= 5 && lengths[index] < 0))
                {
                    throw new InvalidDataException(
                        "CA backup payload component length is invalid.");
                }

                expectedLength = checked(expectedLength + lengths[index]);
            }

            if (expectedLength != plaintext.Length)
            {
                throw new InvalidDataException(
                    "CA backup payload length is invalid.");
            }


            bool hasOther = lengths[5] != 0
                || lengths[6] != 0
                || lengths[7] != 0;
            if (hasOther
                && (lengths[5] == 0
                    || lengths[6] == 0
                    || lengths[7] == 0))
            {
                throw new InvalidDataException(
                    "Secondary CA backup components are incomplete.");
            }

            byte[] metadata = null;
            byte[] ledger = null;
            byte[] certificate = null;
            byte[] crl = null;
            byte[] key = null;
            byte[] otherCertificate = null;
            byte[] otherCrl = null;
            byte[] otherKey = null;
            try
            {
                metadata = ReadBytes(plaintext, ref offset, lengths[0]);
                ledger = ReadBytes(plaintext, ref offset, lengths[1]);
                certificate = ReadBytes(plaintext, ref offset, lengths[2]);
                crl = ReadBytes(plaintext, ref offset, lengths[3]);
                key = ReadBytes(plaintext, ref offset, lengths[4]);
                if (hasOther)
                {
                    otherCertificate = ReadBytes(
                        plaintext,
                        ref offset,
                        lengths[5]);
                    otherCrl = ReadBytes(
                        plaintext,
                        ref offset,
                        lengths[6]);
                    otherKey = ReadBytes(
                        plaintext,
                        ref offset,
                        lengths[7]);
                }
                return new CaBackupPayload(
                    metadata,
                    ledger,
                    certificate,
                    crl,
                    key,
                    otherCertificate,
                    otherCrl,
                    otherKey);
            }
            finally
            {
                Clear(metadata);
                Clear(ledger);
                Clear(certificate);
                Clear(crl);
                Clear(key);
                Clear(otherCertificate);
                Clear(otherCrl);
                Clear(otherKey);
            }
        }

        private static byte[] DeriveKeyMaterial(
            string password,
            byte[] salt)
        {
            using (var derive = new Rfc2898DeriveBytes(
                password,
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256))
            {
                return derive.GetBytes(
                    EncryptionKeyLength + AuthenticationKeyLength);
            }
        }

        private static byte[] EncryptAesCbc(
            byte[] plaintext,
            byte[] keyMaterial,
            byte[] iv)
        {
            byte[] key = CopySegment(keyMaterial, 0, EncryptionKeyLength);
            byte[] initializationVector = (byte[])iv.Clone();
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.BlockSize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = key;
                    aes.IV = initializationVector;
                    using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    {
                        return encryptor.TransformFinalBlock(
                            plaintext,
                            0,
                            plaintext.Length);
                    }
                }
            }
            finally
            {
                Clear(key);
                Clear(initializationVector);
            }
        }

        private static byte[] DecryptAesCbc(
            byte[] ciphertext,
            byte[] keyMaterial,
            byte[] iv)
        {
            byte[] key = CopySegment(keyMaterial, 0, EncryptionKeyLength);
            byte[] initializationVector = (byte[])iv.Clone();
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.BlockSize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = key;
                    aes.IV = initializationVector;
                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    {
                        return decryptor.TransformFinalBlock(
                            ciphertext,
                            0,
                            ciphertext.Length);
                    }
                }
            }
            finally
            {
                Clear(key);
                Clear(initializationVector);
            }
        }

        private static byte[] ComputeHmac(
            byte[] keyMaterial,
            int keyOffset,
            byte[] value,
            int offset,
            int count)
        {
            byte[] key = CopySegment(
                keyMaterial,
                keyOffset,
                AuthenticationKeyLength);
            try
            {
                using (var hmac = new HMACSHA256(key))
                {
                    return hmac.ComputeHash(value, offset, count);
                }
            }
            finally
            {
                Clear(key);
            }
        }

        private static int CountUnicodeScalars(string value)
        {
            int count = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= value.Length
                        || !char.IsLowSurrogate(value[index + 1]))
                    {
                        return int.MaxValue;
                    }

                    index++;
                }
                else if (char.IsLowSurrogate(current))
                {
                    return int.MaxValue;
                }

                count++;
            }

            return count;
        }

        private static bool ContainsControlCharacter(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RequireBytes(
            byte[] source,
            ref int offset,
            byte[] expected)
        {
            if (!FixedTimeEquals(
                    source,
                    offset,
                    expected,
                    0,
                    expected.Length))
            {
                throw new InvalidDataException(
                    "CA backup format marker is invalid.");
            }

            offset += expected.Length;
        }

        private static void WriteInt32(
            byte[] destination,
            ref int offset,
            int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            destination[offset++] = (byte)(value >> 24);
            destination[offset++] = (byte)(value >> 16);
            destination[offset++] = (byte)(value >> 8);
            destination[offset++] = (byte)value;
        }

        private static int ReadInt32(byte[] source, ref int offset)
        {
            if (offset < 0 || source.Length - offset < 4)
            {
                throw new InvalidDataException(
                    "CA backup ended before an integer field.");
            }

            int value = (source[offset] << 24)
                | (source[offset + 1] << 16)
                | (source[offset + 2] << 8)
                | source[offset + 3];
            offset += 4;
            if (value < 0)
            {
                throw new InvalidDataException(
                    "CA backup contains a negative length.");
            }

            return value;
        }

        private static void WriteBytes(
            byte[] destination,
            ref int offset,
            byte[] value)
        {
            Buffer.BlockCopy(value, 0, destination, offset, value.Length);
            offset += value.Length;
        }

        private static byte[] ReadBytes(
            byte[] source,
            ref int offset,
            int count)
        {
            if (count < 0
                || offset < 0
                || source.Length - offset < count)
            {
                throw new InvalidDataException(
                    "CA backup component exceeds the input boundary.");
            }

            var value = new byte[count];
            Buffer.BlockCopy(source, offset, value, 0, count);
            offset += count;
            return value;
        }

        private static byte[] CopySegment(
            byte[] source,
            int offset,
            int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static bool FixedTimeEquals(
            byte[] left,
            int leftOffset,
            byte[] right,
            int rightOffset,
            int count)
        {
            if (left == null
                || right == null
                || leftOffset < 0
                || rightOffset < 0
                || count < 0
                || left.Length - leftOffset < count
                || right.Length - rightOffset < count)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < count; index++)
            {
                difference |= left[leftOffset + index]
                    ^ right[rightOffset + index];
            }

            return difference == 0;
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
