// A stand-in for api.crowdhandler.com for load and failure testing. Speaks the same wire shapes as the real API
// (see Crowdhandler.NETsdk/README.md, "Wire contract") and signs with the private key you give it, so the SDK's
// local validation works against it.
//
//   FAKE_PRIVATE_KEY=<key> FAKE_DOMAIN=https://localhost:5080 dotnet run --urls http://localhost:5090
//
// Knobs (query string on any call, or environment):  delay=<ms>  status=<http>  promoted=0|1  apistatus=<0..6>
//   FAKE_DELAY_MS, FAKE_HTTP_STATUS, FAKE_PROMOTED, FAKE_API_STATUS, FAKE_TIMEOUT_MINUTES (room timeout, default 20)

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

string privateKey = Environment.GetEnvironmentVariable("FAKE_PRIVATE_KEY") ?? "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
string domain = Environment.GetEnvironmentVariable("FAKE_DOMAIN") ?? "https://localhost:5080";
int timeoutMinutes = int.TryParse(Environment.GetEnvironmentVariable("FAKE_TIMEOUT_MINUTES"), out var tm) ? tm : 20;
const string slug = "fake-room";
const string queueActivatesOn = "2024-01-01T00:00:00Z";
long counters_requests = 0, counters_rooms = 0, counters_responses = 0;

string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
string Hash(string token, string requested) => Sha(Sha(privateKey) + slug + queueActivatesOn + token + requested);
string NewToken() => "tok0" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

async Task<bool> Knobs(HttpContext ctx)
{
    int delay = int.TryParse(ctx.Request.Query["delay"], out var d) ? d : int.TryParse(Environment.GetEnvironmentVariable("FAKE_DELAY_MS"), out d) ? d : 0;
    if (delay > 0) await Task.Delay(delay, ctx.RequestAborted);
    int status = int.TryParse(ctx.Request.Query["status"], out var s) ? s : int.TryParse(Environment.GetEnvironmentVariable("FAKE_HTTP_STATUS"), out s) ? s : 200;
    if (status != 200)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = "fake error " + status });
        return false;
    }
    return true;
}

app.MapGet("/v1/rooms", async ctx =>
{
    Interlocked.Increment(ref counters_rooms);
    if (!await Knobs(ctx)) return;
    await ctx.Response.WriteAsJsonAsync(new { result = new[] { new { id = "rom_fake", slug, urlPattern = "", patternType = "all", queueActivatesOn, domain, safetyMode = 0, timeout = timeoutMinutes, stock = (int?)null, checkout = "^/order/complete", ttl = 59 } } });
});

async Task Requests(HttpContext ctx, string? token)
{
    Interlocked.Increment(ref counters_requests);
    if (!await Knobs(ctx)) return;
    bool promoted = (ctx.Request.Query["promoted"].FirstOrDefault() ?? Environment.GetEnvironmentVariable("FAKE_PROMOTED") ?? "1") != "0";
    int apiStatus = int.TryParse(ctx.Request.Query["apistatus"], out var a) ? a : int.TryParse(Environment.GetEnvironmentVariable("FAKE_API_STATUS"), out a) ? a : 1;
    token = string.IsNullOrEmpty(token) || !token.StartsWith("tok") ? NewToken() : token;
    string requested = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    var result = new Dictionary<string, object?>
    {
        ["status"] = apiStatus, ["token"] = apiStatus == 5 ? null : token, ["title"] = "Fake room", ["position"] = promoted ? null : 42,
        ["promoted"] = promoted ? 1 : 0, ["urlRedirect"] = null, ["onsale"] = queueActivatesOn, ["message"] = null, ["slug"] = slug,
        ["priority"] = null, ["priorityAvailable"] = 0, ["logo"] = null, ["stock"] = null, ["responseID"] = Guid.NewGuid().ToString("N"),
        ["captchaRequired"] = 0, ["rate"] = 100, ["sessionsExpire"] = 0, ["sessionsTimeout"] = timeoutMinutes, ["deployment"] = ".net",
        ["domain"] = domain, ["emailAvailable"] = 0, ["requested"] = requested, ["hash"] = promoted ? Hash(token, requested) : null, ["ttl"] = 60,
    };
    await ctx.Response.WriteAsJsonAsync(new { result });
}
app.MapPost("/v1/requests/", ctx => Requests(ctx, null));
app.MapGet("/v1/requests/{token}", (HttpContext ctx, string token) => Requests(ctx, token));
app.MapPut("/v1/responses/{id}", async ctx => { Interlocked.Increment(ref counters_responses); if (!await Knobs(ctx)) return; await ctx.Response.WriteAsJsonAsync(new { result = new { updated = 1 } }); });
app.MapGet("/stats", () => Results.Json(new { rooms = counters_rooms, requests = counters_requests, responses = counters_responses }));
app.MapPost("/stats/reset", () => { counters_rooms = counters_requests = counters_responses = 0; return Results.Ok(); });

app.Run();
