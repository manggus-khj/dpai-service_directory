using System;
using System.Security.Cryptography;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal enum AdminCursorKind : byte
    {
        Services = 1,
        Pending = 2,
        Certificates = 3
    }

    internal sealed class AdminCursorCodec : IDisposable
    {
        private const byte FormatVersion = 1;
        private const int FingerprintLength = 32;
        private const int MacLength = 32;
        private const int PayloadLength = 40;
        private const int TokenLength = PayloadLength + MacLength;
        private const int EncodedLength = 96;

        private readonly object _gate = new object();
        private readonly byte[] _key;
        private bool _disposed;

        internal AdminCursorCodec()
        {
            _key = CreateRandomKey();
        }

        internal AdminCursorCodec(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (key.Length != MacLength)
            {
                throw new ArgumentException(
                    "The Admin cursor key must contain exactly 32 bytes.",
                    nameof(key));
            }

            _key = (byte[])key.Clone();
        }

        internal string Create(
            AdminCursorKind kind,
            bool includeDeleted,
            int offset,
            byte[] fingerprint)
        {
            ValidateArguments(kind, offset, fingerprint);
            lock (_gate)
            {
                ThrowIfDisposed();
                var token = new byte[TokenLength];
                try
                {
                    WritePayload(
                        token,
                        kind,
                        includeDeleted,
                        offset,
                        fingerprint);
                    byte[] mac = ComputeMac(token, PayloadLength);
                    try
                    {
                        Buffer.BlockCopy(
                            mac,
                            0,
                            token,
                            PayloadLength,
                            MacLength);
                        return Convert.ToBase64String(token);
                    }
                    finally
                    {
                        Array.Clear(mac, 0, mac.Length);
                    }
                }
                finally
                {
                    Array.Clear(token, 0, token.Length);
                }
            }
        }

        internal bool TryRead(
            string cursor,
            AdminCursorKind expectedKind,
            bool expectedIncludeDeleted,
            byte[] expectedFingerprint,
            out int offset)
        {
            offset = 0;
            if (string.IsNullOrEmpty(cursor)
                || cursor.Length != EncodedLength
                || !IsDefinedKind(expectedKind)
                || expectedFingerprint == null
                || expectedFingerprint.Length != FingerprintLength)
            {
                return false;
            }

            byte[] token;
            try
            {
                token = Convert.FromBase64String(cursor);
            }
            catch (FormatException)
            {
                return false;
            }

            try
            {
                if (token.Length != TokenLength
                    || !StringComparer.Ordinal.Equals(
                        cursor,
                        Convert.ToBase64String(token)))
                {
                    return false;
                }

                lock (_gate)
                {
                    ThrowIfDisposed();
                    byte[] expectedMac = ComputeMac(token, PayloadLength);
                    try
                    {
                        if (!FixedTimeEquals(
                                expectedMac,
                                0,
                                token,
                                PayloadLength,
                                MacLength))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        Array.Clear(expectedMac, 0, expectedMac.Length);
                    }
                }

                if (token[0] != FormatVersion
                    || token[1] != (byte)expectedKind
                    || token[2] != (expectedIncludeDeleted ? (byte)1 : (byte)0)
                    || token[3] != 0
                    || !FixedTimeEquals(
                        token,
                        8,
                        expectedFingerprint,
                        0,
                        FingerprintLength))
                {
                    return false;
                }

                int decodedOffset = ReadInt32(token, 4);
                if (decodedOffset <= 0)
                {
                    return false;
                }

                offset = decodedOffset;
                return true;
            }
            finally
            {
                Array.Clear(token, 0, token.Length);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                Array.Clear(_key, 0, _key.Length);
                _disposed = true;
            }
        }

        private static void ValidateArguments(
            AdminCursorKind kind,
            int offset,
            byte[] fingerprint)
        {
            if (!IsDefinedKind(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            if (offset <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (fingerprint == null)
            {
                throw new ArgumentNullException(nameof(fingerprint));
            }

            if (fingerprint.Length != FingerprintLength)
            {
                throw new ArgumentException(
                    "The Admin cursor fingerprint must contain 32 bytes.",
                    nameof(fingerprint));
            }
        }

        private static void WritePayload(
            byte[] destination,
            AdminCursorKind kind,
            bool includeDeleted,
            int offset,
            byte[] fingerprint)
        {
            destination[0] = FormatVersion;
            destination[1] = (byte)kind;
            destination[2] = includeDeleted ? (byte)1 : (byte)0;
            destination[3] = 0;
            WriteInt32(destination, 4, offset);
            Buffer.BlockCopy(
                fingerprint,
                0,
                destination,
                8,
                FingerprintLength);
        }

        private byte[] ComputeMac(byte[] value, int count)
        {
            using (var hmac = new HMACSHA256(_key))
            {
                return hmac.ComputeHash(value, 0, count);
            }
        }

        private static byte[] CreateRandomKey()
        {
            var key = new byte[MacLength];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(key);
            }

            return key;
        }

        private static bool IsDefinedKind(AdminCursorKind kind)
        {
            return kind == AdminCursorKind.Services
                || kind == AdminCursorKind.Pending
                || kind == AdminCursorKind.Certificates;
        }

        private static void WriteInt32(
            byte[] destination,
            int offset,
            int value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        private static int ReadInt32(byte[] source, int offset)
        {
            return (source[offset] << 24)
                | (source[offset + 1] << 16)
                | (source[offset + 2] << 8)
                | source[offset + 3];
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

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AdminCursorCodec));
            }
        }
    }
}
