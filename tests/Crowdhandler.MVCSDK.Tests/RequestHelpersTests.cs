using Crowdhandler.MVCSDK;
using Xunit;

namespace Crowdhandler.MVCSDK.Tests
{
    public class RequestHelpersTests
    {
        [Theory]
        [InlineData("203.0.113.5, 10.0.0.1", "10.0.0.2", "203.0.113.5")]
        [InlineData(" 203.0.113.5 ", "10.0.0.2", "203.0.113.5")]
        [InlineData("203.0.113.5:44321", "10.0.0.2", "203.0.113.5")]
        [InlineData("unknown, 203.0.113.5", "10.0.0.2", "203.0.113.5")]
        [InlineData("", "10.0.0.2", "10.0.0.2")]
        [InlineData(null, "::ffff:10.0.0.2", "10.0.0.2")]
        [InlineData(null, "[2001:db8::1]:443", "2001:db8::1")]
        [InlineData("2001:db8::1", null, "2001:db8::1")]
        [InlineData("fe80::1%eth0", null, "fe80::1")]
        [InlineData("not an ip", "also not", "")]
        [InlineData(null, null, "")]
        public void ExtractClientIp(string forwarded, string remote, string expected)
        {
            Assert.Equal(expected, RequestHelpers.ExtractClientIp(forwarded, remote));
        }

        [Theory]
        [InlineData("{\"a\":1}", "{\"a\":1}")]
        [InlineData("%7B%22a%22%3A1%7D", "{\"a\":1}")]
        [InlineData("tok0M7SBFAp9J8kK", "tok0M7SBFAp9J8kK")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("%ZZ", "%ZZ")]
        public void NormaliseCookieValue(string raw, string expected)
        {
            Assert.Equal(expected, RequestHelpers.NormaliseCookieValue(raw));
        }
    }
}
