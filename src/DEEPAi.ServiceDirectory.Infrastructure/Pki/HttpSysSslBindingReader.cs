using System;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class HttpSysSslBinding
    {
        internal HttpSysSslBinding(
            byte[] certificateHash,
            Guid applicationId,
            string certificateStoreName,
            uint certificateCheckMode,
            uint revocationFreshnessTime,
            uint revocationUrlRetrievalTimeout,
            uint flags)
        {
            CertificateHash = (byte[])certificateHash.Clone();
            ApplicationId = applicationId;
            CertificateStoreName = certificateStoreName;
            CertificateCheckMode = certificateCheckMode;
            RevocationFreshnessTime = revocationFreshnessTime;
            RevocationUrlRetrievalTimeout =
                revocationUrlRetrievalTimeout;
            Flags = flags;
        }

        internal byte[] CertificateHash { get; }

        internal Guid ApplicationId { get; }

        internal string CertificateStoreName { get; }

        internal uint CertificateCheckMode { get; }

        internal uint RevocationFreshnessTime { get; }

        internal uint RevocationUrlRetrievalTimeout { get; }

        internal uint Flags { get; }

        internal string GetThumbprint()
        {
            return BitConverter.ToString(CertificateHash)
                .Replace("-", string.Empty);
        }
    }

    internal static class HttpSysSslBindingReader
    {
        private const uint NoError = 0;
        private const uint ErrorInsufficientBuffer = 122;
        private const uint HttpInitializeConfig = 0x00000002;
        private const int HttpServiceConfigSslCertInfo = 1;
        private const int HttpServiceConfigQueryExact = 0;
        private const short AddressFamilyInterNetwork = 2;
        private const int SockaddrInLength = 16;

        internal static HttpSysSslBinding Read(
            IPAddress address,
            int port)
        {
            if (address == null
                || address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException(
                    "An IPv4 HTTP.sys binding address is required.",
                    nameof(address));
            }

            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            var version = new HttpApiVersion { Major = 1, Minor = 0 };
            uint result = HttpInitialize(
                version,
                HttpInitializeConfig,
                IntPtr.Zero);
            if (result != NoError)
            {
                throw CreateException(
                    result,
                    "HTTP Server API configuration initialization failed");
            }

            IntPtr socketAddress = IntPtr.Zero;
            IntPtr input = IntPtr.Zero;
            IntPtr output = IntPtr.Zero;
            try
            {
                socketAddress = CreateSocketAddress(address, port);
                var query = new HttpServiceConfigSslQuery
                {
                    QueryDescription = HttpServiceConfigQueryExact,
                    KeyDescription = new HttpServiceConfigSslKey
                    {
                        IpPort = socketAddress
                    },
                    Token = 0
                };
                int inputLength = Marshal.SizeOf(query);
                input = Marshal.AllocHGlobal(inputLength);
                Marshal.StructureToPtr(query, input, false);

                uint requiredLength;
                result = HttpQueryServiceConfiguration(
                    IntPtr.Zero,
                    HttpServiceConfigSslCertInfo,
                    input,
                    (uint)inputLength,
                    IntPtr.Zero,
                    0,
                    out requiredLength,
                    IntPtr.Zero);
                if (result != ErrorInsufficientBuffer
                    || requiredLength == 0
                    || requiredLength > int.MaxValue)
                {
                    throw CreateException(
                        result,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "HTTP.sys HTTPS binding {0}:{1} was not found",
                            address,
                            port));
                }

                output = Marshal.AllocHGlobal((int)requiredLength);
                result = HttpQueryServiceConfiguration(
                    IntPtr.Zero,
                    HttpServiceConfigSslCertInfo,
                    input,
                    (uint)inputLength,
                    output,
                    requiredLength,
                    out requiredLength,
                    IntPtr.Zero);
                if (result != NoError)
                {
                    throw CreateException(
                        result,
                        "HTTP.sys HTTPS binding query failed");
                }

                var value = (HttpServiceConfigSslSet)
                    Marshal.PtrToStructure(
                        output,
                        typeof(HttpServiceConfigSslSet));
                HttpServiceConfigSslParam parameters =
                    value.ParameterDescription;
                if (parameters.SslHashLength == 0
                    || parameters.SslHashLength > 128
                    || parameters.SslHash == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "HTTP.sys returned an invalid certificate hash.");
                }

                var hash = new byte[checked(
                    (int)parameters.SslHashLength)];
                Marshal.Copy(
                    parameters.SslHash,
                    hash,
                    0,
                    hash.Length);
                string storeName = parameters.SslCertStoreName
                    == IntPtr.Zero
                    ? "MY"
                    : Marshal.PtrToStringUni(
                        parameters.SslCertStoreName);
                return new HttpSysSslBinding(
                    hash,
                    parameters.ApplicationId,
                    storeName,
                    parameters.DefaultCertCheckMode,
                    parameters.DefaultRevocationFreshnessTime,
                    parameters.DefaultRevocationUrlRetrievalTimeout,
                    parameters.DefaultFlags);
            }
            finally
            {
                if (output != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(output);
                }

                if (input != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(input);
                }

                if (socketAddress != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(socketAddress);
                }

                HttpTerminate(HttpInitializeConfig, IntPtr.Zero);
            }
        }

        private static IntPtr CreateSocketAddress(
            IPAddress address,
            int port)
        {
            IntPtr value = Marshal.AllocHGlobal(SockaddrInLength);
            for (int index = 0; index < SockaddrInLength; index++)
            {
                Marshal.WriteByte(value, index, 0);
            }

            Marshal.WriteInt16(value, 0, AddressFamilyInterNetwork);
            Marshal.WriteByte(value, 2, (byte)((port >> 8) & 0xff));
            Marshal.WriteByte(value, 3, (byte)(port & 0xff));
            byte[] addressBytes = address.GetAddressBytes();
            for (int index = 0; index < addressBytes.Length; index++)
            {
                Marshal.WriteByte(value, 4 + index, addressBytes[index]);
            }

            return value;
        }

        private static Exception CreateException(
            uint errorCode,
            string message)
        {
            return new Win32Exception(
                unchecked((int)errorCode),
                message + " (Win32 "
                    + errorCode.ToString(CultureInfo.InvariantCulture)
                    + ").");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HttpApiVersion
        {
            internal ushort Major;
            internal ushort Minor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HttpServiceConfigSslKey
        {
            internal IntPtr IpPort;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HttpServiceConfigSslQuery
        {
            internal int QueryDescription;
            internal HttpServiceConfigSslKey KeyDescription;
            internal uint Token;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HttpServiceConfigSslParam
        {
            internal uint SslHashLength;
            internal IntPtr SslHash;
            internal Guid ApplicationId;
            internal IntPtr SslCertStoreName;
            internal uint DefaultCertCheckMode;
            internal uint DefaultRevocationFreshnessTime;
            internal uint DefaultRevocationUrlRetrievalTimeout;
            internal IntPtr DefaultSslCtlIdentifier;
            internal IntPtr DefaultSslCtlStoreName;
            internal uint DefaultFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HttpServiceConfigSslSet
        {
            internal HttpServiceConfigSslKey KeyDescription;
            internal HttpServiceConfigSslParam ParameterDescription;
        }

        [DllImport("httpapi.dll", SetLastError = true)]
        private static extern uint HttpInitialize(
            HttpApiVersion version,
            uint flags,
            IntPtr reserved);

        [DllImport("httpapi.dll", SetLastError = true)]
        private static extern uint HttpTerminate(
            uint flags,
            IntPtr reserved);

        [DllImport("httpapi.dll", SetLastError = true)]
        private static extern uint HttpQueryServiceConfiguration(
            IntPtr serviceHandle,
            int configId,
            IntPtr input,
            uint inputLength,
            IntPtr output,
            uint outputLength,
            out uint returnLength,
            IntPtr overlapped);
    }
}
