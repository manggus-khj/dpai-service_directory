using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;

namespace DEEPAi.ServiceDirectory.Service
{
    internal interface IInstalledListenerAddressValidator
    {
        void Validate(ServiceDirectoryListenerAddress address);
    }

    internal sealed class InstalledListenerAddressValidator
        : IInstalledListenerAddressValidator
    {
        private const uint PublicNetworkCategory = 0;
        private const uint PrivateNetworkCategory = 1;
        private const uint DomainAuthenticatedNetworkCategory = 2;

        public void Validate(ServiceDirectoryListenerAddress address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            IPAddress configuredAddress;
            if (!IPAddress.TryParse(
                    address.CanonicalAddress,
                    out configuredAddress))
            {
                throw new InvalidOperationException(
                    "The canonical listener address could not be parsed.");
            }

            IReadOnlyCollection<Guid> adapterIds =
                FindActiveAdapterIds(configuredAddress);
            if (adapterIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "ListenAddress is not assigned to an active local network interface.");
            }

            ValidateTrustedNetworkProfiles(adapterIds);
        }

        private static IReadOnlyCollection<Guid> FindActiveAdapterIds(
            IPAddress configuredAddress)
        {
            var matches = new HashSet<Guid>();
            foreach (NetworkInterface networkInterface
                in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface == null
                    || networkInterface.OperationalStatus
                        != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType
                        == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties properties =
                    networkInterface.GetIPProperties();
                if (!ContainsAddress(
                        properties.UnicastAddresses,
                        configuredAddress))
                {
                    continue;
                }

                Guid adapterId;
                if (!Guid.TryParse(
                        networkInterface.Id,
                        out adapterId)
                    || adapterId == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "The ListenAddress network adapter ID is unavailable.");
                }

                matches.Add(adapterId);
            }

            return matches;
        }

        private static bool ContainsAddress(
            UnicastIPAddressInformationCollection unicastAddresses,
            IPAddress configuredAddress)
        {
            foreach (UnicastIPAddressInformation unicast
                in unicastAddresses)
            {
                if (unicast != null
                    && unicast.Address != null
                    && unicast.Address.Equals(configuredAddress))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ValidateTrustedNetworkProfiles(
            IReadOnlyCollection<Guid> adapterIds)
        {
            bool matchingProfileFound = false;
            bool trustedProfileFound = false;
            bool untrustedProfileFound = false;
            INetworkListManager manager = null;
            IEnumNetworkConnections connections = null;

            try
            {
                Type managerType = Type.GetTypeFromCLSID(
                    NetworkListManagerClassId,
                    true);
                manager = (INetworkListManager)Activator.CreateInstance(
                    managerType);
                connections = manager.GetNetworkConnections();
                while (true)
                {
                    INetworkConnection connection = null;
                    uint fetched;
                    int nextResult = connections.Next(
                        1,
                        out connection,
                        out fetched);
                    if (nextResult == 1 || fetched == 0)
                    {
                        ReleaseComObject(connection);
                        break;
                    }

                    if (nextResult < 0)
                    {
                        ReleaseComObject(connection);
                        Marshal.ThrowExceptionForHR(nextResult);
                    }

                    if (connection == null || fetched != 1)
                    {
                        ReleaseComObject(connection);
                        throw new InvalidOperationException(
                            "Windows returned an invalid network connection enumeration result.");
                    }

                    INetwork network = null;
                    try
                    {
                        if (!connection.IsConnected)
                        {
                            continue;
                        }

                        Guid adapterId = connection.GetAdapterId();
                        if (!ContainsAdapterId(adapterIds, adapterId))
                        {
                            continue;
                        }

                        matchingProfileFound = true;
                        network = connection.GetNetwork();
                        uint category = (uint)network.GetCategory();
                        if (category == PrivateNetworkCategory
                            || category ==
                                DomainAuthenticatedNetworkCategory)
                        {
                            trustedProfileFound = true;
                        }
                        else if (category == PublicNetworkCategory
                            || category >
                                DomainAuthenticatedNetworkCategory)
                        {
                            untrustedProfileFound = true;
                        }
                    }
                    finally
                    {
                        ReleaseComObject(network);
                        ReleaseComObject(connection);
                    }
                }
            }
            catch (COMException exception)
            {
                throw new InvalidOperationException(
                    "The Windows network profile could not be verified.",
                    exception);
            }
            finally
            {
                ReleaseComObject(connections);
                ReleaseComObject(manager);
            }

            if (!matchingProfileFound
                || !trustedProfileFound
                || untrustedProfileFound)
            {
                throw new InvalidOperationException(
                    "ListenAddress must belong only to an active Domain or Private network profile.");
            }
        }

        private static bool ContainsAdapterId(
            IReadOnlyCollection<Guid> adapterIds,
            Guid candidate)
        {
            foreach (Guid adapterId in adapterIds)
            {
                if (adapterId == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }

        private static readonly Guid NetworkListManagerClassId =
            new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        private enum NetworkCategory : uint
        {
            Public = PublicNetworkCategory,
            Private = PrivateNetworkCategory,
            DomainAuthenticated = DomainAuthenticatedNetworkCategory
        }

        [ComImport]
        [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INetworkListManager
        {
            [return: MarshalAs(UnmanagedType.Interface)]
            object GetNetworks(uint flags);

            [return: MarshalAs(UnmanagedType.Interface)]
            INetwork GetNetwork([In] Guid networkId);

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumNetworkConnections GetNetworkConnections();

            [return: MarshalAs(UnmanagedType.Interface)]
            INetworkConnection GetNetworkConnection(
                [In] Guid networkConnectionId);

            bool IsConnectedToInternet
            {
                [return: MarshalAs(UnmanagedType.VariantBool)]
                get;
            }

            bool IsConnected
            {
                [return: MarshalAs(UnmanagedType.VariantBool)]
                get;
            }

            uint GetConnectivity();
        }

        [ComImport]
        [Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumNetworkConnections
        {
            [PreserveSig]
            int Next(
                uint requested,
                [MarshalAs(UnmanagedType.Interface)]
                out INetworkConnection connection,
                out uint fetched);

            void Skip(uint count);

            void Reset();

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumNetworkConnections Clone();
        }

        [ComImport]
        [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INetworkConnection
        {
            [return: MarshalAs(UnmanagedType.Interface)]
            INetwork GetNetwork();

            bool IsConnectedToInternet
            {
                [return: MarshalAs(UnmanagedType.VariantBool)]
                get;
            }

            bool IsConnected
            {
                [return: MarshalAs(UnmanagedType.VariantBool)]
                get;
            }

            uint GetConnectivity();

            Guid GetConnectionId();

            Guid GetAdapterId();

            uint GetDomainType();
        }

        [ComImport]
        [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INetwork
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            string GetName();

            void SetName([MarshalAs(UnmanagedType.BStr)] string name);

            [return: MarshalAs(UnmanagedType.BStr)]
            string GetDescription();

            void SetDescription(
                [MarshalAs(UnmanagedType.BStr)] string description);

            Guid GetNetworkId();

            uint GetDomainType();

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumNetworkConnections GetNetworkConnections();

            void GetTimeCreatedAndConnected(
                out uint lowDateTimeCreated,
                out uint highDateTimeCreated,
                out uint lowDateTimeConnected,
                out uint highDateTimeConnected);

            bool IsConnectedToInternet
            {
                [return: MarshalAs(UnmanagedType.VariantBool)]
                get;
            }

            bool IsConnected
            {
                [return: MarshalAs(UnmanagedType.VariantBool)]
                get;
            }

            uint GetConnectivity();

            NetworkCategory GetCategory();

            void SetCategory(NetworkCategory newCategory);
        }
    }
}
