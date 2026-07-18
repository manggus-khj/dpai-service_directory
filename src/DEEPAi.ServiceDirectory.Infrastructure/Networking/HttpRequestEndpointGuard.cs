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
            if (request == null || configuredAddress == null)
            {
                return false;
            }

            return configuredAddress.Matches(request.LocalEndPoint);
        }

        public static bool IsLoopbackScopeAllowed(HttpListenerRequest request)
        {
            if (request == null)
            {
                return false;
            }

            IPEndPoint localEndpoint = request.LocalEndPoint;
            IPEndPoint remoteEndpoint = request.RemoteEndPoint;
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
