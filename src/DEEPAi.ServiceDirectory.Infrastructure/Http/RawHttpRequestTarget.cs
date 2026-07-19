using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal sealed class RawHttpRequestTarget
    {
        internal RawHttpRequestTarget(string absolutePath, string rawQuery)
        {
            AbsolutePath = absolutePath
                ?? throw new ArgumentNullException(nameof(absolutePath));
            RawQuery = rawQuery
                ?? throw new ArgumentNullException(nameof(rawQuery));
        }

        internal string AbsolutePath { get; }

        // Empty when the request-target has no query. Otherwise this is the
        // exact wire value beginning with the first '?'.
        internal string RawQuery { get; }
    }
}
