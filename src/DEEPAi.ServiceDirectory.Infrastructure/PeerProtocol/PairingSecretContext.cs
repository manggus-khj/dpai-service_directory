using System;
using System.Security.Cryptography;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    // Owns the ephemeral K0 and transcript binding only for the active pairing
    // negotiation. Dispose on success, cancellation, timeout, or any failure.
    internal sealed class PairingSecretContext : IDisposable
    {
        private readonly object _lifecycleGate = new object();
        private readonly byte[] _k0;
        private readonly byte[] _transcriptHash;
        private bool _disposed;

        internal PairingSecretContext(byte[] k0, byte[] transcriptHash)
        {
            ValidateExactLength(
                k0,
                nameof(k0),
                PairingCryptography.KeyMaterialLength);
            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                PairingCryptography.TranscriptHashLength);

            _k0 = (byte[])k0.Clone();
            _transcriptHash = (byte[])transcriptHash.Clone();
        }

        internal static PairingSecretContext CreateFromKeyAgreement(
            ECDiffieHellmanCng localKeyAgreement,
            byte[] peerPublicKeyBlob,
            byte[] transcriptHash)
        {
            byte[] k0 = null;
            try
            {
                k0 = PairingCryptography.DeriveK0(
                    localKeyAgreement,
                    peerPublicKeyBlob,
                    transcriptHash);
                return new PairingSecretContext(k0, transcriptHash);
            }
            finally
            {
                Clear(k0);
            }
        }

        internal byte[] CreateConfirmationMac(
            PairingConfirmationDirection direction)
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return PairingCryptography.CreateConfirmationMac(
                    _k0,
                    _transcriptHash,
                    direction);
            }
        }

        internal char[] CreateSas()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return PairingCryptography.CreateSas(
                    _k0,
                    _transcriptHash);
            }
        }

        internal byte[] DerivePairRoot()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return PairingCryptography.DerivePairRoot(
                    _k0,
                    _transcriptHash);
            }
        }

        internal bool IsBoundToTranscriptHash(byte[] transcriptHash)
        {
            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                PairingCryptography.TranscriptHashLength);

            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return PairingCryptography.FixedTimeEquals32(
                    _transcriptHash,
                    transcriptHash);
            }
        }

        internal byte[] CreateDecisionRequestMac(
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            PairingTerminalDecision decision)
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return PairingTerminalMessageAuthenticator
                    .CreateDecisionRequestMac(
                        _k0,
                        _transcriptHash,
                        pairingId,
                        keyEpoch,
                        senderRole,
                        senderInstanceId,
                        receiverInstanceId,
                        decision);
            }
        }

        internal byte[] CreateDecisionResponseMac(
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] requestMac,
            int httpStatus,
            string result,
            uint code,
            byte[] rawResponseBody)
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return PairingTerminalMessageAuthenticator
                    .CreateDecisionResponseMac(
                        _k0,
                        _transcriptHash,
                        pairingId,
                        keyEpoch,
                        senderRole,
                        senderInstanceId,
                        receiverInstanceId,
                        requestMac,
                        httpStatus,
                        result,
                        code,
                        rawResponseBody);
            }
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                Clear(_k0);
                Clear(_transcriptHash);
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PairingSecretContext));
            }
        }

        private static void ValidateExactLength(
            byte[] value,
            string parameterName,
            int expectedLength)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length != expectedLength)
            {
                throw new ArgumentException(
                    "The pairing secret context received an invalid buffer length.",
                    parameterName);
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
