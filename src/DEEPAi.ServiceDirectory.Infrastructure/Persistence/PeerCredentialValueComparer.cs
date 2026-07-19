using System;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal static class PeerCredentialValueComparer
    {
        internal static bool Equals(
            PairedPeerCredential left,
            PairedPeerCredential right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            var codec = new PeerCredentialBinaryCodec();
            byte[] leftBytes = null;
            byte[] rightBytes = null;
            try
            {
                leftBytes = codec.Serialize(left);
                rightBytes = codec.Serialize(right);
                int difference = leftBytes.Length ^ rightBytes.Length;
                int maximum = Math.Max(
                    leftBytes.Length,
                    rightBytes.Length);
                for (int index = 0; index < maximum; index++)
                {
                    byte leftByte = index < leftBytes.Length
                        ? leftBytes[index]
                        : (byte)0;
                    byte rightByte = index < rightBytes.Length
                        ? rightBytes[index]
                        : (byte)0;
                    difference |= leftByte ^ rightByte;
                }

                return difference == 0;
            }
            finally
            {
                Clear(leftBytes);
                Clear(rightBytes);
            }
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
