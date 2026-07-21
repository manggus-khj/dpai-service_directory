using System;
using System.Globalization;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.InternalProtocol
{
    [TestClass]
    public sealed class PeerSyncXmlCodecTests
    {
        private const string XmlNamespace =
            "urn:deepai:service-directory:peer";
        private const string InstanceId =
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77";
        private const string SnapshotId =
            "6f248a04-cc3e-409a-b499-cb571e6d30b7";
        private const string OriginInstanceId =
            "9f2ed127-9834-42b4-a379-eaad9df8fcec";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void PeerSchemaUsesTheFixedEmbeddedResourceName()
        {
            CollectionAssert.Contains(
                typeof(PeerSyncXmlCodec).Assembly
                    .GetManifestResourceNames(),
                "DEEPAi.ServiceDirectory.InternalProtocol.Peer.peer.xsd");
        }

        [TestMethod]
        public void ParsePushReturnsPurposeSpecificCanonicalBatch()
        {
            string services = CreateService(
                    "Active App",
                    "ABCD",
                    "service.internal",
                    "10.0.0.5",
                    "21000",
                    "2026-07-18T00:00:00.1234567Z",
                    "false",
                    null,
                    "8")
                + CreateService(
                    "Old App",
                    "WXYZ",
                    "old-app.internal",
                    "10.0.0.7",
                    "22000",
                    "2026-07-01T00:00:00Z",
                    "true",
                    "2026-07-15T09:30:00Z",
                    "9");

            PeerPushExchangeRequest request =
                PeerSyncXmlCodec.ParseAuthenticatedPushRequest(
                    Encode(CreatePushDocument(
                        services,
                        "9",
                        "4",
                        "2",
                        "true")));

            Assert.AreEqual(new Guid(InstanceId), request.InstanceId);
            Assert.AreEqual(new Guid(SnapshotId), request.SnapshotId);
            Assert.AreEqual((ulong)9, request.LogicalClock);
            Assert.AreEqual((uint)4, request.BatchIndex);
            Assert.AreEqual((ulong)2, request.TotalCount);
            Assert.IsTrue(request.IsLastBatch);
            Assert.AreEqual(2, request.Items.Count);
            Assert.AreEqual("Active App", request.Items[0].Name);
            Assert.AreEqual("ABCD", request.Items[0].ProductCode);
            Assert.AreEqual(
                "service.internal",
                request.Items[0].ServiceHostName);
            Assert.AreEqual(
                "10.0.0.5",
                request.Items[0].ServiceIpv4Address);
            Assert.AreEqual(21000, request.Items[0].Port);
            Assert.AreEqual(DateTimeKind.Utc, request.Items[0].LastModifiedUtc.Kind);
            Assert.IsFalse(request.Items[0].Deleted);
            Assert.IsNull(request.Items[0].DeletedUtc);
            Assert.AreEqual((ulong)8, request.Items[0].LogicalVersion);
            Assert.AreEqual(new Guid(OriginInstanceId),
                request.Items[0].OriginInstanceId);
            Assert.IsTrue(request.Items[1].Deleted);
            Assert.IsNotNull(request.Items[1].DeletedUtc);
            Assert.AreEqual(DateTimeKind.Utc,
                request.Items[1].DeletedUtc.Value.Kind);
        }

        [TestMethod]
        public void ParseRejectsNullEmptyOversizedAndInvalidUtf8Bodies()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => PeerSyncXmlCodec.ParseAuthenticatedPushRequest(null));

            AssertFailure(
                new byte[0],
                PeerSyncProtocolFailure.InvalidRequest);
            AssertFailure(
                new byte[PeerSyncContract.MaximumExchangeBodyBytes + 1],
                PeerSyncProtocolFailure.BodyTooLarge);
            AssertFailure(
                new byte[] { 0xc3, 0x28 },
                PeerSyncProtocolFailure.InvalidRequest);
        }

        [TestMethod]
        public void ParseAcceptsExactRawBodyLimitAndRejectsOneByteMore()
        {
            string document = CreatePushDocument(
                CreateDefaultService("ABCD", "1"),
                "1",
                "0",
                "1",
                "true");
            byte[] documentBytes = Encode(document);
            string exactDocument = document + new string(
                ' ',
                PeerSyncContract.MaximumExchangeBodyBytes
                    - documentBytes.Length);
            byte[] exactBody = Encode(exactDocument);

            Assert.AreEqual(
                PeerSyncContract.MaximumExchangeBodyBytes,
                exactBody.Length);
            Assert.AreEqual(
                1,
                PeerSyncXmlCodec.ParseAuthenticatedPushRequest(exactBody)
                    .Items.Count);

            byte[] tooLarge = Encode(exactDocument + " ");
            AssertFailure(
                tooLarge,
                PeerSyncProtocolFailure.BodyTooLarge);
        }

        [TestMethod]
        public void SecureStreamingCountAcceptsOneThousandItems()
        {
            var services = new StringBuilder();
            for (int index = 0; index < 1000; index++)
            {
                services.Append(CreateDefaultService(
                    "A" + index.ToString(
                        "D3",
                        CultureInfo.InvariantCulture),
                    "1"));
            }

            PeerPushExchangeRequest request =
                PeerSyncXmlCodec.ParseAuthenticatedPushRequest(
                    Encode(CreatePushDocument(
                        services.ToString(),
                        "1",
                        "0",
                        "1000",
                        "true")));

            Assert.AreEqual(1000, request.Items.Count);
            Assert.AreEqual("A000", request.Items[0].ProductCode);
            Assert.AreEqual("A999", request.Items[999].ProductCode);
        }

        [TestMethod]
        public void SecureStreamingCountFailsAtOneThousandFirstBeforeSchema()
        {
            var services = new StringBuilder();
            for (int index = 0; index < 1001; index++)
            {
                services.Append("<Service />");
            }

            PeerSyncProtocolException exception = AssertFailure(
                Encode(CreatePushDocument(
                    services.ToString(),
                    "1",
                    "0",
                    "1001",
                    "true")),
                PeerSyncProtocolFailure.ItemLimitExceeded);

            StringAssert.Contains(exception.Message, "1,000");
        }

        [TestMethod]
        public void ParseRejectsDtdExternalEntityAndDepthOverSixteen()
        {
            string dtd =
                "<!DOCTYPE Exchange [<!ENTITY external SYSTEM "
                + "\"file:///C:/Windows/win.ini\">]>"
                + "<Exchange xmlns=\""
                + XmlNamespace
                + "\" Mode=\"Push\"><SyncData>&external;</SyncData>"
                + "</Exchange>";
            AssertRejected(dtd);

            var deep = new StringBuilder();
            deep.Append("<Exchange xmlns=\"");
            deep.Append(XmlNamespace);
            deep.Append("\" Mode=\"Push\">");
            for (int index = 0; index < 16; index++)
            {
                deep.Append("<Nested>");
            }

            for (int index = 0; index < 16; index++)
            {
                deep.Append("</Nested>");
            }

            deep.Append("</Exchange>");
            AssertRejected(deep.ToString());
        }

        [TestMethod]
        public void ParseRequiresFixedSchemaAndPushSyncDataCorrelation()
        {
            string valid = CreatePushDocument(
                CreateDefaultService("ABCD", "1"),
                "1",
                "0",
                "1",
                "true");
            string[] invalidDocuments =
            {
                valid.Replace(XmlNamespace, "urn:wrong"),
                valid.Replace(" Mode=\"Push\"", ""),
                valid.Replace("Mode=\"Push\"", "Mode=\"Pull\""),
                "<Exchange xmlns=\""
                    + XmlNamespace
                    + "\" Mode=\"Push\"><PullRequest><SnapshotId>"
                    + SnapshotId
                    + "</SnapshotId><BatchIndex>0</BatchIndex>"
                    + "</PullRequest></Exchange>",
                "<Exchange xmlns=\""
                    + XmlNamespace
                    + "\" Mode=\"Pull\"><PullRequest><SnapshotId>"
                    + SnapshotId
                    + "</SnapshotId><BatchIndex>0</BatchIndex>"
                    + "</PullRequest></Exchange>",
                valid.Replace(
                    "Mode=\"Push\"",
                    "Mode=\"Push\" Unexpected=\"true\""),
                valid.Replace(
                    "<Items>",
                    "<Items Unexpected=\"true\">"),
                valid.Replace(
                    "<Items>",
                    "<Unexpected /><Items>"),
                valid.Replace(
                    "<InstanceId>" + InstanceId + "</InstanceId>",
                    "<SnapshotId>" + SnapshotId + "</SnapshotId>"
                        + "<InstanceId>" + InstanceId + "</InstanceId>")
            };

            foreach (string invalidDocument in invalidDocuments)
            {
                AssertRejected(invalidDocument);
            }
        }

        [TestMethod]
        public void ParseRejectsNonCanonicalGuidUtcNumbersBooleansAndProductCode()
        {
            string valid = CreatePushDocument(
                CreateDefaultService("ABCD", "1"),
                "1",
                "0",
                "1",
                "true");
            string[] invalidDocuments =
            {
                valid.Replace(InstanceId, InstanceId.ToUpperInvariant()),
                valid.Replace(InstanceId, Guid.Empty.ToString("D")),
                valid.Replace(
                    "2026-07-18T00:00:00Z",
                    "2026-07-18T09:00:00+09:00"),
                valid.Replace(
                    "2026-07-18T00:00:00Z",
                    "2026-07-18T00:00:00.000Z"),
                valid.Replace("<LogicalClock>1</LogicalClock>",
                    "<LogicalClock>01</LogicalClock>"),
                valid.Replace("<BatchIndex>0</BatchIndex>",
                    "<BatchIndex>+0</BatchIndex>"),
                valid.Replace("<IsLastBatch>true</IsLastBatch>",
                    "<IsLastBatch>1</IsLastBatch>"),
                valid.Replace("<ProductCode>ABCD</ProductCode>",
                    "<ProductCode>abcd</ProductCode>"),
                valid.Replace("<Port>21000</Port>",
                    "<Port>021000</Port>")
            };

            foreach (string invalidDocument in invalidDocuments)
            {
                AssertRejected(invalidDocument);
            }
        }

        [TestMethod]
        public void ParseEnforcesDomainNameAddressAndPortSemantics()
        {
            string supplementaryName = string.Concat(
                Repeat("\U0001F600", 128));
            PeerPushExchangeRequest request =
                PeerSyncXmlCodec.ParseAuthenticatedPushRequest(
                    Encode(CreatePushDocument(
                        CreateService(
                            supplementaryName,
                            "ABCD",
                            "service.internal",
                            "10.0.0.5",
                            "65535",
                            "2026-07-18T00:00:00Z",
                            "false",
                            null,
                            "1"),
                        "1",
                        "0",
                        "1",
                        "true")));
            Assert.AreEqual(supplementaryName, request.Items[0].Name);
            Assert.AreEqual(512,
                StrictUtf8.GetByteCount(request.Items[0].Name));

            string[] invalidServices =
            {
                CreateService(
                    string.Concat(Repeat("\U0001F600", 129)),
                    "ABCD",
                    "service.internal",
                    "10.0.0.5",
                    "21000",
                    "2026-07-18T00:00:00Z",
                    "false",
                    null,
                    "1"),
                CreateService(
                    "App",
                    "ABCD",
                    "service.internal",
                    "999.1.1.1",
                    "21000",
                    "2026-07-18T00:00:00Z",
                    "false",
                    null,
                    "1"),
                CreateService(
                    "App",
                    "ABCD",
                    "service.internal",
                    "10.0.0.5",
                    "0",
                    "2026-07-18T00:00:00Z",
                    "false",
                    null,
                    "1")
            };

            foreach (string invalidService in invalidServices)
            {
                AssertRejected(CreatePushDocument(
                    invalidService,
                    "1",
                    "0",
                    "1",
                    "true"));
            }
        }

        [TestMethod]
        public void ParseEnforcesDeletedAndDeletedUtcInvariant()
        {
            string falseWithDeletedUtc = CreateService(
                "App",
                "ABCD",
                "service.internal",
                "10.0.0.5",
                "21000",
                "2026-07-18T00:00:00Z",
                "false",
                "2026-07-18T00:01:00Z",
                "1");
            string trueWithoutDeletedUtc = CreateService(
                "App",
                "ABCD",
                "service.internal",
                "10.0.0.5",
                "21000",
                "2026-07-18T00:00:00Z",
                "true",
                null,
                "1");

            AssertRejected(CreatePushDocument(
                falseWithDeletedUtc,
                "1",
                "0",
                "1",
                "true"));
            AssertRejected(CreatePushDocument(
                trueWithoutDeletedUtc,
                "1",
                "0",
                "1",
                "true"));
        }

        [TestMethod]
        public void ParseRequiresPositiveVersionNotAboveLogicalClock()
        {
            AssertRejected(CreatePushDocument(
                CreateDefaultService("ABCD", "0"),
                "1",
                "0",
                "1",
                "true"));
            AssertRejected(CreatePushDocument(
                CreateDefaultService("ABCD", "2"),
                "1",
                "0",
                "1",
                "true"));

            PeerPushExchangeRequest maximum =
                PeerSyncXmlCodec.ParseAuthenticatedPushRequest(
                    Encode(CreatePushDocument(
                        CreateDefaultService(
                            "ABCD",
                            ulong.MaxValue.ToString(
                                CultureInfo.InvariantCulture)),
                        ulong.MaxValue.ToString(
                            CultureInfo.InvariantCulture),
                        uint.MaxValue.ToString(
                            CultureInfo.InvariantCulture),
                        ulong.MaxValue.ToString(
                            CultureInfo.InvariantCulture),
                        "false")));
            Assert.AreEqual(ulong.MaxValue, maximum.LogicalClock);
            Assert.AreEqual(uint.MaxValue, maximum.BatchIndex);
            Assert.AreEqual(ulong.MaxValue, maximum.TotalCount);
            Assert.AreEqual(ulong.MaxValue,
                maximum.Items[0].LogicalVersion);
        }

        [TestMethod]
        public void ParseRequiresStrictOrdinalAscendingProductCodes()
        {
            string duplicate = CreateDefaultService("ABCD", "1")
                + CreateDefaultService("ABCD", "1");
            string descending = CreateDefaultService("WXYZ", "1")
                + CreateDefaultService("ABCD", "1");

            AssertRejected(CreatePushDocument(
                duplicate,
                "1",
                "0",
                "2",
                "true"));
            AssertRejected(CreatePushDocument(
                descending,
                "1",
                "0",
                "2",
                "true"));
        }

        private static PeerSyncProtocolException AssertRejected(string xml)
        {
            return AssertFailure(
                Encode(xml),
                PeerSyncProtocolFailure.InvalidRequest);
        }

        private static PeerSyncProtocolException AssertFailure(
            byte[] body,
            PeerSyncProtocolFailure expectedFailure)
        {
            PeerSyncProtocolException exception =
                Assert.ThrowsExactly<PeerSyncProtocolException>(
                    () => PeerSyncXmlCodec
                        .ParseAuthenticatedPushRequest(body));
            Assert.AreEqual(expectedFailure, exception.Failure);
            return exception;
        }

        private static string CreatePushDocument(
            string services,
            string logicalClock,
            string batchIndex,
            string totalCount,
            string isLastBatch)
        {
            return "<Exchange xmlns=\""
                + XmlNamespace
                + "\" Mode=\"Push\"><SyncData><InstanceId>"
                + InstanceId
                + "</InstanceId><SnapshotId>"
                + SnapshotId
                + "</SnapshotId><LogicalClock>"
                + logicalClock
                + "</LogicalClock><BatchIndex>"
                + batchIndex
                + "</BatchIndex><TotalCount>"
                + totalCount
                + "</TotalCount><IsLastBatch>"
                + isLastBatch
                + "</IsLastBatch><Items>"
                + services
                + "</Items></SyncData></Exchange>";
        }

        private static string CreateDefaultService(
            string productCode,
            string logicalVersion)
        {
            return CreateService(
                "App " + productCode,
                productCode,
                "service.internal",
                "10.0.0.5",
                "21000",
                "2026-07-18T00:00:00Z",
                "false",
                null,
                logicalVersion);
        }

        private static string CreateService(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            string port,
            string lastModifiedUtc,
            string deleted,
            string deletedUtc,
            string logicalVersion)
        {
            return "<Service><Name>"
                + name
                + "</Name><ProductCode>"
                + productCode
                + "</ProductCode><ServiceHostName>"
                + serviceHostName
                + "</ServiceHostName><ServiceIpv4Address>"
                + serviceIpv4Address
                + "</ServiceIpv4Address><Port>"
                + port
                + "</Port><LastModifiedUtc>"
                + lastModifiedUtc
                + "</LastModifiedUtc><Deleted>"
                + deleted
                + "</Deleted>"
                + (deletedUtc == null
                    ? string.Empty
                    : "<DeletedUtc>" + deletedUtc + "</DeletedUtc>")
                + "<LogicalVersion>"
                + logicalVersion
                + "</LogicalVersion><OriginInstanceId>"
                + OriginInstanceId
                + "</OriginInstanceId></Service>";
        }

        private static string[] Repeat(string value, int count)
        {
            var values = new string[count];
            for (int index = 0; index < count; index++)
            {
                values[index] = value;
            }

            return values;
        }

        private static byte[] Encode(string value)
        {
            return StrictUtf8.GetBytes(value);
        }
    }
}
