using System;
using System.Collections.Generic;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal static class PairingMacHeaderCodec
    {
        internal const string HeaderName = "X-DPAI-Pairing-MAC";
        internal const int EncodedLength = 44;

        internal static bool TryParseExactlyOne(
            IEnumerable<string> headerValues,
            out byte[] authenticationCode)
        {
            authenticationCode = null;
            if (headerValues == null)
            {
                return false;
            }

            string onlyValue = null;
            int valueCount = 0;
            foreach (string value in headerValues)
            {
                valueCount++;
                if (valueCount > 1)
                {
                    return false;
                }

                onlyValue = value;
            }

            return valueCount == 1
                && onlyValue != null
                && onlyValue.Length == EncodedLength
                && PeerAuthenticationContract.TryParseCanonicalSignature(
                    onlyValue,
                    out authenticationCode);
        }

        internal static string Format(byte[] authenticationCode)
        {
            PeerAuthenticationContract.ValidateExactLength(
                authenticationCode,
                nameof(authenticationCode),
                PeerAuthenticationContract.SignatureLength);
            string value = Convert.ToBase64String(authenticationCode);
            if (value.Length != EncodedLength)
            {
                throw new InvalidOperationException(
                    "The pairing authentication code did not encode to 44 Base64 characters.");
            }

            return value;
        }
    }
}
