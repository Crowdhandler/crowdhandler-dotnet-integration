using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Crowdhandler.MVCSDK;
using Crowdhandler.MVCSDK.AspNetCore;
using Crowdhandler.NETsdk;
using Crowdhandler.NETsdk.JSONTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Crowdhandler.MVCSDK.Tests
{
    /// <summary>A gatekeeper whose answer the test scripts, so adapter behaviour is tested without the API.</summary>
    public class ScriptedGateKeeper : IGateKeeper
    {
        public Func<Uri, string, string, string, string, GateKeeper.ValidateResult> OnValidate;
        public List<(Uri url, string ua, string lang, string ip, string cookie)> Calls = new List<(Uri, string, string, string, string)>();

        public GateKeeper.ValidateResult Validate(Uri url, string userAgent, string language, string ipAddress, string CookieJSON = "", RoomConfig room = null)
        {
            Calls.Add((url, userAgent, language, ipAddress, CookieJSON));
            return OnValidate(url, userAgent, language, ipAddress, CookieJSON);
        }

        public string PublicApiKey { get; set; } = "pub";
        public string PrivateApiKey { get; set; } = "priv";
        public string ApiEndpoint { get; set; } = "https://api.test";
        public string WaitingRoomEndpoint { get; set; } = "https://wait.test";
        public string Exclusions { get; set; }
        public string APIRequestTimeout { get; set; }
        public string RoomCacheTTL { get; set; }
    }

    public static class Ctx
    {
        public static DefaultHttpContext Build(string url = "https://www.example.com/tickets?x=1", string cookie = null, string xff = null, IServiceProvider services = null, bool https = true)
        {
            var ctx = new DefaultHttpContext();
            var uri = new Uri(url);
            ctx.Request.Scheme = uri.Scheme;
            ctx.Request.Host = new HostString(uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port);
            ctx.Request.Path = uri.AbsolutePath;
            ctx.Request.QueryString = new QueryString(uri.Query);
            ctx.Request.Headers["User-Agent"] = "UA/1.0";
            ctx.Request.Headers["Accept-Language"] = "en-GB";
            if (xff != null) ctx.Request.Headers["X-Forwarded-For"] = xff;
            if (cookie != null) ctx.Request.Headers["Cookie"] = "crowdhandler=" + Uri.EscapeDataString(cookie);
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.9");
            ctx.RequestServices = services ?? new ServiceCollection().BuildServiceProvider();
            ctx.Response.Body = new System.IO.MemoryStream();
            return ctx;
        }

        public static string SetCookieHeader(HttpContext ctx) => string.Join("\n", ctx.Response.Headers["Set-Cookie"].ToArray());

        public static IServiceProvider WithOptions(Action<CrowdhandlerOptions> configure)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCrowdhandler(configure);
            return services.BuildServiceProvider();
        }
    }

    public class ProcessorTests
    {
        private static readonly GateKeeper.ValidateResult Allow = new GateKeeper.ValidateResult { Action = "allow", setCookie = true, cookieValue = "{\"a\":1}", bustCookie = "not-busted", responseID = "resp1" };

        [Fact]
        public async Task OriginTimer_StartsAfterValidation()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => { System.Threading.Thread.Sleep(150); return Allow; } };
            var ctx = Ctx.Build();
            var before = System.Diagnostics.Stopwatch.GetTimestamp();
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, null);
            long validationMs = (outcome.StartTimestamp - before) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            Assert.True(validationMs >= 100, "timer should start after the 150 ms validation, started " + validationMs + " ms in");
        }

        [Fact]
        public async Task Allow_SetsCookieWithSafeAttributes_AndPassesRequestDetails()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => Allow };
            var ctx = Ctx.Build(cookie: "{\"old\":1}", xff: "203.0.113.5, 10.0.0.1");
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, NullLogger.Instance);

            Assert.Null(outcome.RedirectUrl);
            Assert.Equal("resp1", outcome.ResponseID);
            var call = Assert.Single(gk.Calls);
            Assert.Equal("https://www.example.com/tickets?x=1", call.url.ToString());
            Assert.Equal("UA/1.0", call.ua);
            Assert.Equal("en-GB", call.lang);
            Assert.Equal("203.0.113.5", call.ip);
            Assert.Equal("{\"old\":1}", call.cookie);

            string setCookie = Ctx.SetCookieHeader(ctx);
            Assert.StartsWith("crowdhandler=%7B%22a%22%3A1%7D", setCookie);
            Assert.Contains("path=/", setCookie);
            Assert.Contains("secure", setCookie);
            Assert.Contains("samesite=lax", setCookie);
            Assert.DoesNotContain("httponly", setCookie);
            Assert.DoesNotContain("expires", setCookie);
        }

        [Fact]
        public async Task Http_RequestDoesNotGetSecureCookie_UnlessForced()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => Allow };
            var ctx = Ctx.Build(url: "http://localhost:5000/tickets");
            await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, null);
            Assert.DoesNotContain("secure", Ctx.SetCookieHeader(ctx));

            var ctx2 = Ctx.Build(url: "http://localhost:5000/tickets");
            await CrowdhandlerRequestProcessor.HandleAsync(ctx2, new CrowdhandlerOptions { CookieSecure = true }, gk, null);
            Assert.Contains("secure", Ctx.SetCookieHeader(ctx2));
        }

        [Fact]
        public async Task CookieDomainAndMaxAge_AreApplied()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => Allow };
            var ctx = Ctx.Build();
            await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions { CookieDomain = ".example.com", CookieMaxAgeSeconds = 3600, CookieName = "chx" }, gk, null);
            string setCookie = Ctx.SetCookieHeader(ctx);
            Assert.StartsWith("chx=", setCookie);
            Assert.Contains("domain=.example.com", setCookie);
            Assert.Contains("max-age=3600", setCookie);
        }

        [Fact]
        public async Task Busted_DeletesCookieOnRootPath()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "allow", setCookie = true, cookieValue = "{}", bustCookie = "busted" } };
            var ctx = Ctx.Build();
            await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, null);
            string setCookie = Ctx.SetCookieHeader(ctx);
            Assert.StartsWith("crowdhandler=;", setCookie);
            Assert.Contains("expires=Thu, 01 Jan 1970", setCookie);
            Assert.Contains("path=/", setCookie);
        }

        [Fact]
        public async Task Redirect_SetsNoCacheHeaders()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "redirect", redirectUrl = "https://wait.test/room?url=x" } };
            var ctx = Ctx.Build();
            ctx.Response.Headers["Cache-Control"] = "public, max-age=60"; // pre-existing header must be overwritten, not throw
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, null);
            Assert.Equal("https://wait.test/room?url=x", outcome.RedirectUrl);
            Assert.Equal("no-cache, no-store, must-revalidate", ctx.Response.Headers["Cache-Control"]);
            Assert.Equal("no-cache", ctx.Response.Headers["Pragma"]);
        }

        [Fact]
        public async Task TransientFailure_FailTrustTrue_Allows()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new CrowdhandlerApiException("down", 503) };
            var ctx = Ctx.Build();
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, null);
            Assert.Null(outcome.RedirectUrl);
            Assert.Null(outcome.Result);
        }

        [Fact]
        public async Task TransientFailure_FailTrustFalse_RedirectsToSafetyNet_WithCleanReturnUrl()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new CrowdhandlerApiException("down", 503) };
            var ctx = Ctx.Build(url: "https://www.example.com/tickets?x=1&ch-id=tok0M7SBFAp9J8kK&ch-id-signature=abc&ch-requested=2024-01-01T00%3A00%3A00Z&y=2");
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions { FailTrust = false, SafetyNetSlug = "safety" }, gk, null);
            Assert.Equal("https://wait.test/safety?url=https%3A%2F%2Fwww.example.com%2Ftickets%3Fx%3D1%26y%3D2&ch-code=&ch-id=&ch-public-key=pub", outcome.RedirectUrl);
        }

        [Fact]
        public async Task CustomGatekeeperThrowingClientError_IsNeverTrusted()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new CrowdhandlerApiException("Invalid Key.", 404) };
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(Ctx.Build(), new CrowdhandlerOptions { FailTrust = true, SafetyNetSlug = "s" }, gk, null);
            Assert.StartsWith("https://wait.test/s?", outcome.RedirectUrl);
        }

        [Fact]
        public async Task ConfigurationErrors_AreNeverSwallowed()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new MissingFieldException("CROWDHANDLER_PUBLIC_KEY") };
            await Assert.ThrowsAsync<MissingFieldException>(() => CrowdhandlerRequestProcessor.HandleAsync(Ctx.Build(), new CrowdhandlerOptions(), gk, null));

            var gk2 = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new ArgumentException("Exclusions value is not a valid regular expression") };
            await Assert.ThrowsAsync<ArgumentException>(() => CrowdhandlerRequestProcessor.HandleAsync(Ctx.Build(), new CrowdhandlerOptions(), gk2, null));
        }

        [Fact]
        public async Task DebugMode_Rethrows()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new CrowdhandlerApiException("down", 503) };
            await Assert.ThrowsAsync<CrowdhandlerApiException>(() => CrowdhandlerRequestProcessor.HandleAsync(Ctx.Build(), new CrowdhandlerOptions { DebugMode = true }, gk, null));
        }

        [Fact]
        public async Task CustomClientIpHeader_IsHonoured()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => Allow };
            var ctx = Ctx.Build(xff: "203.0.113.5");
            ctx.Request.Headers["CF-Connecting-IP"] = "198.51.100.7";
            await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions { ClientIpHeader = "CF-Connecting-IP" }, gk, null);
            Assert.Equal("198.51.100.7", gk.Calls.Single().ip);
        }
    }

    public class MiddlewareTests
    {
        private static CrowdhandlerMiddleware Build(IGateKeeper gk, Func<HttpContext, Task> next, Action<CrowdhandlerOptions> extra = null, Func<HttpContext, bool> skip = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCrowdhandler(o => { o.GatekeeperFactory = () => gk; extra?.Invoke(o); });
            var sp = services.BuildServiceProvider();
            return new CrowdhandlerMiddleware(ctx => next(ctx), sp.GetRequiredService<IOptionsMonitor<CrowdhandlerOptions>>(), sp.GetRequiredService<ILoggerFactory>(), new CrowdhandlerMiddlewareOptions { Skip = skip });
        }

        [Fact]
        public async Task Redirect_ShortCircuits_WithLocationAnd302()
        {
            bool nextCalled = false;
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "redirect", redirectUrl = "https://wait.test/r" } };
            var mw = Build(gk, ctx => { nextCalled = true; return Task.CompletedTask; });
            var ctx = Ctx.Build();
            await mw.InvokeAsync(ctx);
            Assert.False(nextCalled);
            Assert.Equal(302, ctx.Response.StatusCode);
            Assert.Equal("https://wait.test/r", ctx.Response.Headers["Location"]);
        }

        [Fact]
        public async Task Allow_CallsNext()
        {
            bool nextCalled = false;
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "allow" } };
            var mw = Build(gk, ctx => { nextCalled = true; return Task.CompletedTask; });
            await mw.InvokeAsync(Ctx.Build());
            Assert.True(nextCalled);
        }

        [Fact]
        public async Task Skip_BypassesValidation()
        {
            bool nextCalled = false;
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new Exception("should not be called") };
            var mw = Build(gk, ctx => { nextCalled = true; return Task.CompletedTask; }, skip: ctx => ctx.Request.Path.StartsWithSegments("/health"));
            await mw.InvokeAsync(Ctx.Build(url: "https://www.example.com/health"));
            Assert.True(nextCalled);
            Assert.Empty(gk.Calls);
        }

        [Fact]
        public async Task StatusRoute_AnswersWithoutValidating()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new Exception("should not be called") };
            var mw = Build(gk, ctx => Task.CompletedTask);
            var ctx = Ctx.Build(url: "https://www.example.com/ch/status");
            await mw.InvokeAsync(ctx);
            ctx.Response.Body.Position = 0;
            string body = await new System.IO.StreamReader(ctx.Response.Body).ReadToEndAsync();
            Assert.Contains("\"integration\":\"dotnet\"", body);
            Assert.Contains("\"status\":\"ok\"", body);
            Assert.Equal("application/json", ctx.Response.ContentType);
            Assert.Empty(gk.Calls);
        }

        [Fact]
        public async Task SettingTheCookie_MarksResponsePrivate_UnlessAppSetCaching()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "allow", setCookie = true, cookieValue = "{}" } };
            var ctx = Ctx.Build();
            await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, null);
            Assert.Equal("private", ctx.Response.Headers["Cache-Control"]);

            var ctx2 = Ctx.Build();
            ctx2.Response.Headers["Cache-Control"] = "no-store";
            await CrowdhandlerRequestProcessor.HandleAsync(ctx2, new CrowdhandlerOptions(), gk, null);
            Assert.Equal("no-store", ctx2.Response.Headers["Cache-Control"]);
        }

        [Fact]
        public void CheckInSamples_BypassSampleRate()
        {
            var calls = new List<(string id, int status, long ms, double rate)>();
            var gk = new RecordingGateKeeper(calls);
            var outcome = new CrowdhandlerOutcome { GateKeeper = gk, SampleRate = 0.0001, StartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(), ResponseID = "r1",
                Result = new GateKeeper.ValidateResult { Action = "allow", responseID = "r1", checkIn = true, checkInMilliseconds = 1_000_000 } };
            outcome.RecordPerformance(200);
            var call = Assert.Single(calls);
            Assert.Equal(1.0, call.rate);
            Assert.True(call.ms < 1000); // origin time only: the check-in's own duration is never counted

            calls.Clear();
            var plain = new CrowdhandlerOutcome { GateKeeper = gk, SampleRate = 0.3, StartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(), ResponseID = "r2", Result = new GateKeeper.ValidateResult { responseID = "r2" } };
            plain.RecordPerformance(200);
            Assert.Equal(0.3, Assert.Single(calls).rate);

            calls.Clear();
            var off = new CrowdhandlerOutcome { GateKeeper = gk, SampleRate = 0, StartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(), ResponseID = "r3", Result = new GateKeeper.ValidateResult { responseID = "r3", checkIn = true } };
            off.RecordPerformance(200);
            Assert.Empty(calls);
        }

        private class RecordingGateKeeper : GateKeeper
        {
            private readonly List<(string, int, long, double)> _calls;
            public RecordingGateKeeper(List<(string, int, long, double)> calls) : base("pub", "priv", "https://api.test", "https://wait.test", null, "3", "0", null) { _calls = calls; }
            public override void RecordPerformance(string responseID, int httpStatusCode, long elapsedMilliseconds, double sampleRate = 0.2) => _calls.Add((responseID, httpStatusCode, elapsedMilliseconds, sampleRate));
        }

        [Fact]
        public void Options_CheckInInterval_ReachesGateKeeper()
        {
            var o = new CrowdhandlerOptions { PublicApiKey = "p", PrivateApiKey = "s", CheckInIntervalMinutes = 2 };
            Assert.Equal(TimeSpan.FromMinutes(2), Assert.IsType<GateKeeper>(o.CreateGateKeeper()).CheckInInterval);
            var typed = new CrowdhandlerOptions { PublicApiKey = "p", PrivateApiKey = "s", CheckInIntervalMinutes = 2, GatekeeperType = typeof(FilterTests.LocalRoomsGateKeeper) };
            Assert.Equal(TimeSpan.FromMinutes(2), Assert.IsType<FilterTests.LocalRoomsGateKeeper>(typed.CreateGateKeeper()).CheckInInterval);
            Assert.Equal(TimeSpan.FromMinutes(2), new CrowdhandlerOptions { PublicApiKey = "p", PrivateApiKey = "s" }.CreateGateKeeper() is GateKeeper g ? g.CheckInInterval : TimeSpan.MaxValue);
            Assert.Equal(TimeSpan.Zero, new CrowdhandlerOptions { PublicApiKey = "p", PrivateApiKey = "s", CheckInIntervalMinutes = 0 }.CreateGateKeeper() is GateKeeper z ? z.CheckInInterval : TimeSpan.MaxValue);
        }

        [Fact]
        public async Task NoHostHeader_IsLetThrough_NotA500()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new Exception("must not be called") };
            var ctx = Ctx.Build();
            ctx.Request.Host = new HostString(""); // HTTP/1.0 probe without Host
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions(), gk, null);
            Assert.Null(outcome.RedirectUrl);
            Assert.Empty(gk.Calls);
        }

        [Fact]
        public async Task ClientAbort_DuringValidation_IsNotAnError()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new OperationCanceledException() };
            var ctx = Ctx.Build();
            var cts = new System.Threading.CancellationTokenSource(); cts.Cancel(); ctx.RequestAborted = cts.Token;
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(ctx, new CrowdhandlerOptions { FailTrust = false, SafetyNetSlug = "s" }, gk, null);
            Assert.Null(outcome.RedirectUrl); // no safety-net redirect for a client that has gone away
        }

        private class SyncOverrideGateKeeper : GateKeeper
        {
            public bool SyncCalled;
            public SyncOverrideGateKeeper() : base("pub", "priv", "https://api.test", "https://wait.test", null, "3", "0", null) { }
            public override ValidateResult Validate(Uri url, string userAgent, string language, string ipAddress, string CookieJSON = "", Crowdhandler.NETsdk.JSONTypes.RoomConfig room = null)
            { SyncCalled = true; return new ValidateResult { Action = "allow" }; }
        }

        [Fact]
        public async Task SubclassOverridingSynchronousValidate_IsHonouredOnCore()
        {
            var gk = new SyncOverrideGateKeeper();
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(Ctx.Build(), new CrowdhandlerOptions(), gk, null);
            Assert.True(gk.SyncCalled);
            Assert.Null(outcome.RedirectUrl);
        }

        [Fact]
        public void GatekeeperType_StockGateKeeper_AndNineParameterSubclass_Construct()
        {
            var stock = new CrowdhandlerOptions { PublicApiKey = "p", PrivateApiKey = "s", CheckInIntervalMinutes = 3, GatekeeperType = typeof(GateKeeper) }.CreateGateKeeper();
            Assert.Equal("p", stock.PublicApiKey);
            Assert.Equal(TimeSpan.FromMinutes(3), Assert.IsType<GateKeeper>(stock).CheckInInterval);
            var nine = new CrowdhandlerOptions { PublicApiKey = "p", PrivateApiKey = "s", GatekeeperType = typeof(NineParamGateKeeper) }.CreateGateKeeper();
            Assert.Equal("p", nine.PublicApiKey);
        }

        public class NineParamGateKeeper : GateKeeper
        {
            public NineParamGateKeeper(string publicKey = null, string privateKey = null, string apiEndpoint = null, string waitingRoomEndpoint = null, string exclusions = null, string apiRequestTimeout = null, string roomCacheTTL = null, string safetyNetSlug = null, string checkInIntervalMinutes = null)
                : base(publicKey, privateKey, apiEndpoint, waitingRoomEndpoint, exclusions, apiRequestTimeout, roomCacheTTL, safetyNetSlug, checkInIntervalMinutes) { }
        }

        private class TwoArgGatekeeperOverride : CrowdhandlerFilterAttribute
        {
            public bool Called;
            protected override IGateKeeper getGatekeeper(HttpContext context, CrowdhandlerOptions options) { Called = true; return base.getGatekeeper(context, options); }
        }

        [Fact]
        public async Task Filter_TwoArgumentGetGatekeeperOverride_StillUsesDiOptions()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "allow" } };
            var sp = Ctx.WithOptions(o => o.GatekeeperFactory = () => gk);
            var ctx = ActionContextFor(Ctx.Build(services: sp));
            var filter = new TwoArgGatekeeperOverride();
            await filter.OnActionExecutionAsync(ctx, () => Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object())));
            Assert.True(filter.Called);
            Assert.Single(gk.Calls); // DI-provided gatekeeper used, no MissingFieldException
        }

        [Fact]
        public async Task Skip_RunsBeforeTheTenantResolver()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCrowdhandler((Func<HttpContext, CrowdhandlerOptions>)(ctx => throw new InvalidOperationException("resolver must not run for skipped requests")));
            var sp = services.BuildServiceProvider();
            bool nextCalled = false;
            var mw = new CrowdhandlerMiddleware(ctx => { nextCalled = true; return Task.CompletedTask; }, sp.GetRequiredService<IOptionsMonitor<CrowdhandlerOptions>>(), sp.GetRequiredService<ILoggerFactory>(), new CrowdhandlerMiddlewareOptions { Skip = ctx => ctx.Request.Path.StartsWithSegments("/health") });
            await mw.InvokeAsync(Ctx.Build(url: "https://www.example.com/health", services: sp));
            Assert.True(nextCalled);
        }

        [Fact]
        public async Task MultiTenant_ResolverSelectsKeysPerRequest_AndNullBypasses()
        {
            var gkA = new ScriptedGateKeeper { PublicApiKey = "tenant-a", OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "redirect", redirectUrl = "https://wait.test/a" } };
            var gkB = new ScriptedGateKeeper { PublicApiKey = "tenant-b", OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "allow" } };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCrowdhandler(ctx =>
            {
                switch (ctx.Request.Host.Host)
                {
                    case "a.platform.test": return new CrowdhandlerOptions { GatekeeperFactory = () => gkA };
                    case "b.platform.test": return new CrowdhandlerOptions { GatekeeperFactory = () => gkB };
                    default: return null; // tenant on another queue provider
                }
            });
            var sp = services.BuildServiceProvider();
            var mw = new CrowdhandlerMiddleware(ctx => { ctx.Response.StatusCode = 204; return Task.CompletedTask; }, sp.GetRequiredService<IOptionsMonitor<CrowdhandlerOptions>>(), sp.GetRequiredService<ILoggerFactory>());

            var a = Ctx.Build(url: "https://a.platform.test/tickets", services: sp); await mw.InvokeAsync(a);
            Assert.Equal(302, a.Response.StatusCode); Assert.Single(gkA.Calls); Assert.Empty(gkB.Calls);

            var b = Ctx.Build(url: "https://b.platform.test/tickets", services: sp); await mw.InvokeAsync(b);
            Assert.Equal(204, b.Response.StatusCode); Assert.Single(gkB.Calls);

            var c = Ctx.Build(url: "https://c.platform.test/tickets", services: sp); await mw.InvokeAsync(c);
            Assert.Equal(204, c.Response.StatusCode); Assert.Single(gkA.Calls); Assert.Single(gkB.Calls); // untouched

            // the filter honours the same resolver
            var fa = ActionContextFor(Ctx.Build(url: "https://a.platform.test/tickets", services: sp));
            await new CrowdhandlerFilterAttribute().OnActionExecutionAsync(fa, () => Task.FromResult(new ActionExecutedContext(fa, new List<IFilterMetadata>(), new object())));
            Assert.IsType<RedirectResult>(fa.Result);
            var fc = ActionContextFor(Ctx.Build(url: "https://c.platform.test/tickets", services: sp));
            bool ran = false;
            await new CrowdhandlerFilterAttribute().OnActionExecutionAsync(fc, () => { ran = true; return Task.FromResult(new ActionExecutedContext(fc, new List<IFilterMetadata>(), new object())); });
            Assert.True(ran); Assert.Null(fc.Result);
        }

        private static ActionExecutingContext ActionContextFor(HttpContext http)
        {
            var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
            return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object>(), controller: new object());
        }

        [Fact]
        public async Task MissingKeys_GiveAnActionableError()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCrowdhandler(o => { });
            var sp = services.BuildServiceProvider();
            var mw = new CrowdhandlerMiddleware(ctx => Task.CompletedTask, sp.GetRequiredService<IOptionsMonitor<CrowdhandlerOptions>>(), sp.GetRequiredService<ILoggerFactory>());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(Ctx.Build(services: sp)));
            Assert.Contains("Crowdhandler:PublicApiKey", ex.Message);
        }

        [Fact]
        public async Task PerformanceSample_IsSentAfterResponse()
        {
            // real GateKeeper against the stubbed API so the PUT is observable
            var requests = new List<HttpRequestMessage>();
            ApiClient.HttpMessageHandlerFactory = () => new RecordingHandler(requests);
            var gk = new GateKeeper("pub" + new string('x', 61), "priv", "https://api.test", "https://wait.test", null, "3", "0", null);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCrowdhandler(o => { o.GatekeeperFactory = () => gk; o.PerformanceSampleRate = 1.0; });
            var sp = services.BuildServiceProvider();
            var mw = new CrowdhandlerMiddleware(ctx => { ctx.Response.StatusCode = 201; return Task.CompletedTask; }, sp.GetRequiredService<IOptionsMonitor<CrowdhandlerOptions>>(), sp.GetRequiredService<ILoggerFactory>());

            await mw.InvokeAsync(Ctx.Build());

            var put = await WaitFor(() => requests.FirstOrDefault(r => r.Method == HttpMethod.Put));
            Assert.NotNull(put);
            Assert.Equal("/v1/responses/resp0000000000000000000000000001", put.RequestUri.AbsolutePath);
            string body = RecordingHandler.Bodies[put];
            Assert.Contains("\"httpCode\":201", body);
            Assert.Contains("\"sampleRate\":1", body);
            Assert.Contains("\"time\":", body);
        }

        private static async Task<T> WaitFor<T>(Func<T> probe) where T : class
        {
            for (int i = 0; i < 100; i++)
            {
                var v = probe();
                if (v != null) return v;
                await Task.Delay(20);
            }
            return null;
        }
    }

    public class RecordingHandler : HttpMessageHandler
    {
        public static readonly Dictionary<HttpRequestMessage, string> Bodies = new Dictionary<HttpRequestMessage, string>();
        private readonly List<HttpRequestMessage> _requests;
        public RecordingHandler(List<HttpRequestMessage> requests) { _requests = requests; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            Bodies[request] = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
            lock (_requests) _requests.Add(request);
            string path = request.RequestUri.AbsolutePath;
            string json = path.EndsWith("/v1/rooms")
                ? "{\"result\":[{\"id\":\"rom_1\",\"slug\":\"main\",\"urlPattern\":\"\",\"patternType\":\"all\",\"queueActivatesOn\":\"2024-01-01T00:00:00Z\",\"domain\":\"https://www.example.com\",\"safetyMode\":0,\"timeout\":15,\"stock\":null,\"checkout\":\"\",\"ttl\":59}]}"
                : path.Contains("/v1/requests")
                    ? "{\"result\":{\"status\":1,\"token\":\"tok0M7SBFAp9J8kK\",\"promoted\":1,\"slug\":\"main\",\"responseID\":\"resp0000000000000000000000000001\",\"requested\":\"2024-01-01T00:00:00Z\",\"hash\":null,\"ttl\":60}}"
                    : "{\"result\":{}}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    public class FilterTests
    {
        private static ActionExecutingContext ActionContext(HttpContext http)
        {
            var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
            return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object>(), controller: new object());
        }

        [Fact]
        public async Task Filter_ShortCircuitsWithRedirectResult()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "redirect", redirectUrl = "https://wait.test/r" } };
            var sp = Ctx.WithOptions(o => o.GatekeeperFactory = () => gk);
            var ctx = ActionContext(Ctx.Build(services: sp));
            bool nextCalled = false;
            var filter = new CrowdhandlerFilterAttribute();
            await filter.OnActionExecutionAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object())); });
            Assert.False(nextCalled);
            var redirect = Assert.IsType<RedirectResult>(ctx.Result);
            Assert.Equal("https://wait.test/r", redirect.Url);
            Assert.False(redirect.Permanent);
        }

        [Fact]
        public async Task Filter_AllowRunsAction_AndRecordsPerformanceAfterResult()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "allow", responseID = "r1" } };
            var sp = Ctx.WithOptions(o => o.GatekeeperFactory = () => gk);
            var http = Ctx.Build(services: sp);
            var ctx = ActionContext(http);
            bool nextCalled = false;
            var filter = new CrowdhandlerFilterAttribute();
            await filter.OnActionExecutionAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object())); });
            Assert.True(nextCalled);
            Assert.Null(ctx.Result);
            Assert.True(http.Items.ContainsKey("Crowdhandler.Performance"));

            filter.OnResultExecuted(new ResultExecutedContext(ctx, new List<IFilterMetadata>(), new EmptyResult(), new object()));
            Assert.False(http.Items.ContainsKey("Crowdhandler.Performance"));
        }

        [Fact]
        public async Task Filter_AttributePropertiesOverrideDiOptions_ButUnsetOnesFallBack()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => throw new CrowdhandlerApiException("down", 503) };
            var sp = Ctx.WithOptions(o => { o.GatekeeperFactory = () => gk; o.FailTrust = false; o.SafetyNetSlug = "from-di"; });
            var ctx = ActionContext(Ctx.Build(services: sp));

            // FailTrust not set on the attribute -> DI's false applies -> safety net redirect
            var filter = new CrowdhandlerFilterAttribute();
            await filter.OnActionExecutionAsync(ctx, () => Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object())));
            var redirect = Assert.IsType<RedirectResult>(ctx.Result);
            Assert.StartsWith("https://wait.test/from-di?", redirect.Url);

            // FailTrust explicitly true on the attribute wins over DI
            var ctx2 = ActionContext(Ctx.Build(services: sp));
            var filter2 = new CrowdhandlerFilterAttribute { FailTrust = true };
            await filter2.OnActionExecutionAsync(ctx2, () => Task.FromResult(new ActionExecutedContext(ctx2, new List<IFilterMetadata>(), new object())));
            Assert.Null(ctx2.Result);
        }

        private class CustomGatekeeperSubclass : CrowdhandlerFilterAttribute
        {
            public ScriptedGateKeeper Gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "redirect", redirectUrl = "https://wait.test/custom" } };
            protected override IGateKeeper getGatekeeper() => Gk;
            public override string getCookieValue(ActionExecutingContext filterContext) => "{\"from\":\"header\"}";
        }

        [Fact]
        public async Task Filter_HonoursGetGatekeeperAndGetCookieValueOverrides_OnCore()
        {
            // options are registered (as any real host will have) but must not displace the subclass's gatekeeper
            var sp = Ctx.WithOptions(o => { o.PublicApiKey = "x"; o.PrivateApiKey = "y"; });
            var ctx = ActionContext(Ctx.Build(services: sp, cookie: "{\"from\":\"cookie\"}"));
            var filter = new CustomGatekeeperSubclass();
            await filter.OnActionExecutionAsync(ctx, () => Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object())));
            Assert.Equal("https://wait.test/custom", Assert.IsType<RedirectResult>(ctx.Result).Url);
            Assert.Equal("{\"from\":\"header\"}", filter.Gk.Calls.Single().cookie);
        }

        [Fact]
        public async Task Filter_UsesCookieNameFromRegisteredOptions()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "allow", setCookie = true, cookieValue = "{}" } };
            var sp = Ctx.WithOptions(o => { o.GatekeeperFactory = () => gk; o.CookieName = "ch_session"; });
            var http = Ctx.Build(services: sp);
            http.Request.Headers["Cookie"] = "ch_session=%7B%22in%22%3A1%7D";
            var ctx = ActionContext(http);
            await new CrowdhandlerFilterAttribute().OnActionExecutionAsync(ctx, () => Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object())));
            Assert.Equal("{\"in\":1}", gk.Calls.Single().cookie);
            Assert.StartsWith("ch_session=", Ctx.SetCookieHeader(http));
        }

        public class LocalRoomsGateKeeper : GateKeeper
        {
            public LocalRoomsGateKeeper(string publicKey = null, string privateKey = null, string apiEndpoint = null, string waitingRoomEndpoint = null, string exclusions = null, string apiRequestTimeout = null, string roomCacheTTL = null, string safetyNetSlug = null)
                : base(publicKey, privateKey, apiEndpoint, waitingRoomEndpoint, exclusions, apiRequestTimeout, roomCacheTTL, safetyNetSlug) { }
            public override List<Crowdhandler.NETsdk.JSONTypes.RoomConfig> getRoomConfig() => new List<Crowdhandler.NETsdk.JSONTypes.RoomConfig>();
        }

        public class ParameterlessGateKeeper : GateKeeper { }

        [Fact]
        public void GatekeeperType_SubclassGetsConfiguredKeys_AndParameterlessFailsClearly()
        {
            var o = new CrowdhandlerOptions { PublicApiKey = "pub", PrivateApiKey = "priv", SafetyNetSlug = "s", GatekeeperType = typeof(LocalRoomsGateKeeper) };
            var gk = Assert.IsType<LocalRoomsGateKeeper>(o.CreateGateKeeper());
            Assert.Equal("pub", gk.PublicApiKey);
            Assert.Equal("s", gk.SafetyNetSlug);

            var bad = new CrowdhandlerOptions { PublicApiKey = "pub", PrivateApiKey = "priv", GatekeeperType = typeof(ParameterlessGateKeeper) };
            Assert.Throws<MissingFieldException>(() => bad.CreateGateKeeper()); // not a TargetInvocationException
        }

        [Fact]
        public async Task SafetyNetSlug_FallsBackToGatekeeper()
        {
            var options = new CrowdhandlerOptions { FailTrust = false };
            var throwing = new ThrowingGateKeeper("from-gatekeeper");
            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(Ctx.Build(), options, throwing, null);
            Assert.StartsWith("https://wait.test/from-gatekeeper?", outcome.RedirectUrl);
        }

        private class ThrowingGateKeeper : GateKeeper
        {
            public ThrowingGateKeeper(string slug) : base("pub", "priv", "https://api.test", "https://wait.test", null, "3", "0", slug) { }
            public override Task<ValidateResult> ValidateAsync(Uri url, string userAgent, string language, string ipAddress, string CookieJSON = "", Crowdhandler.NETsdk.JSONTypes.RoomConfig room = null, System.Threading.CancellationToken cancellationToken = default)
                => throw new CrowdhandlerApiException("down", 503);
        }

        private class LegacySubclass : CrowdhandlerFilterAttribute
        {
            public bool SyncCalled;
            public override void OnActionExecuting(ActionExecutingContext filterContext)
            {
                SyncCalled = true;
                if (filterContext.HttpContext.Request.Path.StartsWithSegments("/feed")) return;
                base.OnActionExecuting(filterContext);
            }
        }

        [Fact]
        public async Task Filter_HonoursSubclassSyncOverride()
        {
            var gk = new ScriptedGateKeeper { OnValidate = (u, ua, l, ip, c) => new GateKeeper.ValidateResult { Action = "redirect", redirectUrl = "https://wait.test/r" } };
            var sp = Ctx.WithOptions(o => o.GatekeeperFactory = () => gk);

            var feed = ActionContext(Ctx.Build(url: "https://www.example.com/feed/events", services: sp));
            var filter = new LegacySubclass();
            await filter.OnActionExecutionAsync(feed, () => Task.FromResult(new ActionExecutedContext(feed, new List<IFilterMetadata>(), new object())));
            Assert.True(filter.SyncCalled);
            Assert.Null(feed.Result);
            Assert.Empty(gk.Calls);

            var other = ActionContext(Ctx.Build(url: "https://www.example.com/tickets", services: sp));
            await filter.OnActionExecutionAsync(other, () => Task.FromResult(new ActionExecutedContext(other, new List<IFilterMetadata>(), new object())));
            Assert.IsType<RedirectResult>(other.Result);
        }
    }
}
