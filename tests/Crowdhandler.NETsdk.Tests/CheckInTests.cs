using System;
using System.Linq;
using System.Net.Http;
using Crowdhandler.NETsdk.JSONTypes;
using Newtonsoft.Json;
using Xunit;

namespace Crowdhandler.NETsdk.Tests
{
    public class CheckInTests
    {
        private const string UA = "UA/1.0";
        private static string At(int minutesAgo) => DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");

        private static GateKeeper Gk(double minutes)
        {
            var gk = Fixture.NewGateKeeper();
            gk.CheckInInterval = TimeSpan.FromMinutes(minutes);
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 60)), Fixture.TokenJson(true));
            return gk;
        }

        private static string CookieWithSignatureAgedMinutes(int minutesAgo)
        {
            string gen = At(minutesAgo);
            return Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen) }, gens: new[] { gen });
        }

        [Fact]
        public void Disabled_NeverCallsApi()
        {
            var gk = Gk(0);
            var r = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", CookieWithSignatureAgedMinutes(30));
            Assert.Equal("allow", r.Action);
            Assert.False(r.checkIn);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void NotYetDue_StaysLocal()
        {
            var gk = Gk(10);
            var r = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", CookieWithSignatureAgedMinutes(1));
            Assert.Equal("allow", r.Action);
            Assert.False(r.checkIn);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void Due_ChecksInWithGet_RefreshesSignature_AndFlagsResult()
        {
            var gk = Gk(2);
            string requested = At(0);
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 60)), Fixture.TokenJson(true, requested: requested));
            var r = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", CookieWithSignatureAgedMinutes(5));

            Assert.Equal("allow", r.Action);
            Assert.True(r.checkIn);
            Assert.Equal("resp0000000000000000000000000001", r.responseID);
            var get = Fixture.RequestsTo("/v1/requests").Single();
            Assert.Equal(HttpMethod.Get, get.Method);
            Assert.Equal("/v1/requests/" + Fixture.Token, get.RequestUri.AbsolutePath);

            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            Assert.Single(cookie.tokens[0].signatures); // same room: the refreshed signature replaces the old one
            Assert.Equal(requested, Util.FormatUtc(cookie.tokens[0].signatures.Last().gen)); // clock reset

            // and the very next request is local again
            StubHandler.Requests.Clear();
            var r2 = gk.Validate(new Uri("https://www.example.com/t2"), UA, "en", "1.2.3.4", r.cookieValue);
            Assert.False(r2.checkIn);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void MultiRoom_CadenceIsPerVisitor_NotPerRoom()
        {
            // One token valid in rooms A and B with room-scoped signatures. The API refreshes every room's slot for the token on
            // any request, so check-ins are judged on the newest signature regardless of room, and sent from whichever page the
            // visitor is on. A fresh signature in B means a page in A is not due; once the newest is stale, a page in A checks in.
            var gk = Gk(2);
            var roomA = Fixture.Room(patternType: "contains", urlPattern: "/a", slug: "room-a", timeout: 60);
            var roomB = Fixture.Room(patternType: "contains", urlPattern: "/b", slug: "room-b", timeout: 60);

            string genA = At(30), genB = At(1);
            var fresh = Fixture.CookieJson(sigs: new[] { Fixture.Signature(genA, slug: "room-a"), Fixture.Signature(genB, slug: "room-b") }, gens: new[] { genA, genB });
            Fixture.ScriptApi(Fixture.RoomsJson(roomA, roomB), Fixture.TokenJson(true, slug: "room-a"));
            var notDue = gk.Validate(new Uri("https://www.example.com/a"), UA, "en", "1.2.3.4", fresh);
            Assert.Equal("allow", notDue.Action);
            Assert.False(notDue.checkIn);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));

            string genB2 = At(5);
            var stale = Fixture.CookieJson(sigs: new[] { Fixture.Signature(genA, slug: "room-a"), Fixture.Signature(genB2, slug: "room-b") }, gens: new[] { genA, genB2 });
            string requested = At(0);
            Fixture.ScriptApi(Fixture.RoomsJson(roomA, roomB), Fixture.TokenJson(true, slug: "room-a", requested: requested, hash: Fixture.Signature(requested, slug: "room-a")));
            var due = gk.Validate(new Uri("https://www.example.com/a?x=1"), UA, "en", "1.2.3.4", stale);
            Assert.Equal("allow", due.Action);
            Assert.True(due.checkIn);
            Assert.Contains("%2Fa%3Fx%3D1", Fixture.RequestsTo("/v1/requests").Single().RequestUri.Query); // sent for the room the visitor is in
            var cookie = JsonConvert.DeserializeObject<CookieData>(due.cookieValue);
            Assert.Equal(2, cookie.tokens[0].signatures.Count); // one per room: A's old signature replaced, B's kept
            Assert.True(gk.ValidateSignature(cookie.tokens[0].signatures.Last().sig, cookie.tokens[0].signatures.Last().gen, Fixture.Token, roomA).success);
            Assert.True(gk.ValidateSignature(cookie.tokens[0].signatures.First().sig, cookie.tokens[0].signatures.First().gen, Fixture.Token, roomB).success);
        }

        [Theory]
        [InlineData(503)]
        [InlineData(429)]
        [InlineData(404)]
        public void ApiFailureDuringCheckIn_IsSkipped_VisitorKeepsSession(int status)
        {
            var gk = Gk(2);
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 60)), "{\"error\":\"x\"}", tokenStatus: status);
            var r = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", CookieWithSignatureAgedMinutes(5));
            Assert.Equal("allow", r.Action);
            Assert.False(r.checkIn);
            Assert.Null(r.apiError);
            Assert.True(r.setCookie);
        }

        [Fact]
        public void ApiOutage_SuspendsCheckIns_SoOnlyOneRequestPaysTheTimeout()
        {
            var gk = Gk(2);
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 60)), "{\"error\":\"down\"}", tokenStatus: 503);
            string cookie = CookieWithSignatureAgedMinutes(5);
            var first = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", cookie);
            Assert.Equal("allow", first.Action);
            int calls = Fixture.RequestsTo("/v1/requests").Count();
            Assert.Equal(2, calls); // attempt + retry
            for (int i = 0; i < 5; i++)
            {
                var r = gk.Validate(new Uri("https://www.example.com/t" + i), UA, "en", "1.2.3.4", cookie);
                Assert.Equal("allow", r.Action);
                Assert.False(r.checkIn);
            }
            Assert.Equal(calls, Fixture.RequestsTo("/v1/requests").Count()); // no further check-in attempts while degraded
        }

        [Fact]
        public void ForgedFreshSignatureInCookie_CannotPostponeCheckIn()
        {
            // Visitor has a real, stale signature and appends a fake entry with a fresh gen to look "not due".
            var gk = Gk(2);
            string realGen = At(10), fakeGen = At(0);
            var cookie = Fixture.CookieJson(sigs: new[] { Fixture.Signature(realGen), "0000000000000000000000000000000000000000000000000000000000000000" }, gens: new[] { realGen, fakeGen });
            var r = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", cookie);
            Assert.Equal("allow", r.Action);
            Assert.True(r.checkIn, "the forged entry must not count");
        }

        [Fact]
        public void Throttled_Status6_SuspendsCheckInsToo()
        {
            var gk = Gk(2);
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 60)), "{\"result\":{\"status\":6,\"token\":null,\"slug\":\"\",\"responseID\":null,\"deployment\":null,\"promoted\":0}}");
            string cookie = CookieWithSignatureAgedMinutes(5);
            Assert.Equal("allow", gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", cookie).Action);
            int calls = Fixture.RequestsTo("/v1/requests").Count();
            for (int i = 0; i < 5; i++) Assert.Equal("allow", gk.Validate(new Uri("https://www.example.com/t" + i), UA, "en", "1.2.3.4", cookie).Action);
            Assert.Equal(calls, Fixture.RequestsTo("/v1/requests").Count());
        }

        [Fact]
        public void CheckIn_NotPromoted_RedirectsToWaitingRoom()
        {
            var gk = Gk(2);
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 60)), Fixture.TokenJson(false));
            var r = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", CookieWithSignatureAgedMinutes(5));
            Assert.Equal("redirect", r.Action);
            Assert.StartsWith("https://wait.test/main-sale?", r.redirectUrl);
            Assert.True(r.checkIn);
        }

        [Fact]
        public void CheckIn_NewTokenMinted_IsAdopted()
        {
            var gk = Gk(2);
            string requested = At(0);
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room(timeout: 60)), Fixture.TokenJson(true, token: "tok0NEW0000000B", requested: requested, hash: Fixture.Signature(requested, token: "tok0NEW0000000B")));
            var r = gk.Validate(new Uri("https://www.example.com/t"), UA, "en", "1.2.3.4", CookieWithSignatureAgedMinutes(5));
            Assert.Equal("allow", r.Action);
            var cookie = JsonConvert.DeserializeObject<CookieData>(r.cookieValue);
            Assert.Equal("tok0NEW0000000B", Assert.Single(cookie.tokens).token);
            Assert.Single(cookie.tokens[0].signatures);
        }

        [Fact]
        public void FreshFromWaitingRoom_UrlSignature_DoesNotCheckIn()
        {
            var gk = Gk(2);
            string requested = At(5);
            var url = new Uri($"https://www.example.com/t?ch-id={Fixture.Token}&ch-id-signature={Fixture.Signature(requested)}&ch-requested={Uri.EscapeDataString(requested)}");
            // even with a stale cookie for a *different* token: the URL-validated visitor must not be checked in against the old token's history
            var r = gk.Validate(url, UA, "en", "1.2.3.4", Fixture.CookieJson(token: "tok0OLD0000000Z", sigs: new[] { "x" }, gens: new[] { At(30) }));
            Assert.Equal("redirect", r.Action); // clean-URL redirect
            Assert.False(r.checkIn);
            Assert.Empty(Fixture.RequestsTo("/v1/requests"));
        }

        [Fact]
        public void Jitter_StaysWithin25Percent_AndIsStablePerToken()
        {
            var gk = Gk(10);
            var sig = new CookieSignature { gen = DateTime.UtcNow.AddMinutes(-7.4) };
            // 7.4 minutes is under the 7.5 minute floor: never due whatever the token
            for (int i = 0; i < 50; i++) Assert.False(gk.IsCheckInDue(sig, "tok" + i));
            sig.gen = DateTime.UtcNow.AddMinutes(-12.6);
            for (int i = 0; i < 50; i++) Assert.True(gk.IsCheckInDue(sig, "tok" + i));
            sig.gen = DateTime.UtcNow.AddMinutes(-10);
            bool a = gk.IsCheckInDue(sig, "tok0M7SBFAp9J8kK");
            Assert.Equal(a, gk.IsCheckInDue(sig, "tok0M7SBFAp9J8kK"));
        }

        [Fact]
        public void Config_ParsesMinutesFromConstructor()
        {
            var gk = new GateKeeper("pub", "priv", "https://api.test", "https://wait.test", null, "3", "0", null, "2.5");
            Assert.Equal(TimeSpan.FromMinutes(2.5), gk.CheckInInterval);
            Assert.Equal(TimeSpan.Zero, new GateKeeper("pub", "priv", checkInIntervalMinutes: "0").CheckInInterval);
            Assert.Equal(TimeSpan.Zero, new GateKeeper("pub", "priv", checkInIntervalMinutes: "junk").CheckInInterval);
            Assert.Equal(TimeSpan.FromMinutes(2), new GateKeeper("pub", "priv").CheckInInterval);
        }
    }
}

namespace Crowdhandler.NETsdk.Tests
{
    public class RoomLookupPathTests
    {
        private class LocalRooms : GateKeeper
        {
            public int Calls;
            public LocalRooms() : base(Fixture.NewPublicKey(), Fixture.PrivateKey, "https://api.test", "https://wait.test", null, "3", "0", null, "0") { }
            public override System.Collections.Generic.List<JSONTypes.RoomConfig> getRoomConfig() { Calls++; return new System.Collections.Generic.List<JSONTypes.RoomConfig> { Fixture.Room(slug: "local") }; }
        }

        [Fact]
        public async System.Threading.Tasks.Task AsyncPath_HonoursGetRoomConfigOverride_AndNeverFetchesRooms()
        {
            StubHandler.Reset();
            Fixture.ScriptApi("{\"result\":[]}", Fixture.TokenJson(false, slug: "local"));
            var gk = new LocalRooms();
            var r = await gk.ValidateAsync(new System.Uri("https://www.example.com/tickets"), "UA", "en", "1.2.3.4");
            Assert.Equal("redirect", r.Action);
            Assert.Equal("local", r.slug);
            Assert.True(gk.Calls >= 1);
            Assert.Empty(Fixture.RequestsTo("/v1/rooms"));
        }

        [Fact]
        public async System.Threading.Tasks.Task AsyncPath_DefaultGateKeeper_FetchesRoomsOnce_UnderConcurrency()
        {
            var gk = Fixture.NewGateKeeper(roomCacheTTL: "60");
            Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true));
            StubHandler.DelayRequestsMs = 0;
            var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task<GateKeeper.ValidateResult>>();
            for (int i = 0; i < 50; i++) tasks.Add(gk.ValidateAsync(new System.Uri("https://www.example.com/t" + i), "UA", "en", "1.2.3.4"));
            var results = await System.Threading.Tasks.Task.WhenAll(tasks);
            Assert.All(results, r => Assert.Equal("allow", r.Action));
            Assert.Single(Fixture.RequestsTo("/v1/rooms"));
        }

        [Fact]
        public async System.Threading.Tasks.Task AsyncPath_ColdCache_ManyConcurrentRequests_DoNotStarve()
        {
            // Regression for the cold-start collapse: with a limited thread pool, 200 concurrent first visits must all complete
            // well inside the API timeout budget. Before the fix this took minutes and timed out.
            System.Threading.ThreadPool.GetMinThreads(out int w, out int io);
            System.Threading.ThreadPool.SetMinThreads(4, io);
            try
            {
                var gk = Fixture.NewGateKeeper(roomCacheTTL: "60");
                Fixture.ScriptApi(Fixture.RoomsJson(Fixture.Room()), Fixture.TokenJson(true));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task<GateKeeper.ValidateResult>>();
                for (int i = 0; i < 200; i++) tasks.Add(System.Threading.Tasks.Task.Run(() => gk.ValidateAsync(new System.Uri("https://www.example.com/cold"), "UA", "en", "1.2.3.4")));
                var results = await System.Threading.Tasks.Task.WhenAll(tasks);
                Assert.All(results, r => Assert.Equal("allow", r.Action));
                Assert.True(sw.Elapsed < System.TimeSpan.FromSeconds(3), "took " + sw.Elapsed);
            }
            finally { System.Threading.ThreadPool.SetMinThreads(w, io); }
        }
    }
}
