// Minimal ASP.NET Core application protected by CrowdHandler.
//
//   dotnet user-secrets set "Crowdhandler:PublicApiKey"  "<your public key>"
//   dotnet user-secrets set "Crowdhandler:PrivateApiKey" "<your private key>"
//   dotnet run
//
// Then create a waiting room for https://localhost:<port> in the CrowdHandler control panel (or set the domain's
// deployment type to .NET) and open https://localhost:<port>/tickets.

using Crowdhandler.MVCSDK;
using Crowdhandler.MVCSDK.AspNetCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// All CrowdHandler settings live under the "Crowdhandler" section of appsettings.json / user secrets / environment.
if (builder.Configuration.GetValue<bool>("Crowdhandler:MultiTenant"))
{
    // Multi-tenant shape: settings resolved per request from the host name. Here a single tenant (TestHost) is
    // CrowdHandler-protected and every other host passes straight through.
    var tenant = builder.Configuration.GetSection("Crowdhandler").Get<CrowdhandlerOptions>() ?? new CrowdhandlerOptions();
    var tenantHost = builder.Configuration["Crowdhandler:TestHost"] ?? "localhost";
    builder.Services.AddCrowdhandler(ctx => string.Equals(ctx.Request.Host.Host, tenantHost, StringComparison.OrdinalIgnoreCase) ? tenant : null);
}
else
{
    builder.Services.AddCrowdhandler(builder.Configuration.GetSection("Crowdhandler"));
}

// If you run behind a proxy or load balancer, let ASP.NET Core see the original scheme and client IP first.
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

app.UseForwardedHeaders();

// Option A (recommended): the middleware validates every request. Health checks are skipped entirely.
app.UseCrowdhandler(ctx => ctx.Request.Path.StartsWithSegments("/health"));

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/health", () => Results.Ok("ok"));

app.MapGet("/", () => Results.Content(
    "<h1>CrowdHandler ASP.NET Core sample</h1>" +
    "<p>Every page on this site is protected by the middleware. <a href=\"/tickets\">Buy tickets</a> &middot; <a href=\"/tickets/filtered\">Filter-protected action</a></p>",
    "text/html"));

app.MapControllers();

app.Run();

// Option B: protect individual actions with the filter. Because the options are registered with AddCrowdhandler,
// the attribute needs no properties. (With the middleware in place this is redundant; shown for completeness.)
[Route("tickets")]
public class TicketsController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => Content("<h1>Tickets</h1><p>You are through the waiting room.</p>", "text/html");

    [HttpGet("filtered")]
    [CrowdhandlerFilter(FailTrust = false)]
    public IActionResult Filtered() => Content("<h1>Tickets (filter)</h1><p>Validated by the action filter, with trust-on-fail disabled for this action.</p>", "text/html");
}
