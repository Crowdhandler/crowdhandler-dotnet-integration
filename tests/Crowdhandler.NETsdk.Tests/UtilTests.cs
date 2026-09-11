using System;
using System.Linq;
using Xunit;

namespace Crowdhandler.NETsdk.Tests
{
    public class UtilTests
    {
        [Fact]
        public void Sha256_MatchesKnownVector()
        {
            // echo -n "abc" | sha256sum
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Util.SHA256Hash("abc"));
        }

        [Theory]
        [InlineData("2022-07-27T11:16:13Z", "2022-07-27T11:16:13Z")]
        [InlineData("2022-07-27T11:16:13+01:00", "2022-07-27T10:16:13Z")]
        [InlineData("2022-07-27T11:16:13", "2022-07-27T11:16:13Z")] // no zone: assume UTC, never local
        [InlineData("2022-07-27 11:16:13", "2022-07-27T11:16:13Z")]
        public void TryParseUtc_ParsesAsUtcAndRoundTrips(string input, string expected)
        {
            Assert.True(Util.TryParseUtc(input, out var dt));
            Assert.Equal(DateTimeKind.Utc, dt.Kind);
            Assert.Equal(expected, Util.FormatUtc(dt));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]
        [InlineData("undefined")]
        [InlineData("2022-13-45T99:99:99Z")]
        public void TryParseUtc_RejectsGarbageWithoutThrowing(string input)
        {
            Assert.False(Util.TryParseUtc(input, out _));
        }

        [Fact]
        public void FormatUtc_TreatsUnspecifiedKindAsUtc()
        {
            var unspecified = new DateTime(2022, 7, 27, 11, 16, 13, DateTimeKind.Unspecified);
            Assert.Equal("2022-07-27T11:16:13Z", Util.FormatUtc(unspecified));
        }

        [Fact]
        public void Touched_AcceptsSecondsAndMilliseconds()
        {
            var instant = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            ulong seconds = Util.DateTimeToUnixTimeStamp(instant);
            ulong millis = Util.DateTimeToUnixTimeStampMs(instant);

            Assert.True(Util.TryTouchedToDateTime(seconds, out var fromSeconds));
            Assert.True(Util.TryTouchedToDateTime(millis, out var fromMillis));
            Assert.Equal(instant, fromSeconds);
            Assert.Equal(instant, fromMillis);
        }

        [Theory]
        [InlineData(ulong.MaxValue)]
        [InlineData(9_999_999_999_999_999UL)]
        public void Touched_RejectsAbsurdValuesWithoutThrowing(ulong touched)
        {
            Assert.False(Util.TryTouchedToDateTime(touched, out _));
        }

        [Theory]
        [InlineData("tok0M7SBFAp9J8kK", true)]
        [InlineData("tok_abc-123", true)]
        [InlineData("tok", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("notatoken", false)]
        [InlineData("tok../rooms", false)]
        [InlineData("tok abc", false)]
        public void IsValidToken(string token, bool expected)
        {
            Assert.Equal(expected, Util.IsValidToken(token));
        }

        [Theory]
        [InlineData("https://h.test/p?a=1&ch-id=t&CH-ID-SIGNATURE=s&ch-requested=r&ch-code=c&ch-fresh=true&ch-public-key=k&b=x%3Dy", "https://h.test/p?a=1&b=x%3Dy")]
        [InlineData("https://h.test:8443/p?ch-id=t", "https://h.test:8443/p")]
        [InlineData("https://h.test/p", "https://h.test/p")]
        [InlineData("https://h.test/p?", "https://h.test/p")]
        public void RemoveCrowdhandlerParameters(string input, string expected)
        {
            Assert.Equal(expected, GateKeeper.RemoveCrowdhandlerParameters(new Uri(input)));
        }

        [Fact]
        public void FixedTimeEquals_Semantics()
        {
            Assert.True(Util.FixedTimeEquals("abc", "abc"));
            Assert.False(Util.FixedTimeEquals("abc", "abd"));
            Assert.False(Util.FixedTimeEquals("abc", "abcd"));
            Assert.False(Util.FixedTimeEquals(null, "abc"));
            Assert.False(Util.FixedTimeEquals("abc", null));
        }

        [Fact]
        public void BuildQuery_EscapesEverything()
        {
            var q = Util.BuildQuery(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("url", "https://a.com/x?y=1&z=2"),
                new System.Collections.Generic.KeyValuePair<string, string>("agent", "Mozilla/5.0 (X11; Linux)"),
                new System.Collections.Generic.KeyValuePair<string, string>("lang", null),
            });
            Assert.Equal("url=https%3A%2F%2Fa.com%2Fx%3Fy%3D1%26z%3D2&agent=Mozilla%2F5.0%20%28X11%3B%20Linux%29&lang=", q);
        }
    }
}
