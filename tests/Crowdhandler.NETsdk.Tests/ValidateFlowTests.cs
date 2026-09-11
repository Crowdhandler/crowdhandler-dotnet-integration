using System;
using System.Linq;
using System.Net.Http;
using Crowdhandler.NETsdk.JSONTypes;
using Newtonsoft.Json;
using Xunit;

namespace Crowdhandler.NETsdk.Tests
{
    public class ValidateFlowTests
    {
        private const string UA = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 & friends";
        private static string Now(int minutesAgo = 0) => DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");

        // ------------------------------------------------------------------ exclusions & no room

        [Fact]
        public void ExcludedUrl_IsAllowedWithoutTouchingApiOrCookie()
        {
            var gk = Fixture.NewGateKeeper();
            var r = gk.Validate(new Uri("https://www.example.com/assets/site.css"), UA, "en", "1.2.3.4");
            Assert.Equal("allow", r.Action);
            Assert.False(r.setCookie);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void DefaultExclusions_CoverFavicon()
        {
            var gk = Fixture.NewGateKeeper();
            var r = gk.Validate(new Uri("https://www.example.com/favicon.ico"), UA, "en", "1.2.3.4");
            Assert.Equal("allow", r.Action);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Theory]
        [InlineData("/tickets?file=x.css")]
        [InlineData("/download.css/tickets")]
        [InlineData("/tickets.json.php")]
        public void DefaultExclusions_DoNotOverMatch(string path)
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            Assert.Equal("redirect", gk.Validate(new Uri("https://www.example.com" + path), UA, "en", "1.2.3.4").Action);
        }

        [Fact]
        public void NoMatchingRoom_IsAllowed()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(domain: "https://other.com")), Fixture.TokenJson(true));
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.Equal("allow", r.Action);
            Assert.False(r.setCookie);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void InvalidExclusionsRegex_DoesNotTakeTheSiteDown()
        {
            var gk = Fixture.NewGateKeeper(exclusions: "(unclosed");
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            var r = gk.Validate(new Uri("https://www.example.com/site.css"), UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action); // nothing excluded, protection intact
        }

        [Theory]
        [InlineData("/a.webp")]
        [InlineData("/downloads/report.xlsx")]
        [InlineData("/app.mjs")]
        [InlineData("/app.js.map")]
        [InlineData("/app.js?v=3")]
        [InlineData("/styles/site.css?ver=2024.1&x=y")]
        public void DefaultExclusions_CoverCommonAssets(string path)
        {
            var gk = Fixture.NewGateKeeper();
            Assert.Equal("allow", gk.Validate(new Uri("https://www.example.com" + path), UA, "en", "1.2.3.4").Action);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void ApiCalls_CarryRequestSourceHeader()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.All(StubHandler.Requests, r => Assert.StartsWith("dotnet-sdk/1.1", r.request.Headers.GetValues("x-request-source").Single()));
        }

        // ------------------------------------------------------------------ first visit

        [Fact]
        public void FirstVisit_NotPromoted_RedirectsToWaitingRoom()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            var r = gk.Validate(new Uri("https://www.example.com/tickets?a=1&b=2"), UA, "en-GB,en;q=0.9", "1.2.3.4");

            Assert.Equal("redirect", r.Action);
            Assert.Equal("https://wait.test/main-sale?url=https%3A%2F%2Fwww.example.com%2Ftickets%3Fa%3D1%26b%3D2&ch-code=&ch-id=tok0M7SBFAp9J8kK&ch-public-key=" + gk.PublicApiKey, r.redirectUrl);
            Assert.Equal(Fixture.Token, r.token);
            Assert.Equal("main-sale", r.slug);
            Assert.False(r.setCookie);
            Assert.Equal("resp0000000000000000000000000001", r.responseID);

            var post = Fixture.RequestsTo("/v1/requests").Single();
            Assert.Equal(HttpMethod.Post, post.Method);
            Assert.Equal("https://api.test/v1/requests/", post.RequestUri.ToString());
            Assert.Equal(gk.PublicApiKey, post.Headers.GetValues("x-api-key").Single());
            string body = StubHandler.Requests.Last().body;
            Assert.Contains("url=https%3A%2F%2Fwww.example.com%2Ftickets%3Fa%3D1%26b%3D2", body);
            Assert.Contains("agent=Mozilla", body);
            Assert.Contains("lang=en-GB%2Cen%3Bq%3D0.9", body);
            Assert.Contains("ip=1.2.3.4", body);
            Assert.DoesNotContain("code=", body);
        }

        [Fact]
        public void FirstVisit_Promoted_SetsCookieWithSignature()
        {
            var gk = Fixture.NewGateKeeper();
            string requested = Now();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true, requested: requested));
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");

            Assert.Equal("allow", r.Action);
            Assert.True(r.setCookie);
            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            Assert.Equal("dotnet", cookie.integration);
            var tok = Assert.Single(cookie.tokens);
            Assert.Equal(Fixture.Token, tok.token);
            Assert.Equal(Fixture.Signature(requested), tok.signatures.Single().sig);
            Assert.Equal(requested, Util.FormatUtc(tok.signatures.Single().gen));
            Assert.True(tok.touched < 100_000_000_000UL, "touched is written in seconds so 1.0.x nodes can read it during a rolling upgrade");
            Assert.Equal(Fixture.TouchedSig(tok.touched), tok.touchedSig);

            // and that cookie validates locally on the next request, without an API call
            StubHandler.Requests.Clear();
            var r2 = gk.Validate(new Uri("https://www.example.com/tickets/2"), UA, "en", "1.2.3.4", r.cookieValue);
            Assert.Equal("allow", r2.Action);
            Assert.True(r2.setCookie);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
            Assert.Null(r2.responseID);
        }

        [Fact]
        public void EmptyIpAndLanguage_AreOmittedFromApiCall()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true));
            gk.Validate(new Uri("https://www.example.com/tickets"), UA, "", "");
            string body = StubHandler.Requests.Last().body;
            Assert.DoesNotContain("ip=", body);
            Assert.DoesNotContain("lang=", body);
        }

        [Fact]
        public void PriorityCode_IsForwardedToApi_AndStrippedFromUrl()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true));
            var r = gk.Validate(new Uri("https://www.example.com/tickets?ch-code=VIP%2B1&x=1"), UA, "en", "1.2.3.4");
            Assert.Contains("code=VIP%2B1", StubHandler.Requests.Last().body);
            Assert.Equal("redirect", r.Action);
            Assert.Equal("https://www.example.com/tickets?x=1", r.redirectUrl);
            Assert.True(r.setCookie);
            Assert.Equal("VIP+1", r.code);
        }

        [Fact]
        public void RejectedPriorityCode_RetriesWithoutIt()
        {
            var gk = Fixture.NewGateKeeper();
            int calls = 0;
            StubHandler.Respond = (req, body) =>
            {
                if (req.RequestUri.AbsolutePath.EndsWith("/v1/rooms")) return StubHandler.Json(200, Fixture.RoomsJson(Fixture.Room()));
                calls++;
                return body != null && body.Contains("code=") ? StubHandler.Json(401, "{\"error\":\" Invalid priority code\"}") : StubHandler.Json(200, Fixture.TokenJson(true));
            };
            var r = gk.Validate(new Uri("https://www.example.com/tickets?ch-code=BAD"), UA, "en", "1.2.3.4");
            Assert.Equal(2, calls);
            Assert.True(r.setCookie);
            Assert.Null(r.apiError);
        }

        // ------------------------------------------------------------------ return from waiting room

        [Fact]
        public void ReturnFromWaitingRoom_ValidSignature_SetsCookieAndRedirectsToCleanUrl()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            string requested = Now();
            var url = new Uri($"https://www.example.com/tickets?keep=a%3Db&ch-id={Fixture.Token}&ch-id-signature={Fixture.Signature(requested)}&ch-requested={Uri.EscapeDataString(requested)}&ch-code=&ch-fresh=true&CH-PUBLIC-KEY=zzz");
            var r = gk.Validate(url, UA, "en", "1.2.3.4");

            Assert.Equal("redirect", r.Action);
            Assert.Equal("https://www.example.com/tickets?keep=a%3Db", r.redirectUrl);
            Assert.True(r.setCookie);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            Assert.Equal(Fixture.Signature(requested), cookie.tokens[0].signatures[0].sig);
        }

        [Fact]
        public void ReturnFromWaitingRoom_PreservesPortInCleanUrl()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            string requested = Now();
            var url = new Uri($"https://www.example.com:8443/tickets?ch-id={Fixture.Token}&ch-id-signature={Fixture.Signature(requested)}&ch-requested={Uri.EscapeDataString(requested)}");
            var r = gk.Validate(url, UA, "en", "1.2.3.4");
            Assert.Equal("https://www.example.com:8443/tickets", r.redirectUrl);
        }

        [Fact]
        public void ReturnFromWaitingRoom_ExpiredSignature_GoesBackToApi()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 15)), Fixture.TokenJson(false));
            string requested = Now(minutesAgo: 20);
            var url = new Uri($"https://www.example.com/tickets?ch-id={Fixture.Token}&ch-id-signature={Fixture.Signature(requested)}&ch-requested={Uri.EscapeDataString(requested)}");
            var r = gk.Validate(url, UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
            Assert.StartsWith("https://wait.test/main-sale?", r.redirectUrl);
            Assert.True(r.expired);
            var get = Fixture.RequestsTo("/v1/requests").Single();
            Assert.Equal(HttpMethod.Get, get.Method);
            Assert.Equal("/v1/requests/" + Fixture.Token, get.RequestUri.AbsolutePath);
        }

        // ------------------------------------------------------------------ fail-open regressions: tampered input must never throw

        [Theory]
        [InlineData("ch-id=tok0M7SBFAp9J8kK&ch-id-signature=deadbeef&ch-requested=garbage")]
        [InlineData("ch-id=tok0M7SBFAp9J8kK&ch-id-signature=deadbeef")]
        [InlineData("ch-id=tok0M7SBFAp9J8kK&ch-id-signature=deadbeef&ch-requested=undefined")]
        [InlineData("ch-id=../../rooms&ch-id-signature=x&ch-requested=2024-01-01T00:00:00Z")]
        [InlineData("ch-id=%00&ch-requested=%ZZ")]
        [InlineData("ch-id=tok0M7SBFAp9J8kK&ch-id-signature=%E0%A4%A")]
        public void TamperedUrlParams_FallThroughToQueue(string query)
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            var r = gk.Validate(new Uri("https://www.example.com/tickets?" + query), UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
            Assert.StartsWith("https://wait.test/", r.redirectUrl);
            Assert.Null(r.apiError);
            // path-injection guard: whatever the token was, the API path is intact
            Assert.All(Fixture.RequestsTo("/v1/requests"), req => Assert.DoesNotContain("rooms", req.RequestUri.AbsolutePath));
        }

        [Theory]
        [InlineData("{\"tokens\":null}")]
        [InlineData("{\"tokens\":[{\"token\":\"tok0M7SBFAp9J8kK\",\"signatures\":null}]}")]
        [InlineData("{\"tokens\":[{\"token\":\"tok0M7SBFAp9J8kK\",\"signatures\":[{\"sig\":null,\"gen\":\"2024-01-01T00:00:00Z\"}]}]}")]
        [InlineData("{\"tokens\":[{\"token\":\"tok0M7SBFAp9J8kK\",\"touched\":18446744073709551615,\"touchedSig\":\"x\",\"signatures\":[{\"sig\":\"x\",\"gen\":\"2024-01-01T00:00:00Z\"}]}]}")]
        [InlineData("{\"tokens\":[{\"token\":\"tok0M7SBFAp9J8kK\",\"signatures\":[{\"sig\":\"x\",\"gen\":\"not a date\"}]}]}")]
        [InlineData("{\"tokens\":[{\"token\":\"../../rooms\",\"signatures\":[]}]}")]
        [InlineData("garbage")]
        public void TamperedCookie_FallsThroughToQueue(string cookie)
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4", cookie);
            Assert.Equal("redirect", r.Action);
            Assert.StartsWith("https://wait.test/", r.redirectUrl);
        }

        // ------------------------------------------------------------------ API failure semantics

        [Fact]
        public void ApiClientError_RedirectsToWaitingRoom_NotTrusted()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), "{\"error\":\"Invalid Key.\"}", tokenStatus: 404);
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
            Assert.StartsWith("https://wait.test/main-sale?", r.redirectUrl);
            Assert.NotNull(r.apiError);
            Assert.Equal(404, r.apiError.StatusCode);
            Assert.True(r.apiError.IsClientError);
            Assert.Single(Fixture.RequestsTo("/v1/requests")); // 4xx is not retried
        }

        [Fact]
        public void ApiRedirect_IsNeverFollowed_AndCountsAsTransientFailure()
        {
            var gk = Fixture.NewGateKeeper();
            StubHandler.Respond = (req, body) =>
            {
                if (req.RequestUri.AbsolutePath.EndsWith("/v1/rooms")) return StubHandler.Json(200, Fixture.RoomsJson(Fixture.Room()));
                var r = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found); r.Headers.Location = new Uri("https://evil.test/"); return r;
            };
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.Equal(302, ex.StatusCode);
            Assert.All(StubHandler.Requests, r => Assert.NotEqual("evil.test", r.request.RequestUri.Host));
        }

        [Fact]
        public void ApiThrottled429_IsTransient_ButNotRetried()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), "{\"message\":\"Too Many Requests\"}", tokenStatus: 429);
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.True(ex.IsTransient);
            Assert.Single(Fixture.RequestsTo("/v1/requests"));
        }

        [Theory]
        [InlineData(500)]
        [InlineData(502)]
        [InlineData(503)]
        public void ApiServerError_ThrowsTransient_AfterOneRetry(int status)
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), "{\"error\":\"Sorry!\"}", tokenStatus: status);
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.True(ex.IsTransient);
            Assert.Equal(status, ex.StatusCode);
            Assert.Equal(2, Fixture.RequestsTo("/v1/requests").Count());
        }

        [Fact]
        public void ApiThrottledStatus6_ThrowsTransient()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), "{\"result\":{\"status\":6,\"token\":null,\"slug\":\"\",\"responseID\":null,\"deployment\":null,\"promoted\":0}}");
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.True(ex.IsTransient);
        }

        [Fact]
        public void ApiNetworkFailure_ThrowsTransient_AndWrapsCause()
        {
            var gk = Fixture.NewGateKeeper();
            StubHandler.Respond = (req, body) =>
            {
                if (req.RequestUri.AbsolutePath.EndsWith("/v1/rooms")) return StubHandler.Json(200, Fixture.RoomsJson(Fixture.Room()));
                throw new HttpRequestException("connection reset");
            };
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.True(ex.IsTransient);
            Assert.Null(ex.StatusCode);
            Assert.IsType<HttpRequestException>(ex.InnerException);
        }

        [Fact]
        public void RoomFull_Status5_NullToken_RedirectsWithoutCrashing()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), "{\"result\":{\"status\":5,\"token\":null,\"title\":\"x\",\"position\":null,\"promoted\":0,\"slug\":\"main-sale\",\"ttl\":300}}");
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
            Assert.Contains("&ch-id=&", r.redirectUrl);
        }

        [Fact]
        public void PromotedWithoutHash_AllowsButStoresNoSignature()
        {
            // status 0 / pass-listed responses carry no hash; the visitor is allowed and re-checked next time
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), "{\"result\":{\"status\":0,\"token\":\"tok0M7SBFAp9J8kK\",\"responseID\":null,\"deployment\":\".net\",\"promoted\":1}}");
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.Equal("allow", r.Action);
            Assert.True(r.setCookie);
            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            Assert.Empty(cookie.tokens[0].signatures);
        }

        [Fact]
        public void UnknownToken_ApiMintsNewOne_CookieAdoptsIt()
        {
            var gk = Fixture.NewGateKeeper();
            string requested = Now();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true, token: "tok0NEW0000000A", requested: requested, hash: Fixture.Signature(requested, token: "tok0NEW0000000A")));
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4", Fixture.CookieJson(token: "tok0OLD0000000A"));
            Assert.Equal("allow", r.Action);
            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            Assert.Equal("tok0NEW0000000A", Assert.Single(cookie.tokens).token);
            Assert.Equal("/v1/requests/tok0OLD0000000A", Fixture.RequestsTo("/v1/requests").Single().RequestUri.AbsolutePath);
        }

        // ------------------------------------------------------------------ rooms feed resilience

        [Fact]
        public void RoomsFeedOutage_ServesLastGoodCopy()
        {
            string key = Fixture.NewPublicKey();
            var gk = Fixture.NewGateKeeper(publicKey: key, roomCacheTTL: "0");
            string requested = Now();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true, requested: requested));
            var first = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.True(first.setCookie);

            StubHandler.Respond = (req, body) => StubHandler.Json(503, "down");
            var second = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4", first.cookieValue);
            Assert.Equal("allow", second.Action); // validated locally against the stale room copy, no API needed
        }

        [Fact]
        public void RoomsFeedOutage_WithNoPriorCopy_ThrowsTransient()
        {
            var gk = Fixture.NewGateKeeper();
            StubHandler.Respond = (req, body) => StubHandler.Json(503, "down");
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.True(ex.IsTransient);
        }

        [Fact]
        public void RoomsFeedRejected_BadKey_RedirectsToWaitingRoom_NeverTrusts()
        {
            var gk = Fixture.NewGateKeeper();
            StubHandler.Respond = (req, body) => StubHandler.Json(404, "{\"error\":\"Invalid Key.\"}");
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
            Assert.StartsWith("https://wait.test/?url=", r.redirectUrl);
            Assert.NotNull(r.apiError);
            Assert.True(r.apiError.IsClientError);
            // excluded assets are still served
            Assert.Equal("allow", gk.Validate(new Uri("https://www.example.com/site.css"), UA, "en", "1.2.3.4").Action);
        }

        [Fact]
        public void RoomsFeedRejected_AfterWarmCache_DoesNotServeStaleRooms()
        {
            string key = Fixture.NewPublicKey();
            var gk = new GateKeeper(key, Fixture.PrivateKey, "https://api.test", "https://wait.test", null, "3", "1", null);
            string requested = Now();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true, requested: requested));
            var first = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.True(first.setCookie);
            System.Threading.Thread.Sleep(1200); // cache expires
            StubHandler.Respond = (req, body) => StubHandler.Json(404, "{\"error\":\"Invalid Key.\"}"); // key revoked
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4", first.cookieValue);
            Assert.Equal("redirect", r.Action); // not validated against the old rooms
            Assert.NotNull(r.apiError);
        }

        [Fact]
        public void RoomsFeedOutage_StaleCopyIsCached_NotRefetchedPerRequest()
        {
            var gk = Fixture.NewGateKeeper(roomCacheTTL: "60");
            string requested = Now();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true, requested: requested));
            var first = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            // expire the cache entry by using a fresh gatekeeper with the same key but TTL 0 fetch path forced... simpler: new key, prime, then break the API
            string key = Fixture.NewPublicKey();
            var gk2 = new GateKeeper(key, Fixture.PrivateKey, "https://api.test", "https://wait.test", null, "3", "1", null); // 1 s cache
            var primed = gk2.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            System.Threading.Thread.Sleep(1200);
            StubHandler.Requests.Clear();
            StubHandler.Respond = (req, body) => StubHandler.Json(503, "down");
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal("allow", gk2.Validate(new Uri("https://www.example.com/t" + i), UA, "en", "1.2.3.4", primed.cookieValue).Action);
            }
            Assert.True(Fixture.RequestsTo("/v1/rooms").Count() <= 2, "stale copy must be cached, not refetched on every request; fetched " + Fixture.RequestsTo("/v1/rooms").Count() + " times");
        }

        [Fact]
        public void RoomsFeedOutage_FailsFastAfterFirstAttempt()
        {
            var gk = Fixture.NewGateKeeper(roomCacheTTL: "60");
            StubHandler.Respond = (req, body) => StubHandler.Json(503, "down");
            Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            int after = Fixture.RequestsTo("/v1/rooms").Count();
            Assert.Equal(2, after); // one attempt plus one retry
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.True(ex.IsTransient);
            Assert.Equal(after, Fixture.RequestsTo("/v1/rooms").Count()); // suppressed: no further calls inside the backoff window
        }

        [Fact]
        public void RoomsFeed_IsCachedForConfiguredSeconds()
        {
            var gk = Fixture.NewGateKeeper(roomCacheTTL: "60");
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true));
            gk.Validate(new Uri("https://www.example.com/a"), UA, "en", "1.2.3.4");
            gk.Validate(new Uri("https://www.example.com/b"), UA, "en", "1.2.3.4");
            Assert.Single(Fixture.RequestsTo("/v1/rooms"));
        }

        // ------------------------------------------------------------------ checkout busting

        [Fact]
        public void CheckoutPage_BustsSessionAndNotifiesApi()
        {
            var gk = Fixture.NewGateKeeper();
            string gen = Now();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(checkout: "^/order/complete")), Fixture.TokenJson(true));
            var cookie = Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen) }, gens: new[] { gen });
            var r = gk.Validate(new Uri("https://www.example.com/order/complete?id=9"), UA, "en", "1.2.3.4", cookie);
            Assert.Equal("busted", r.bustCookie);
            Assert.Equal("allow", r.Action);
            Assert.Equal("/v1/requests/" + Fixture.Token, Fixture.RequestsTo("/v1/requests").Single().RequestUri.AbsolutePath);
        }

        [Fact]
        public void CheckoutPage_WithoutToken_DoesNotCreateASession()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(domain: "https://other.com", checkout: "^/order/complete")), Fixture.TokenJson(true));
            // room is on another domain so no validation happens; checkout regex should not fire a POST for a tokenless visitor either
            var rooms = Fixture.RoomsJson(Fixture.Room(patternType: "contains", urlPattern: "/never", checkout: "^/order/complete"));
            Fixture.ScriptApi(rooms, Fixture.TokenJson(true));
            var r = gk.Validate(new Uri("https://www.example.com/order/complete"), UA, "en", "1.2.3.4");
            Assert.Equal("busted", r.bustCookie);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void CheckoutPage_ThatIsAlsoExcluded_StillBustsSession()
        {
            // e.g. an SPA confirmation endpoint under /api/ that the operator excluded from queueing
            var gk = Fixture.NewGateKeeper(exclusions: "^/api/");
            string gen = Now();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(checkout: "^/api/checkout/complete")), Fixture.TokenJson(true));
            var r = gk.Validate(new Uri("https://www.example.com/api/checkout/complete"), UA, "en", "1.2.3.4", Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen) }, gens: new[] { gen }));
            Assert.Equal("allow", r.Action);
            Assert.Equal("busted", r.bustCookie);
            Assert.False(r.setCookie);
            Assert.Single(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void ExcludedUrl_WithRoomsFeedDown_IsStillAllowed()
        {
            var gk = Fixture.NewGateKeeper();
            StubHandler.Respond = (req, body) => StubHandler.Json(503, "down");
            var r = gk.Validate(new Uri("https://www.example.com/assets/site.css"), UA, "en", "1.2.3.4");
            Assert.Equal("allow", r.Action);
            Assert.Equal("not-busted", r.bustCookie);
        }

        [Fact]
        public void CleanUrl_PreservesRawQueryExactly()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true));
            var r = gk.Validate(new Uri("https://www.example.com/t?flag&empty=&x=a%20b&ch-fresh=true&y=1"), UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
            Assert.Equal("https://www.example.com/t?flag&empty=&x=a%20b&y=1", r.redirectUrl);
        }

        [Fact]
        public void ApiTimestampsWithFractionalSecondsOrOffsets_StillParse()
        {
            var gk = Fixture.NewGateKeeper();
            string requested = Now();
            string json = Fixture.TokenJson(true, requested: requested).Replace("\"requested\":\"" + requested + "\"", "\"requested\":\"" + requested.TrimEnd('Z') + ".000+00:00\"");
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), json);
            var r = gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.Equal("allow", r.Action);
            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            Assert.Equal(requested, Util.FormatUtc(cookie.tokens[0].signatures[0].gen));
        }

        [Fact]
        public void NullCaptchaRequired_DoesNotBreakParsing()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true).Replace("\"captchaRequired\":0", "\"captchaRequired\":null"));
            Assert.Equal("allow", gk.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4").Action);
        }

        [Fact]
        public void PerRequestTimeout_IsHonouredPerGateKeeper()
        {
            // the shared client pool must not bake in the first gatekeeper's timeout
            var slow = new GateKeeper(Fixture.NewPublicKey(), Fixture.PrivateKey, "https://api.test", "https://wait.test", null, "1", "0", null);
            var patient = new GateKeeper(Fixture.NewPublicKey(), Fixture.PrivateKey, "https://api.test", "https://wait.test", null, "5", "0", null);
            StubHandler.Reset();
            StubHandler.DelayRequestsMs = 1500;
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true));
            Assert.Throws<CrowdhandlerApiException>(() => slow.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4"));
            Assert.Equal("allow", patient.Validate(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4").Action);
        }

        // ------------------------------------------------------------------ async

        [Fact]
        public async System.Threading.Tasks.Task ValidateAsync_BehavesLikeValidate()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            var r = await gk.ValidateAsync(new Uri("https://www.example.com/tickets"), UA, "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
        }

        [Fact]
        public void Cookie_NeverExceedsByteBackstop()
        {
            var gk = Fixture.NewGateKeeper();
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(false));
            string cookie = null;
            for (int i = 0; i < 30; i++)
            {
                string requested = DateTime.UtcNow.AddSeconds(-i).ToString("yyyy-MM-ddTHH:mm:ssZ");
                var r = gk.Validate(new Uri($"https://www.example.com/t?ch-id={Fixture.Token}&ch-id-signature={Fixture.Signature(requested)}&ch-requested={Uri.EscapeDataString(requested)}"), UA, "en", "1.2.3.4", cookie);
                cookie = r.cookieValue;
                Assert.True(cookie.Length <= GateKeeper.MaxCookieBytes, "cookie grew to " + cookie.Length);
            }
        }

        [Fact]
        public void SignatureHistory_OnePerRoom_AndCappedAcrossRooms()
        {
            var gk = Fixture.NewGateKeeper(roomCacheTTL: "60");
            int roomCount = GateKeeper.MaxStoredSignatures + 5;
            var rooms = Enumerable.Range(0, roomCount).Select(i => Fixture.Room(patternType: "contains", urlPattern: "/r" + i + "/", slug: "room-" + i)).ToArray();
            Fixture.ScriptApi(Fixture.RoomsJson(rooms), Fixture.TokenJson(false));
            string cookie = null;

            // Same room three times: the history stays at one signature for it.
            for (int k = 0; k < 3; k++)
            {
                string requested = DateTime.UtcNow.AddSeconds(-k).ToString("yyyy-MM-ddTHH:mm:ssZ");
                var url = new Uri($"https://www.example.com/r0/?ch-id={Fixture.Token}&ch-id-signature={Fixture.Signature(requested, slug: "room-0")}&ch-requested={Uri.EscapeDataString(requested)}");
                cookie = gk.Validate(url, UA, "en", "1.2.3.4", cookie).cookieValue;
            }
            Assert.Single(JsonConvert.DeserializeObject<CookieData>(cookie).tokens[0].signatures);

            // Fifteen rooms: capped at ten, newest kept.
            for (int i = 0; i < roomCount; i++)
            {
                string requested = DateTime.UtcNow.AddSeconds(-i).ToString("yyyy-MM-ddTHH:mm:ssZ");
                var url = new Uri($"https://www.example.com/r{i}/?ch-id={Fixture.Token}&ch-id-signature={Fixture.Signature(requested, slug: "room-" + i)}&ch-requested={Uri.EscapeDataString(requested)}");
                var r = gk.Validate(url, UA, "en", "1.2.3.4", cookie);
                Assert.True(r.setCookie);
                cookie = r.cookieValue;
            }
            var data = JsonConvert.DeserializeObject<CookieData>(cookie);
            Assert.Equal(GateKeeper.MaxStoredSignatures, data.tokens[0].signatures.Count);
            Assert.True(gk.ValidateSignature(data.tokens[0].signatures.Last().sig, data.tokens[0].signatures.Last().gen, Fixture.Token, rooms[roomCount - 1]).success);
        }
    }
}
