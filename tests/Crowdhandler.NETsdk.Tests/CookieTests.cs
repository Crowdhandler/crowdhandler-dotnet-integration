using System;
using Newtonsoft.Json;
using Xunit;

namespace Crowdhandler.NETsdk.Tests
{
    public class CookieTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json")]
        [InlineData("{")]
        [InlineData("[]")]
        [InlineData("{\"integration\":\"dotnet\"}")]
        [InlineData("{\"tokens\":null}")]
        [InlineData("{\"tokens\":[]}")]
        [InlineData("{\"tokens\":[null]}")]
        [InlineData("{\"tokens\":[{\"token\":\"\"}]}")]
        [InlineData("{\"tokens\":[{\"token\":\"x\",\"touched\":-5}]}")]
        [InlineData("{\"tokens\":[{\"token\":\"x\",\"touched\":\"abc\"}]}")]
        public void getCookieData_ReturnsNullForAnythingUnusable(string cookie)
        {
            Assert.Null(Fixture.NewGateKeeper().getCookieData(cookie));
        }

        [Fact]
        public void getCookieData_AcceptsBareTokenWrittenByClientSideScript()
        {
            var data = Fixture.NewGateKeeper().getCookieData("tok0M7SBFAp9J8kK");
            Assert.NotNull(data);
            Assert.Single(data.tokens);
            Assert.Equal("tok0M7SBFAp9J8kK", data.tokens[0].token);
            Assert.Empty(data.tokens[0].signatures);
        }

        [Fact]
        public void getCookieData_ToleratesMissingSignaturesAndPreservesDeployment()
        {
            var data = Fixture.NewGateKeeper().getCookieData("{\"integration\":\"JSDK\",\"deployment\":\"dns\",\"tokens\":[{\"token\":\"tok0M7SBFAp9J8kK\",\"touched\":1717243200000,\"touchedSig\":\"abc\"}]}");
            Assert.NotNull(data);
            Assert.Equal("dns", data.deployment);
            Assert.Null(data.tokens[0].signatures);
        }

        [Fact]
        public void CookieRoundTrip_PreservesCanonicalTimestampFormat()
        {
            var gk = Fixture.NewGateKeeper();
            string gen = "2024-06-01T12:00:00Z";
            var data = gk.getCookieData(Fixture.CookieJson(sigs: new[] { "abc" }, gens: new[] { gen }));
            string json = JsonConvert.SerializeObject(data);
            Assert.Contains("\"gen\":\"2024-06-01T12:00:00Z\"", json);
            Assert.Equal(DateTimeKind.Utc, data.tokens[0].signatures[0].gen.Kind);
            Assert.DoesNotContain("deployment", json);
        }
    }
}
