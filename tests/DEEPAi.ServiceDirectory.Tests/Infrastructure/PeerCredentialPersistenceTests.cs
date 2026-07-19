using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerCredentialPersistenceTests
    {
        private static readonly Guid PairingId = new Guid(
            "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12");
        private static readonly Guid LocalInstanceId = new Guid(
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77");
        private static readonly Guid PeerInstanceId = new Guid(
            "9f2ed127-9834-42b4-a379-eaad9df8fcec");
        private static readonly DateTime CommitExpiresUtc = new DateTime(
            2026,
            7,
            19,
            2,
            0,
            0,
            DateTimeKind.Utc);

        [TestMethod]
        public void BinaryCodecRoundTripsCanonicalPendingCredential()
        {
            var codec = new PeerCredentialBinaryCodec();
            using (PairedPeerCredential expected = CreateCredential(
                DurablePeerCredentialState.PairedPendingCommit,
                true,
                false))
            {
                byte[] encoded = codec.Serialize(expected);
                byte[] encodedAgain = codec.Serialize(expected);
                CollectionAssert.AreEqual(encoded, encodedAgain);
                CollectionAssert.AreEqual(
                    Encoding.ASCII.GetBytes("DPAISDPC"),
                    Slice(encoded, 0, 8));
                Assert.AreEqual(0, encoded[8]);
                Assert.AreEqual(1, encoded[9]);

                using (PairedPeerCredential actual =
                    codec.Deserialize(encoded))
                {
                    AssertCredentialEqual(expected, actual);
                    Assert.IsNotNull(actual.LocalCommitEvidence);
                    Assert.IsNull(actual.RemoteCommitEvidence);
                }

                Array.Clear(encoded, 0, encoded.Length);
                Array.Clear(encodedAgain, 0, encodedAgain.Length);
            }
        }

        [TestMethod]
        public void BinaryCodecRejectsVersionCaseAndTrailingTampering()
        {
            var codec = new PeerCredentialBinaryCodec();
            using (PairedPeerCredential credential = CreateCredential(
                DurablePeerCredentialState.PairedDisabled,
                true,
                true))
            {
                byte[] encoded = codec.Serialize(credential);

                byte[] futureVersion = (byte[])encoded.Clone();
                futureVersion[9] = 2;
                Assert.ThrowsExactly<InvalidDataException>(
                    () => codec.Deserialize(futureVersion));

                byte[] uppercaseGuid = (byte[])encoded.Clone();
                int guidOffset = IndexOf(
                    uppercaseGuid,
                    Encoding.ASCII.GetBytes(PairingId.ToString("D")));
                Assert.IsTrue(guidOffset >= 0);
                uppercaseGuid[guidOffset] = (byte)'B';
                Assert.ThrowsExactly<InvalidDataException>(
                    () => codec.Deserialize(uppercaseGuid));

                byte[] trailing = new byte[encoded.Length + 1];
                Buffer.BlockCopy(
                    encoded,
                    0,
                    trailing,
                    0,
                    encoded.Length);
                Assert.ThrowsExactly<InvalidDataException>(
                    () => codec.Deserialize(trailing));

                Array.Clear(encoded, 0, encoded.Length);
                Array.Clear(futureVersion, 0, futureVersion.Length);
                Array.Clear(uppercaseGuid, 0, uppercaseGuid.Length);
                Array.Clear(trailing, 0, trailing.Length);
            }
        }

        [TestMethod]
        public void CredentialAndCommitEvidenceDefensivelyCopyAndDispose()
        {
            byte[] requestMac = Bytes(1, 32);
            byte[] body = Encoding.UTF8.GetBytes("<Response />");
            byte[] responseMac = Bytes(33, 32);
            var evidence = new PairingCommitEvidence(
                requestMac,
                200,
                body,
                responseMac);
            using (PairedPeerCredential credential = CreateCredential(
                DurablePeerCredentialState.PairedPendingCommit,
                true,
                false,
                evidence,
                null))
            {
                Array.Clear(requestMac, 0, requestMac.Length);
                Array.Clear(body, 0, body.Length);
                Array.Clear(responseMac, 0, responseMac.Length);
                evidence.Dispose();

                CollectionAssert.AreEqual(
                    Bytes(1, 32),
                    credential.LocalCommitEvidence.CopyRequestMac());
                byte[] root = credential.CopyPairRoot();
                root[0] ^= 0xff;
                CollectionAssert.AreEqual(
                    Bytes(64, 32),
                    credential.CopyPairRoot());
            }

            Assert.ThrowsExactly<ObjectDisposedException>(
                () => evidence.CopyRequestMac());

            PairedPeerCredential disposed = CreateCredential(
                DurablePeerCredentialState.PairedPendingCommit,
                false,
                false);
            disposed.Dispose();
            disposed.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => disposed.CopyPairRoot());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => disposed.CopyTranscriptHash());
        }

        [TestMethod]
        public void FileBoundaryClearsProtectorPlaintextBuffers()
        {
            string root = CreateStateDirectory();
            try
            {
                var protector = new CapturingProtector();
                var accessPolicy = new PeerSecretAccessPolicy(
                    new SecurityIdentifier("S-1-5-80-1-2-3-4-5"));
                var file = new PeerCredentialFile(
                    root,
                    protector,
                    accessPolicy);
                using (PairedPeerCredential expected = CreateCredential(
                    DurablePeerCredentialState.PairedDisabled,
                    true,
                    true))
                {
                    byte[] protectedBytes = file.EncodeProtected(expected);
                    AssertAllZero(protector.LastProtectInput);
                    using (PairedPeerCredential actual =
                        file.DecodeProtected(protectedBytes))
                    {
                        AssertCredentialEqual(expected, actual);
                    }

                    AssertAllZero(protector.LastUnprotectOutput);
                    Array.Clear(
                        protectedBytes,
                        0,
                        protectedBytes.Length);
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void CrossFileValidatorRequiresExactBindingAndState()
        {
            ServiceDirectoryConfiguration pendingConfiguration =
                CreateConfiguration(
                    DurableSynchronizationState.PairedPendingCommit);
            using (PairedPeerCredential pendingCredential = CreateCredential(
                DurablePeerCredentialState.PairedPendingCommit,
                true,
                false))
            {
                PeerCredentialConfigurationValidator.Validate(
                    pendingConfiguration,
                    pendingCredential);
            }

            ServiceDirectoryConfiguration unpaired =
                ServiceDirectoryConfiguration.CreateInitial(
                    "10.0.0.1",
                    LocalInstanceId);
            PeerCredentialConfigurationValidator.Validate(unpaired, null);
            using (PairedPeerCredential unexpected = CreateCredential(
                DurablePeerCredentialState.PairedDisabled,
                true,
                true))
            {
                Assert.ThrowsExactly<InvalidDataException>(
                    () => PeerCredentialConfigurationValidator.Validate(
                        unpaired,
                        unexpected));
            }

            ServiceDirectoryConfiguration wrongPeer =
                new ServiceDirectoryConfiguration(
                    "10.0.0.1",
                    LocalInstanceId,
                    42,
                    30,
                    SynchronizationConfiguration.PairedDisabled(
                        "http://10.0.0.3:21000",
                        PeerInstanceId,
                        42,
                        LastSynchronizationStatus.NotRun(),
                        PeerNotificationStatus.NotRun()));
            using (PairedPeerCredential credential = CreateCredential(
                DurablePeerCredentialState.PairedDisabled,
                true,
                true))
            {
                Assert.ThrowsExactly<InvalidDataException>(
                    () => PeerCredentialConfigurationValidator.Validate(
                        wrongPeer,
                        credential));
            }
        }

        [TestMethod]
        public void AclPolicyAllowsOnlyProtectedTrustedReadWriteRules()
        {
            var serviceSid = new SecurityIdentifier(
                "S-1-5-80-1-2-3-4-5");
            var systemSid = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            var administratorsSid = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var policy = new PeerSecretAccessPolicy(serviceSid);
            FileSecurity secure = CreateSecureDescriptor(
                serviceSid,
                systemSid,
                administratorsSid);
            policy.ValidateDescriptor(secure);

            FileSecurity inherited = CreateSecureDescriptor(
                serviceSid,
                systemSid,
                administratorsSid);
            inherited.SetAccessRuleProtection(false, true);
            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => policy.ValidateDescriptor(inherited));

            FileSecurity extraPrincipal = CreateSecureDescriptor(
                serviceSid,
                systemSid,
                administratorsSid);
            extraPrincipal.AddAccessRule(
                new FileSystemAccessRule(
                    new SecurityIdentifier(
                        WellKnownSidType.WorldSid,
                        null),
                    FileSystemRights.Read,
                    AccessControlType.Allow));
            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => policy.ValidateDescriptor(extraPrincipal));
        }

        private static PairedPeerCredential CreateCredential(
            DurablePeerCredentialState state,
            bool localConfirmed,
            bool remoteConfirmed,
            PairingCommitEvidence localEvidence = null,
            PairingCommitEvidence remoteEvidence = null)
        {
            PairingCommitEvidence ownedLocal = localEvidence;
            PairingCommitEvidence ownedRemote = remoteEvidence;
            bool disposeLocal = false;
            bool disposeRemote = false;
            if (localConfirmed && ownedLocal == null)
            {
                ownedLocal = CreateEvidence(1);
                disposeLocal = true;
            }

            if (remoteConfirmed && ownedRemote == null)
            {
                ownedRemote = CreateEvidence(101);
                disposeRemote = true;
            }

            try
            {
                return new PairedPeerCredential(
                    state,
                    PairingRole.Initiator,
                    PairingId,
                    LocalInstanceId,
                    PeerInstanceId,
                    "http://10.0.0.1:21000",
                    "http://10.0.0.2:21000",
                    42,
                    Bytes(32, 32),
                    Bytes(64, 32),
                    CommitExpiresUtc,
                    localConfirmed,
                    remoteConfirmed,
                    ownedLocal,
                    ownedRemote);
            }
            finally
            {
                if (disposeLocal)
                {
                    ownedLocal.Dispose();
                }

                if (disposeRemote)
                {
                    ownedRemote.Dispose();
                }
            }
        }

        private static PairingCommitEvidence CreateEvidence(int seed)
        {
            return new PairingCommitEvidence(
                Bytes(seed, 32),
                200,
                Encoding.UTF8.GetBytes("<Response />"),
                Bytes(seed + 32, 32));
        }

        private static ServiceDirectoryConfiguration CreateConfiguration(
            DurableSynchronizationState state)
        {
            SynchronizationConfiguration synchronization;
            if (state == DurableSynchronizationState.PairedPendingCommit)
            {
                synchronization =
                    SynchronizationConfiguration.PairedPendingCommit(
                        "http://10.0.0.2:21000",
                        PeerInstanceId,
                        42,
                        PairingId,
                        CommitExpiresUtc,
                        true,
                        false,
                        LastSynchronizationStatus.NotRun(),
                        PeerNotificationStatus.NotRun());
            }
            else
            {
                synchronization = state
                    == DurableSynchronizationState.Enabled
                    ? SynchronizationConfiguration.Enabled(
                        "http://10.0.0.2:21000",
                        PeerInstanceId,
                        42,
                        LastSynchronizationStatus.NotRun(),
                        PeerNotificationStatus.NotRun())
                    : SynchronizationConfiguration.PairedDisabled(
                        "http://10.0.0.2:21000",
                        PeerInstanceId,
                        42,
                        LastSynchronizationStatus.NotRun(),
                        PeerNotificationStatus.NotRun());
            }

            return new ServiceDirectoryConfiguration(
                "10.0.0.1",
                LocalInstanceId,
                42,
                30,
                synchronization);
        }

        private static FileSecurity CreateSecureDescriptor(
            SecurityIdentifier serviceSid,
            SecurityIdentifier systemSid,
            SecurityIdentifier administratorsSid)
        {
            var security = new FileSecurity();
            security.SetOwner(systemSid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(
                new FileSystemAccessRule(
                    serviceSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            security.AddAccessRule(
                new FileSystemAccessRule(
                    systemSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            security.AddAccessRule(
                new FileSystemAccessRule(
                    administratorsSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            return security;
        }

        private static void AssertCredentialEqual(
            PairedPeerCredential expected,
            PairedPeerCredential actual)
        {
            Assert.AreEqual(expected.State, actual.State);
            Assert.AreEqual(expected.LocalRole, actual.LocalRole);
            Assert.AreEqual(expected.PairingId, actual.PairingId);
            Assert.AreEqual(
                expected.LocalInstanceId,
                actual.LocalInstanceId);
            Assert.AreEqual(expected.PeerInstanceId, actual.PeerInstanceId);
            Assert.AreEqual(expected.LocalEndpoint, actual.LocalEndpoint);
            Assert.AreEqual(expected.PeerEndpoint, actual.PeerEndpoint);
            Assert.AreEqual(expected.KeyEpoch, actual.KeyEpoch);
            Assert.AreEqual(
                expected.CommitExpiresUtc,
                actual.CommitExpiresUtc);
            Assert.AreEqual(
                expected.LocalCommitConfirmed,
                actual.LocalCommitConfirmed);
            Assert.AreEqual(
                expected.RemoteCommitConfirmed,
                actual.RemoteCommitConfirmed);
            CollectionAssert.AreEqual(
                expected.CopyTranscriptHash(),
                actual.CopyTranscriptHash());
            CollectionAssert.AreEqual(
                expected.CopyPairRoot(),
                actual.CopyPairRoot());
        }

        private static byte[] Bytes(int seed, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = (byte)(seed + index);
            }

            return result;
        }

        private static byte[] Slice(
            byte[] source,
            int offset,
            int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static int IndexOf(byte[] source, byte[] pattern)
        {
            for (int offset = 0;
                offset <= source.Length - pattern.Length;
                offset++)
            {
                bool match = true;
                for (int index = 0; index < pattern.Length; index++)
                {
                    if (source[offset + index] != pattern[index])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return offset;
                }
            }

            return -1;
        }

        private static void AssertAllZero(byte[] value)
        {
            Assert.IsNotNull(value);
            foreach (byte current in value)
            {
                Assert.AreEqual(0, current);
            }
        }

        private static string CreateStateDirectory()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "dpai-peer-credential-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "secrets"));
            return root;
        }

        private sealed class CapturingProtector : IPeerCredentialProtector
        {
            internal byte[] LastProtectInput { get; private set; }

            internal byte[] LastUnprotectOutput { get; private set; }

            public byte[] Protect(byte[] plaintext)
            {
                LastProtectInput = plaintext;
                return (byte[])plaintext.Clone();
            }

            public byte[] Unprotect(byte[] protectedBytes)
            {
                LastUnprotectOutput = (byte[])protectedBytes.Clone();
                return LastUnprotectOutput;
            }
        }
    }
}
