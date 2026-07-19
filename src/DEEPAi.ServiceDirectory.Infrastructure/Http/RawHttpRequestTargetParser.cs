namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal static class RawHttpRequestTargetParser
    {
        public static bool TryParse(
            string rawUrl,
            out RawHttpRequestTarget requestTarget)
        {
            requestTarget = null;
            if (string.IsNullOrEmpty(rawUrl) || rawUrl[0] != '/')
            {
                return false;
            }

            for (int index = 0; index < rawUrl.Length; index++)
            {
                char current = rawUrl[index];
                if (current <= 0x20
                    || current == 0x7f
                    || current > 0x7f
                    || current == '#')
                {
                    return false;
                }
            }

            int queryIndex = rawUrl.IndexOf('?');
            string absolutePath = queryIndex < 0
                ? rawUrl
                : rawUrl.Substring(0, queryIndex);
            string rawQuery = queryIndex < 0
                ? string.Empty
                : rawUrl.Substring(queryIndex);

            requestTarget = new RawHttpRequestTarget(
                absolutePath,
                rawQuery);
            return true;
        }
    }
}
