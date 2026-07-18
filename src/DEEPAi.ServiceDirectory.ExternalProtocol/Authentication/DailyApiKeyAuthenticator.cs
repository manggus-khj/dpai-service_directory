using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.Authentication
{
    public static class DailyApiKeyAuthenticator
    {
        public static bool TryAuthenticate(
            IEnumerable<string> headerValues,
            out ProductCode authenticatedProductCode)
        {
            authenticatedProductCode = default(ProductCode);
            if (headerValues == null)
            {
                return false;
            }

            IEnumerator<string> enumerator = headerValues.GetEnumerator();
            if (enumerator == null)
            {
                return false;
            }

            using (enumerator)
            {
                if (!enumerator.MoveNext())
                {
                    return false;
                }

                string onlyValue = enumerator.Current;
                if (enumerator.MoveNext())
                {
                    return false;
                }

                return DailyApiKeyCodec.TryValidate(
                    onlyValue,
                    out authenticatedProductCode);
            }
        }

        public static bool MatchesRequestedProductCode(
            ProductCode authenticatedProductCode,
            string requestedRawProductCode,
            out ProductCode requestedProductCode)
        {
            if (!authenticatedProductCode.IsValid)
            {
                throw new ArgumentException(
                    "Authenticated product code must be valid.",
                    nameof(authenticatedProductCode));
            }

            if (!ProductCode.TryCreate(
                requestedRawProductCode,
                out requestedProductCode))
            {
                return false;
            }

            return StringComparer.OrdinalIgnoreCase.Equals(
                authenticatedProductCode.Value,
                requestedProductCode.Value);
        }
    }
}
