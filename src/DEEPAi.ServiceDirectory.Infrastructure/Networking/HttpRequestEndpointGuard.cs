using System.Net;
using System.Net.Sockets;

namespace DEEPAi.ServiceDirectory.Infrastructure.Networking
{
    public static class HttpRequestEndpointGuard
    {
        public static bool IsConfiguredLocalEndpointAllowed(
            HttpListenerRequest request,
            ServiceDirectoryListenerAddress configuredAddress)
        {
            return request != null
                && IsConfiguredLocalEndpointAllowed(
                    request.LocalEndPoint,
                    configuredAddress);
        }

        public static bool IsConfiguredLocalEndpointAllowed(
            IPEndPoint localEndpoint,
            ServiceDirectoryListenerAddress configuredAddress)
        {
            return configuredAddress != null
                && configuredAddress.Matches(localEndpoint);
        }

        public static bool IsLoopbackScopeAllowed(HttpListenerRequest request)
        {
            return request != null
                && IsLoopbackScopeAllowed(
                    request.LocalEndPoint,
                    request.RemoteEndPoint);
        }

        public static bool IsLoopbackScopeAllowed(
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint)
        {
            if (localEndpoint == null
                || localEndpoint.Address == null
                || localEndpoint.Port != ServiceDirectoryListenerAddress.Port
                || !localEndpoint.Address.Equals(IPAddress.Loopback)
                || remoteEndpoint == null
                || remoteEndpoint.Address == null)
            {
                return false;
            }

            AddressFamily remoteFamily = remoteEndpoint.Address.AddressFamily;
            if (remoteFamily != AddressFamily.InterNetwork
                && remoteFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            return IPAddress.IsLoopback(remoteEndpoint.Address);
        }
    }
}
