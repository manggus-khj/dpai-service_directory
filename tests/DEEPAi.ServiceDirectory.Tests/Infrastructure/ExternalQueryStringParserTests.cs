using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class ExternalQueryStringParserTests
    {
        [TestMethod]
        public void EmptyQueriesProduceNoFields()
        {
            foreach (string rawQuery in new[] { null, string.Empty, "?" })
            {
                IReadOnlyList<ExternalApiQueryParameter> parameters;
                Assert.IsTrue(
                    ExternalQueryStringParser.TryParse(
                        rawQuery,
                        out parameters));
                Assert.IsNotNull(parameters);
                Assert.AreEqual(0, parameters.Count);
            }
        }

        [TestMethod]
        public void PercentDecodingIsStrictUtf8AndPreservesDuplicatesAndPlus()
        {
            IReadOnlyList<ExternalApiQueryParameter> parameters;
            Assert.IsTrue(
                ExternalQueryStringParser.TryParse(
                    "?productCode=AB12&productCode=%41%42%31%32"
                    + "&literal=a+b&utf8=%EC%84%9C%EB%B9%84%EC%8A%A4",
                    out parameters));

            Assert.AreEqual(4, parameters.Count);
            Assert.AreEqual("productCode", parameters[0].Name);
            Assert.AreEqual("AB12", parameters[0].Value);
            Assert.AreEqual("productCode", parameters[1].Name);
            Assert.AreEqual("AB12", parameters[1].Value);
            Assert.AreEqual("a+b", parameters[2].Value);
            Assert.AreEqual("서비스", parameters[3].Value);
        }

        [TestMethod]
        public void MalformedOrNonRfc3986RawQueriesAreRejected()
        {
            string[] invalidQueries =
            {
                "?x=%",
                "?x=%0",
                "?x=%GG",
                "?x=%C3%28",
                "?x=한글",
                "?x=has space",
                "?x=value#fragment",
                "?x=[value]",
                "?x=back\\slash",
                "?x=1&&y=2",
                "?x=1&"
            };

            foreach (string rawQuery in invalidQueries)
            {
                IReadOnlyList<ExternalApiQueryParameter> parameters;
                Assert.IsFalse(
                    ExternalQueryStringParser.TryParse(
                        rawQuery,
                        out parameters),
                    rawQuery);
                Assert.IsNull(parameters);
            }
        }

        [TestMethod]
        public void RawWireLengthAndFieldCountAreBoundedBeforeDecoding()
        {
            string maximumQuery = "?" + new string(
                'a',
                ExternalQueryStringParser.MaximumRawQueryBytes - 1);
            string oversizedQuery = maximumQuery + "a";
            IReadOnlyList<ExternalApiQueryParameter> parameters;

            Assert.AreEqual(
                ExternalQueryStringParser.MaximumRawQueryBytes,
                maximumQuery.Length);
            Assert.IsTrue(
                ExternalQueryStringParser.TryParse(
                    maximumQuery,
                    out parameters));
            Assert.AreEqual(1, parameters.Count);
            Assert.IsFalse(
                ExternalQueryStringParser.TryParse(
                    oversizedQuery,
                    out parameters));

            var fields = new List<string>();
            for (int index = 0;
                index < ExternalQueryStringParser.MaximumFieldCount;
                index++)
            {
                fields.Add("f" + index + "=v");
            }

            Assert.IsTrue(
                ExternalQueryStringParser.TryParse(
                    "?" + string.Join("&", fields),
                    out parameters));
            Assert.AreEqual(
                ExternalQueryStringParser.MaximumFieldCount,
                parameters.Count);

            fields.Add("overflow=v");
            Assert.IsFalse(
                ExternalQueryStringParser.TryParse(
                    "?" + string.Join("&", fields),
                    out parameters));
        }
    }
}
