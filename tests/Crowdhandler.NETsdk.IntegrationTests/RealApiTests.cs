// Runs against a real CrowdHandler account. Skipped unless credentials are present in the sample app's user secrets
// (dotnet user-secrets, id crowdhandler-aspnetcore-sample) or environment variables Crowdhandler__PublicApiKey etc.
// Requires: a domain named https://{TestHost} with a live "all" room and a countdown room matching "/queue",
// and a domain checkout regex of ^/order/complete. See samples/AspNetCoreSample/README.md.
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Crowdhandler.NETsdk;
using Crowdhandler.NETsdk.JSONTypes;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Crowdhandler.NETsdk.IntegrationTests
{
    public sealed class RealApiFactAttribute : FactAttribute
    {
        public RealApiFactAttribute()
        {
            if (Config.PublicKey == null || Config.PrivateKey == null || Config.TestHost == null)
            {
                Skip = "No CrowdHandler test credentials configured (Crowdhandler:PublicApiKey / PrivateApiKey / TestHost).";
            }
        }
    }

    public static class Config
    {
        private static readonly IConfiguration Root = new ConfigurationBuilder()
            .AddUserSecrets("crowdhandler-aspnetcore-sample")
            .AddEnvironmentVariables()
            .Build();

        public static string PublicKey => Root["Crowdhandler:PublicApiKey"];
        public static string PrivateKey => Root["Crowdhandler:PrivateApiKey"];
        public static string ApiEndpoint => Root["Crowdhandler:ApiEndpoint"] ?? "https://api.crowdhandler.com";
        public static string WaitingRoomEndpoint => Root["Crowdhandler:WaitingRoomEndpoint"] ?? "https://wait.crowdhandler.com";
        public static string TestHost => Root["Crowdhandler:TestHost"];

        public static GateKeeper NewGateKeeper(string publicKey = null, string apiEndpoint = null, string timeout = "10")
            => new GateKeeper(publicKey ?? PublicKey, PrivateKey, apiEndpoint ?? ApiEndpoint, WaitingRoomEndpoint, null, timeout, "0", null);

        public static Uri Url(string pathAndQuery) => new Uri("https://" + TestHost + pathAndQuery);
        public const string UA = "Mozilla/5.0 (Macintosh) dotnet-sdk-integration-tests";
        public const string Ip = "203.0.113.77";
    }

    public class RealApiTests
    {
        [RealApiFact]
        public void RoomsFeed_ContainsTestDomainRooms_InFeedOrder()
        {
            var rooms = Config.NewGateKeeper().getRoomConfig();
            var mine = rooms.Where(r => r.domain.Contains(Config.TestHost)).ToList();
            Assert.True(mine.Count >= 2, "expected the test rooms in the public feed");
            Assert.Contains(mine, r => r.patternType == "contains");
            Assert.Contains(mine, r => r.patternType == "all");
            // feed order: specific patterns before catch-alls, so first match wins correctly
            Assert.True(mine.FindLastIndex(r => r.patternType == "contains") < mine.FindIndex(r => r.patternType == "all"), "contains rooms must precede all rooms");
            Assert.Equal(DateTimeKind.Utc, mine[0].queueActivatesOn.Kind);
            Assert.Contains("/order/complete", mine[0].checkout);
        }

        [RealApiFact]
        public void FirstVisit_LiveRoom_PromotesAndRealHashValidatesLocally()
        {
            var gk = Config.NewGateKeeper();
            var r = gk.Validate(Config.Url("/tickets?evt=1"), Config.UA, "en-GB", Config.Ip);

            Assert.Equal("allow", r.Action);
            Assert.True(r.setCookie);
            Assert.NotNull(r.responseID);
            Assert.StartsWith("tok", r.token);
            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            var sig = Assert.Single(cookie.tokens[0].signatures);
            Assert.Equal(64, sig.sig.Length);

            // The hash the API issued must validate with our local formula against the room from the feed.
            var room = gk.IsRoomMatch(Config.TestHost, "/tickets?evt=1");
            Assert.Equal("all", room.patternType);
            var check = gk.ValidateSignature(sig.sig, sig.gen, r.token, room);
            Assert.True(check.success, "API-issued hash did not validate locally: formula or timestamp format has drifted");

            // Second request with the cookie: validated locally, no API call.
            var r2 = gk.Validate(Config.Url("/tickets?evt=2"), Config.UA, "en-GB", Config.Ip, r.cookieValue);
            Assert.Equal("allow", r2.Action);
            Assert.Null(r2.responseID);
            Assert.True(r2.setCookie);
        }

        [RealApiFact]
        public void CountdownRoom_RedirectsToWaitingRoom_WithRealSlugAndToken()
        {
            var gk = Config.NewGateKeeper();
            var r = gk.Validate(Config.Url("/queue/x"), Config.UA, "en-GB", Config.Ip);
            Assert.Equal("redirect", r.Action);
            var room = gk.IsRoomMatch(Config.TestHost, "/queue/x");
            Assert.Equal("contains", room.patternType);
            Assert.StartsWith(Config.WaitingRoomEndpoint + "/" + room.Slug + "?url=", r.redirectUrl);
            Assert.Contains("&ch-id=tok", r.redirectUrl);
            Assert.Contains("&ch-public-key=" + Config.PublicKey, r.redirectUrl);
            Assert.False(r.setCookie);
        }

        [RealApiFact]
        public void ReturnFromWaitingRoom_WithRealHash_SetsCookieAndCleansUrl()
        {
            // Simulate what the waiting room does on promotion: obtain a real token+hash, then arrive with them in the URL.
            var gk = Config.NewGateKeeper();
            var first = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip);
            var cookie = JsonConvert.DeserializeObject<CookieData>(first.cookieValue);
            var sig = cookie.tokens[0].signatures.Single();
            string requested = Util_FormatUtc(sig.gen);

            var url = Config.Url($"/tickets?keep=1&ch-id={first.token}&ch-id-signature={sig.sig}&ch-requested={Uri.EscapeDataString(requested)}&ch-code=&ch-fresh=true");
            var r = gk.Validate(url, Config.UA, "en-GB", Config.Ip);
            Assert.Equal("redirect", r.Action);
            Assert.Equal("https://" + Config.TestHost + "/tickets?keep=1", r.redirectUrl);
            Assert.True(r.setCookie);
            Assert.Null(r.responseID); // validated locally
        }

        [RealApiFact]
        public void ExistingToken_IsRefreshedWithGet_AndEchoedBack()
        {
            var gk = Config.NewGateKeeper();
            var first = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip);
            // Cookie without signatures forces a GET /v1/requests/{token}
            string bare = first.token;
            var r = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip, bare);
            Assert.Equal("allow", r.Action);
            Assert.Equal(first.token, r.token);
            Assert.NotNull(r.responseID);
        }

        [RealApiFact]
        public async Task CheckIn_RefreshesLiveSession_AndKeepsToken()
        {
            var gk = Config.NewGateKeeper();
            gk.CheckInInterval = TimeSpan.FromSeconds(2);
            var first = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip);
            Assert.True(first.setCookie);

            var notDue = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip, first.cookieValue);
            Assert.False(notDue.checkIn);

            await Task.Delay(3000);
            var due = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip, notDue.cookieValue);
            Assert.Equal("allow", due.Action);
            Assert.True(due.checkIn, "expected a check-in after the interval elapsed");
            Assert.Equal(first.token, due.token);
            Assert.NotNull(due.responseID);
            Assert.True(due.checkInMilliseconds > 0);
            var cookie = JsonConvert.DeserializeObject<CookieData>(due.cookieValue);
            Assert.Single(cookie.tokens[0].signatures); // same room: refreshed in place

            // the refreshed signature validates locally against the live room
            var room = gk.IsRoomMatch(Config.TestHost, "/tickets");
            var newest = cookie.tokens[0].signatures.Last();
            Assert.True(gk.ValidateSignature(newest.sig, newest.gen, due.token, room).success);
        }

        [RealApiFact]
        public async Task CheckoutPage_Busts_AndTheApiDestroysTheSession()
        {
            var gk = Config.NewGateKeeper();
            var first = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip);
            var control = gk.Validate(Config.Url("/tickets?control=1"), Config.UA + " control", "en-GB", Config.Ip);
            Assert.NotEqual(first.token, control.token);

            var r = gk.Validate(Config.Url("/order/complete?id=1"), Config.UA, "en-GB", Config.Ip, first.cookieValue);
            Assert.Equal("busted", r.bustCookie);

            // Server side: the buyer's token must no longer be recognised (the API mints a new one), the control's must be.
            // /v1/requests GET responses are cached at CrowdHandler's edge for `ttl` keyed on the URL, so probe with a unique url.
            Assert.NotEqual(first.token, await ProbeTokenAsync(first.token, Config.UA));
            Assert.Equal(control.token, await ProbeTokenAsync(control.token, Config.UA + " control"));
        }

        private static async Task<string> ProbeTokenAsync(string token, string agent)
        {
            using (var http = new HttpClient())
            {
                string url = Uri.EscapeDataString("https://" + Config.TestHost + "/tickets?probe=" + Guid.NewGuid().ToString("N"));
                var req = new HttpRequestMessage(HttpMethod.Get, Config.ApiEndpoint + "/v1/requests/" + token + "?url=" + url + "&agent=" + Uri.EscapeDataString(agent) + "&lang=en&ip=" + Config.Ip);
                req.Headers.Add("x-api-key", Config.PublicKey);
                var body = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
                return (string)JObject.Parse(body)["result"]["token"];
            }
        }

        [RealApiFact]
        public void BadPublicKey_IsClientError_NotTrusted()
        {
            var gk = Config.NewGateKeeper(publicKey: new string('0', 64));
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.getRoomConfig());
            Assert.True(ex.IsClientError, "expected a definitive rejection, got " + ex.StatusCode);
            Assert.Equal(404, ex.StatusCode);
        }

        [RealApiFact]
        public void EmptyIpAndLanguage_AreAccepted()
        {
            var gk = Config.NewGateKeeper();
            var r = gk.Validate(Config.Url("/tickets"), Config.UA, "", "");
            Assert.Equal("allow", r.Action);
        }

        [RealApiFact]
        public void UnroutableApi_IsTransient_WithinTimeout()
        {
            var gk = Config.NewGateKeeper(apiEndpoint: "https://10.255.255.1", timeout: "1");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = Assert.Throws<CrowdhandlerApiException>(() => gk.getRoomConfig());
            Assert.True(ex.IsTransient);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "two 1 s attempts should not take " + sw.Elapsed);
        }

        [RealApiFact]
        public async Task PerformanceSample_IsAcceptedByApi()
        {
            var gk = Config.NewGateKeeper();
            var first = gk.Validate(Config.Url("/tickets"), Config.UA, "en-GB", Config.Ip);
            Assert.NotNull(first.responseID);

            using (var http = new HttpClient())
            {
                var req = new HttpRequestMessage(HttpMethod.Put, Config.ApiEndpoint + "/v1/responses/" + first.responseID)
                {
                    Content = new StringContent("{\"httpCode\":200,\"sampleRate\":5,\"time\":123}", Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-api-key", Config.PublicKey);
                var resp = await http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                Assert.True(resp.IsSuccessStatusCode, body);
                Assert.Equal(1, (int)JObject.Parse(body)["result"]["updated"]);
            }

            // and the SDK's fire-and-forget path does not throw
            gk.RecordPerformance(first.responseID, 200, 50, 1.0);
            await Task.Delay(1500);
        }

        private static string Util_FormatUtc(DateTime dt) => dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }
}
