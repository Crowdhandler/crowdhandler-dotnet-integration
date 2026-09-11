using System.Collections.Generic;
using Xunit;

namespace Crowdhandler.NETsdk.Tests
{
    public class RoomMatchingTests
    {
        private static readonly GateKeeper Gk = Fixture.NewGateKeeper();

        [Theory]
        [InlineData("https://www.example.com", "www.example.com", true)]
        [InlineData("https://www.example.com", "example.com", true)]
        [InlineData("https://example.com", "www.example.com", true)]
        [InlineData("https://example.com", "WWW.EXAMPLE.COM", true)]
        [InlineData("https://example.com", "example.com:8443", true)]
        [InlineData("https://example.com", "shop.example.com", false)]
        [InlineData("https://example.com", "example.com.evil.com", false)]
        [InlineData("https://*.example.com", "shop.example.com", true)]
        [InlineData("https://*.example.com", "a.b.example.com", true)]
        [InlineData("https://*.example.com", "www.example.com", true)]
        [InlineData("https://*.example.com", "example.com", false)]
        [InlineData("https://*.example.com", "shop.example.com.evil.com", false)]
        [InlineData("", "example.com", false)]
        [InlineData("https://example.com", "", false)]
        public void DomainMatches(string roomDomain, string host, bool expected)
        {
            Assert.Equal(expected, GateKeeper.DomainMatches(roomDomain, host));
        }

        [Theory]
        [InlineData("all", null, "/anything?x=1", true)]
        [InlineData("contains", "/tickets", "/events/tickets/123", true)]
        [InlineData("contains", "/tickets", "/events", false)]
        [InlineData("contains-not", "/tickets", "/events", true)]
        [InlineData("contains-not", "/tickets", "/events/tickets", false)]
        [InlineData("regex", "^/events/\\d+", "/events/123", true)]
        [InlineData("regex", "^/events/\\d+", "/events/abc", false)]
        [InlineData("regex-not", "^/admin", "/events/123", true)]
        [InlineData("regex-not", "^/admin", "/admin/users", false)]
        [InlineData("REGEX", "^/events", "/events", true)]
        [InlineData("disabled", "/x", "/x", false)]
        [InlineData("unknown-type", "/x", "/x", false)]
        [InlineData("regex", "", "/x", false)]
        [InlineData("regex", "(unclosed", "/x", false)]
        [InlineData("contains", "", "/x", false)]
        public void MatchRoom_PatternTypes(string patternType, string pattern, string path, bool expected)
        {
            var rooms = new List<Crowdhandler.NETsdk.JSONTypes.RoomConfig> { Fixture.Room(patternType: patternType, urlPattern: pattern) };
            var match = Gk.MatchRoom("www.example.com", path, rooms);
            Assert.Equal(expected, match != null);
        }

        [Fact]
        public void MatchRoom_FirstMatchInFeedOrderWins()
        {
            var rooms = new List<Crowdhandler.NETsdk.JSONTypes.RoomConfig>
            {
                Fixture.Room(patternType: "regex", urlPattern: "^/vip", slug: "vip"),
                Fixture.Room(patternType: "all", slug: "catch-all"),
            };
            Assert.Equal("vip", Gk.MatchRoom("www.example.com", "/vip/tickets", rooms).Slug);
            Assert.Equal("catch-all", Gk.MatchRoom("www.example.com", "/other", rooms).Slug);
            Assert.Null(Gk.MatchRoom("www.other.com", "/vip", rooms));
        }

        [Fact]
        public void MatchRoom_NullSafe()
        {
            Assert.Null(Gk.MatchRoom("www.example.com", "/x", null));
            Assert.Null(Gk.MatchRoom(null, "/x", new List<Crowdhandler.NETsdk.JSONTypes.RoomConfig> { Fixture.Room() }));
            Assert.Null(Gk.MatchRoom("www.example.com", null, new List<Crowdhandler.NETsdk.JSONTypes.RoomConfig> { null, Fixture.Room(patternType: "contains", urlPattern: "/x") }));
        }

        [Theory]
        [InlineData("^/order/complete", "/order/complete?id=1", "busted")]
        [InlineData("^/order/complete", "/order/start", "not-busted")]
        [InlineData("", "/order/complete", "not-busted")]
        [InlineData("(bad", "/order/complete", "not-busted")]
        public void IsCheckoutBuster(string checkout, string path, string expected)
        {
            var rooms = new List<Crowdhandler.NETsdk.JSONTypes.RoomConfig> { Fixture.Room(checkout: checkout) };
            Assert.Equal(expected, Gk.IsCheckoutBuster("www.example.com", path, rooms));
        }

        [Fact]
        public void RoomsFeed_ParsesRealShape()
        {
            var rooms = ApiClient.ParseRooms(Fixture.RoomsJson(Fixture.Room(patternType: "regex", urlPattern: "^/a\\d+")));
            Assert.Single(rooms);
            Assert.Equal("regex", rooms[0].patternType);
            Assert.Equal("^/a\\d+", rooms[0].urlPattern);
            Assert.Equal(System.DateTimeKind.Utc, rooms[0].queueActivatesOn.Kind);
            Assert.Equal(Fixture.QueueActivatesOn, Util.FormatUtc(rooms[0].queueActivatesOn));
        }

        [Theory]
        [InlineData("{\"result\":null}")]
        [InlineData("{\"result\":{}}")]
        [InlineData("{\"error\":\"Invalid Key.\"}")]
        [InlineData("")]
        [InlineData("<html>")]
        public void RoomsFeed_BadShapesThrowApiException(string json)
        {
            Assert.Throws<CrowdhandlerApiException>(() => ApiClient.ParseRooms(json));
        }
    }
}
