using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal enum ServiceDirectoryHttpRoute
    {
        NotFound = 0,
        External = 1,
        Admin = 2,
        WatchdogHealth = 3
    }

    internal static class HttpListenerRequestRouting
    {
        private const string AdminPrefix = "/admin/";
        private const string ApiPrefix = "/api/";
        private const string EncodedApiPrefix = "/api%";
        private const string PeerRoot = "/api/sync";
        private const string PeerPrefix = "/api/sync/";

        internal static AuthenticationSchemes SelectAuthentication(
            IHttpServerRequest request)
        {
            if (request == null)
            {
                return AuthenticationSchemes.Anonymous;
            }

            RawHttpRequestTarget target;
            return RawHttpRequestTargetParser.TryParse(
                    request.RawUrl,
                    out target)
                && IsAdminCandidate(target.AbsolutePath)
                && HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    request.LocalEndPoint,
                    request.RemoteEndPoint)
                    ? AuthenticationSchemes.Negotiate
                    : AuthenticationSchemes.Anonymous;
        }

        internal static ServiceDirectoryHttpRoute ResolveRoute(
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint,
            RawHttpRequestTarget target,
            ServiceDirectoryListenerAddress configuredAddress)
        {
            if (target == null || configuredAddress == null)
            {
                return ServiceDirectoryHttpRoute.NotFound;
            }

            if (IsAdminCandidate(target.AbsolutePath))
            {
                // Authentication selection remains anonymous outside exact
                // loopback scope, but the Admin adapter must still see the
                // request so its endpoint guard records the required 4106
                // diagnostic before returning a bodyless 403.
                return ServiceDirectoryHttpRoute.Admin;
            }

            if (IsPeerCandidate(target.AbsolutePath)
                || !IsApiCandidate(target.AbsolutePath))
            {
                return ServiceDirectoryHttpRoute.NotFound;
            }

            if (configuredAddress.Matches(localEndpoint))
            {
                return ServiceDirectoryHttpRoute.External;
            }

            if (IsLoopbackAddressCandidate(localEndpoint))
            {
                return ServiceDirectoryHttpRoute.WatchdogHealth;
            }

            // A missing, wrong-port, or unexpected non-loopback local
            // endpoint still reaches the External adapter so its exact
            // endpoint guard emits the required 4106 diagnostic before
            // rejecting the request.
            return ServiceDirectoryHttpRoute.External;
        }

        private static bool IsAdminCandidate(string path)
        {
            return path != null
                && path.StartsWith(
                    AdminPrefix,
                    StringComparison.Ordinal);
        }

        private static bool IsApiCandidate(string path)
        {
            return path != null
                && (path.StartsWith(ApiPrefix, StringComparison.Ordinal)
                    || path.StartsWith(
                        EncodedApiPrefix,
                        StringComparison.Ordinal));
        }

        private static bool IsPeerCandidate(string path)
        {
            return StringComparer.Ordinal.Equals(path, PeerRoot)
                || (path != null
                    && path.StartsWith(
                        PeerPrefix,
                        StringComparison.Ordinal));
        }

        private static bool IsLoopbackAddressCandidate(IPEndPoint endpoint)
        {
            return endpoint != null
                && endpoint.Address != null
                && IPAddress.IsLoopback(endpoint.Address);
        }
    }

    internal static class HttpListenerRequestMapper
    {
        internal static ExternalHttpRequestData ToExternal(
            IHttpServerContext context,
            RawHttpRequestTarget target)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            IHttpServerRequest request = context.Request;
            return new ExternalHttpRequestData(
                request.HttpMethod,
                target.AbsolutePath,
                target.RawQuery,
                CopyHeaderValues(
                    request.GetHeaderValues(
                        ExternalApiContract.ApiKeyHeaderName)),
                request.ContentType,
                CombineHeaderValues(
                    request.GetHeaderValues("Content-Encoding")),
                request.ContentLength64,
                request.InputStream,
                request.LocalEndPoint,
                request.RemoteEndPoint);
        }

        internal static AdminHttpRequestData ToAdmin(
            IHttpServerContext context,
            RawHttpRequestTarget target)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            IHttpServerRequest request = context.Request;
            IPrincipal principal = context.Principal;
            return new AdminHttpRequestData(
                request.HttpMethod,
                target.AbsolutePath,
                target.RawQuery,
                request.ContentType,
                CombineHeaderValues(
                    request.GetHeaderValues("Content-Encoding")),
                request.ContentLength64,
                request.InputStream,
                request.LocalEndPoint,
                request.RemoteEndPoint,
                principal);
        }

        private static IReadOnlyList<string> CopyHeaderValues(
            IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return new string[0];
            }

            var copy = new string[values.Count];
            for (int index = 0; index < values.Count; index++)
            {
                copy[index] = values[index];
            }

            return Array.AsReadOnly(copy);
        }

        private static string CombineHeaderValues(
            IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            var copy = new string[values.Count];
            for (int index = 0; index < values.Count; index++)
            {
                copy[index] = values[index] ?? string.Empty;
            }

            return string.Join(",", copy);
        }
    }

    internal interface IHttpDeadlineWaiter
    {
        Task WaitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken);
    }

    internal sealed class SystemHttpDeadlineWaiter : IHttpDeadlineWaiter
    {
        public Task WaitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return Task.Delay(timeout, cancellationToken);
        }
    }

    internal static class HttpListenerDeadlinePolicy
    {
        internal static readonly TimeSpan ExternalReadDeadline =
            TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan ExternalRegistrationDeadline =
            TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan AdminDeadline =
            TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan WatchdogHealthDeadline =
            TimeSpan.FromSeconds(5);

        internal static TimeSpan GetDeadline(
            ServiceDirectoryHttpRoute route,
            string method,
            string path)
        {
            switch (route)
            {
                case ServiceDirectoryHttpRoute.External:
                    return StringComparer.Ordinal.Equals(method, "POST")
                        && StringComparer.Ordinal.Equals(
                            path,
                            "/api/registration")
                        ? ExternalRegistrationDeadline
                        : ExternalReadDeadline;
                case ServiceDirectoryHttpRoute.Admin:
                    return AdminDeadline;
                case ServiceDirectoryHttpRoute.WatchdogHealth:
                    return WatchdogHealthDeadline;
                case ServiceDirectoryHttpRoute.NotFound:
                default:
                    return ExternalReadDeadline;
            }
        }
    }
}
