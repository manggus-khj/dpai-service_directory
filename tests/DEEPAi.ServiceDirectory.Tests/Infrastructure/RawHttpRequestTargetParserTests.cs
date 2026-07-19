using System;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class RawHttpRequestTargetParserTests
    {
        [TestMethod]
        public void OriginFormIsSplitAtFirstQuestionMarkWithoutNormalization()
        {
            const string rawUrl =
                "/api/services?productCode=AB12&next=/a?b=c";

            RawHttpRequestTarget parsed;
            bool succeeded = RawHttpRequestTargetParser.TryParse(
                rawUrl,
                out parsed);

            Assert.IsTrue(succeeded);
            Assert.IsNotNull(parsed);
            Assert.AreEqual("/api/services", parsed.AbsolutePath);
            Assert.AreEqual(
                "?productCode=AB12&next=/a?b=c",
                parsed.RawQuery);
        }

        [TestMethod]
        public void EncodedCharactersArePreservedAndNotInterpreted()
        {
            const string rawUrl = "/api%2Fhealth?q=%2f%ZZ%";

            RawHttpRequestTarget parsed;
            bool succeeded = RawHttpRequestTargetParser.TryParse(
                rawUrl,
                out parsed);

            Assert.IsTrue(succeeded);
            Assert.AreEqual("/api%2Fhealth", parsed.AbsolutePath);
            Assert.AreEqual("?q=%2f%ZZ%", parsed.RawQuery);
        }

        [TestMethod]
        public void MissingAndEmptyQueriesRemainDistinct()
        {
            AssertTarget("/", "/", string.Empty);
            AssertTarget("/api/health?", "/api/health", "?");
        }

        [TestMethod]
        public void DuplicateAndEmptyQueryFieldsArePreservedForLaterValidation()
        {
            AssertTarget(
                "/api/services?a=1&a=2&&empty=&=value",
                "/api/services",
                "?a=1&a=2&&empty=&=value");
        }

        [TestMethod]
        public void NonOriginRequestTargetFormsAreRejected()
        {
            string[] invalidValues =
            {
                null,
                string.Empty,
                "api/health",
                "http://10.0.0.1:21000/api/health",
                "10.0.0.1:21000",
                "*"
            };

            foreach (string invalidValue in invalidValues)
            {
                AssertRejected(invalidValue);
            }
        }

        [TestMethod]
        public void FragmentWhitespaceControlAndNonAsciiAreRejected()
        {
            string[] invalidValues =
            {
                "/api#fragment",
                "/api?q=value#fragment",
                "/api path",
                "/api\tpath",
                "/api\rpath",
                "/api\npath",
                "/api\0path",
                "/api" + (char)0x1f + "path",
                "/api" + (char)0x7f + "path",
                "/서비스"
            };

            foreach (string invalidValue in invalidValues)
            {
                AssertRejected(invalidValue);
            }
        }

        [TestMethod]
        public void TargetValueRejectsNullComponents()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => new RawHttpRequestTarget(null, string.Empty));
            Assert.ThrowsExactly<ArgumentNullException>(
                () => new RawHttpRequestTarget("/", null));
        }

        private static void AssertTarget(
            string rawUrl,
            string expectedPath,
            string expectedQuery)
        {
            RawHttpRequestTarget parsed;
            Assert.IsTrue(
                RawHttpRequestTargetParser.TryParse(rawUrl, out parsed));
            Assert.IsNotNull(parsed);
            Assert.AreEqual(expectedPath, parsed.AbsolutePath);
            Assert.AreEqual(expectedQuery, parsed.RawQuery);
        }

        private static void AssertRejected(string rawUrl)
        {
            RawHttpRequestTarget parsed;
            Assert.IsFalse(
                RawHttpRequestTargetParser.TryParse(rawUrl, out parsed));
            Assert.IsNull(parsed);
        }
    }
}
