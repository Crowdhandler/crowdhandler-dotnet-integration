# CrowdHandler for ASP.NET

The official [CrowdHandler](https://www.crowdhandler.com) virtual waiting room integration for ASP.NET Core and ASP.NET MVC 5.

* **ASP.NET Core 6, 8 and later**: middleware (`app.UseCrowdhandler()`) or action filter (`[CrowdhandlerFilter]`), configured from `appsettings.json`.
* **ASP.NET MVC 5 (.NET Framework 4.7.2 and later)**: action filter (`[CrowdhandlerFilter]`), configured from `Web.config`.

Both are built on [`Crowdhandler.NETsdk`](https://www.nuget.org/packages/Crowdhandler.NETsdk/), which performs the validation and can be used on its own in any .NET application.

Source, issues and changelog: [github.com/Crowdhandler/crowdhandler-dotnet-integration](https://github.com/Crowdhandler/crowdhandler-dotnet-integration)

## Installation

```
dotnet add package Crowdhandler.MVCSDK
```

On .NET Framework, use the Package Manager console: `Install-Package Crowdhandler.MVCSDK`.

Your API keys are in the CrowdHandler control panel under **Account > API**.

## Quick start

### ASP.NET Core

`appsettings.json`:

```json
{
  "Crowdhandler": {
    "PublicApiKey": "YOUR_PUBLIC_KEY",
    "PrivateApiKey": "YOUR_PRIVATE_KEY"
  }
}
```

Use user secrets or environment variables (`Crowdhandler__PrivateApiKey`) rather than committing the private key.

`Program.cs`:

```csharp
using Crowdhandler.MVCSDK.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCrowdhandler(builder.Configuration.GetSection("Crowdhandler"));

var app = builder.Build();

app.UseForwardedHeaders();   // if you are behind a proxy or load balancer
app.UseCrowdhandler();       // before static files, routing and endpoints
app.UseStaticFiles();
app.MapControllers();
app.Run();
```

The middleware validates every request, including Razor Pages, minimal APIs and static files. To exempt some requests entirely, pass a predicate:

```csharp
app.UseCrowdhandler(ctx => ctx.Request.Path.StartsWithSegments("/health")
                        || ctx.Request.Path.StartsWithSegments("/webhooks"));
```

For path-based exclusions the `Exclusions` regex (see [Configuration](#configuration)) is usually the better tool.

The middleware also answers `GET /ch/status` with `{"integration":"dotnet","status":"ok","version":"..."}` so CrowdHandler support can verify the integration is live. Change or disable the path with `CrowdhandlerMiddlewareOptions.StatusPath`:

```csharp
app.UseMiddleware<CrowdhandlerMiddleware>(new CrowdhandlerMiddlewareOptions { StatusPath = null });
```

#### Protecting individual actions instead

To protect specific controllers or actions only, register the options as above and apply the attribute instead of the middleware:

```csharp
using Crowdhandler.MVCSDK;

public class TicketsController : Controller
{
    [CrowdhandlerFilter]
    public IActionResult Buy(int eventId) => View();
}
```

Attribute properties override the registered options for that action, for example `[CrowdhandlerFilter(FailTrust = false)]`. Without `AddCrowdhandler`, the attribute falls back to its own properties and then to `CROWDHANDLER_*` appSettings in `app.config`.

### ASP.NET MVC 5

`Web.config`:

```xml
<appSettings>
  <add key="CROWDHANDLER_PUBLIC_KEY" value="YOUR_PUBLIC_KEY" />
  <add key="CROWDHANDLER_PRIVATE_KEY" value="YOUR_PRIVATE_KEY" />
</appSettings>
```

Apply the filter to the actions you want to protect:

```csharp
using Crowdhandler.MVCSDK;

public class TicketsController : Controller
{
    [CrowdhandlerFilter]
    public ActionResult Buy(int eventId)
    {
        return View();
    }
}
```

To protect every action, register it globally in `FilterConfig.cs`:

```csharp
filters.Add(new CrowdhandlerFilterAttribute());
```

### Finish the setup (both frameworks)

1. Add the [CrowdHandler JavaScript integration](https://www.crowdhandler.com/docs) to your pages. Check-ins keep sessions alive and report timings server-side (see [Check-ins](#check-ins)); the JavaScript additionally covers visitors who stay on one page without making requests, and adds client-side page timing.
2. In the CrowdHandler control panel, set the deployment type for your domain to **.NET**.

## How it works

1. A visitor requests a page. The SDK matches the URL against the waiting rooms configured for your domain. The room list is fetched from the CrowdHandler API and cached for 60 seconds.
2. If the visitor already carries a valid CrowdHandler signature (in the `crowdhandler` cookie, or in the URL on their way back from a waiting room), it is verified locally with your private key and the request proceeds. No API call is made.
3. Otherwise the SDK asks the CrowdHandler API whether the visitor may proceed. If not, they are redirected to the waiting room and returned to the same URL when it is their turn.
4. Static assets (by extension) are excluded from all of this by default.

Only the public key is sent to the API. The private key stays on your server and is used only to verify signatures.

## Configuration

Every setting can be given three ways. Precedence: attribute property, then `appsettings.json` / `AddCrowdhandler` (ASP.NET Core) or `Web.config` appSettings, then the default.

| Setting (`appsettings.json` / `CrowdhandlerOptions`) | Attribute property | `Web.config` key | Default | Description |
|---|---|---|---|---|
| `PublicApiKey` | `PublicApiKey` | `CROWDHANDLER_PUBLIC_KEY` | required | Your public API key. |
| `PrivateApiKey` | `PrivateApiKey` | `CROWDHANDLER_PRIVATE_KEY` | required | Your private API key. Never leaves the server. |
| `ApiEndpoint` | `ApiEndpoint` | `CROWDHANDLER_API_ENDPOINT` | `https://api.crowdhandler.com` | API base URL. |
| `WaitingRoomEndpoint` | `WaitingRoomEndpoint` | `CROWDHANDLER_WR_ENDPOINT` | `https://wait.crowdhandler.com` | Waiting room base URL. |
| `Exclusions` | `Exclusions` | `CROWDHANDLER_EXCLUSIONS_REGEX` | static asset extensions (see below) | Regex tested against the path and query string. Matches are never validated. |
| `ApiRequestTimeoutSeconds` | `APIRequestTimeout` | `CROWDHANDLER_API_REQUEST_TIMEOUT` | `3` | Seconds to wait for the API. Transient failures are retried once, so the worst case is double this. |
| `RoomCacheSeconds` | `RoomCacheTTL` | `CROWDHANDLER_ROOM_CACHE_TIME` | `60` | Seconds to cache the room configuration. `0` disables caching. |
| `FailTrust` | `FailTrust` | none | `true` | What to do when the API is unreachable. See [Failure behaviour](#failure-behaviour). |
| `SafetyNetSlug` | `SafetyNetSlug` | `CROWDHANDLER_SAFETYNET_SLUG` | none | Waiting room used when `FailTrust` is false and the API is unreachable. |
| `DebugMode` | `DebugMode` | none | `false` | Rethrow validation errors instead of applying `FailTrust`. Local development only. |
| `CookieName` | override `getCookieName()` | none | `crowdhandler` | Session cookie name. Only change it if you change it in the control panel too. |
| `CookieDomain` | `CookieDomain` | none | host-only | Set to `.example.com` to share the session across subdomains. |
| `CookieSecure` | none | none | `true` on HTTPS | Force the `Secure` attribute. |
| `CookieMaxAgeSeconds` | none | none | session | Persist the cookie for this long. |
| `ClientIpHeader` | `ClientIpHeader` | none | `X-Forwarded-For` | Header carrying the original client IP behind a proxy. The first address is used. |
| `PerformanceSampleRate` | `PerformanceSampleRate` | none | `0.2` | Fraction of first-visit responses whose timing is reported to CrowdHandler for capacity autotuning. `0` disables all reporting. |
| `CheckInIntervalMinutes` | `CheckInIntervalMinutes` | `CROWDHANDLER_CHECK_IN_INTERVAL` | `2` | Periodic API check-in for visitors validated locally. `0` disables. See [Check-ins](#check-ins). |
| `GatekeeperType` | `GatekeeperType` | none | `GateKeeper` | A custom `IGateKeeper` type to instantiate. |
| `GatekeeperFactory` | none | none | none | A factory for the `IGateKeeper` to use (code only). |

### Exclusions

The default `Exclusions` pattern matches static assets by path extension, with or without a cache-busting query string:

```
^[^?]*\.(3gp|7z|avi|avif|bmp|css|csv|doc|docx|eot|flac|gif|gz|ico|ics|jpeg|jpg|js|json|jsonld|map|mid|mjs|mov|mp3|mp4|mpeg|mpg|odt|og[gv]|otf|pdf|png|ppt|pptx|rar|rtf|svg|tar|tif|tiff|tsv|ttf|txt|wav|webm|webp|wmv|woff|woff2|xls|xlsx|xml|zip)(\?.*)?$
```

To exclude additional paths, add alternatives in front of the asset pattern. The default value is available in code as `GateKeeper.DefaultExclusions`:

```
^(/contact-us.*)|(/api/.*)|(^[^?]*\.(css|js|png|jpg|gif|svg|ico|woff2?)(\?.*)?$)
```

An invalid regex is logged once and treated as "nothing excluded".

### Behind a proxy or load balancer

CrowdHandler rate-limits and fingerprints by visitor IP, so the SDK must see the real client address. By default it takes the first address in `X-Forwarded-For`, falling back to the socket address. If your edge uses a different header (Cloudflare: `CF-Connecting-IP`, Akamai: `True-Client-IP`), set `ClientIpHeader`. On ASP.NET Core, `UseForwardedHeaders` before `UseCrowdhandler` also works and additionally fixes the scheme, which matters for the `Secure` cookie flag.

`X-Forwarded-For` can be spoofed by clients unless your edge overwrites it. Make sure your proxy does.

## Failure behaviour

| Situation | What happens |
|---|---|
| Visitor has a valid signature | Allowed, no API call. |
| API says not promoted | Redirected to the waiting room. |
| API rejects the request (HTTP 4xx: invalid key, bad parameters), on the session call or the room feed | Redirected to the waiting room and an error is logged. `FailTrust` does **not** apply: this is a configuration problem, not an outage. |
| API unreachable, times out, returns 5xx or is throttling (429 / status 6) | `FailTrust = true` (default): the visitor is let through. `FailTrust = false`: the visitor is redirected to the `SafetyNetSlug` waiting room. |
| Room configuration cannot be refreshed | The last successfully fetched copy is used. Visitors with valid signatures are still validated locally. |
| Malformed or tampered cookie or URL parameters | Treated as "no signature": the visitor goes through the normal flow. Never throws, never lets anyone through. |
| Missing or invalid configuration (no keys, unknown gatekeeper type) | Throws on every request. An invalid `Exclusions` regex is logged and ignored instead. |

Read more about [Trust on Fail](https://www.crowdhandler.com/docs/80000984411-trust-on-fail).

## Check-ins

Once a visitor is through, every request is validated from the cookie. Without check-ins, CrowdHandler hears nothing more about them: it cannot count them as active, their session expires on its side after the room timeout, and their page timings are never measured. The JavaScript integration normally covers this. Check-ins cover it server-side and are on by default:

```json
"Crowdhandler": { "CheckInIntervalMinutes": 2 }
```

Every N minutes (jittered ±25% per visitor), one of the visitor's page requests also asks the API whether they are still promoted before being served. This adds 100 to 300 ms to one request every N minutes; every other request stays local. The check-in:

* keeps the session alive on CrowdHandler's side;
* reports a timing sample for that page (always, regardless of `PerformanceSampleRate`, with the check-in's own duration subtracted);
* adopts a new token silently if the old one had expired;
* sends the visitor to the waiting room if the API says they are no longer promoted.

Behaviour to be aware of:

* A check-in that fails (API down, throttled, error) is skipped and the visitor keeps their locally validated session. It is never a trust-on-fail event. After any transient API failure, check-ins are suspended for 10 seconds so an outage does not slow visitors who are already through.
* The cadence is per visitor, not per room. A visitor holds one token that can be valid in several rooms (with a room-scoped signature for each), and the API refreshes every room's session for the token on any request. One check-in from whichever page they are on keeps all their rooms alive.
* A visitor idle on a single page makes no requests, so no check-ins. Raise the domain timeout in the control panel for that case.
* A check-in can legitimately come back "not promoted": the session expired while the visitor was idle, the room was switched into countdown mode, or the room's URL boundary was changed so the page now belongs to a busier room. In each case the visitor is sent to the waiting room. This gives operators a way to reach visitors who are already through, within about N minutes.
* Load is one API call per active visitor per N minutes on your public key. The edge integrations call the API on every page view, so this is considerably lower. Under throttling (HTTP 429 or status 6) check-ins are skipped and the visitor keeps their session.

## Logging

**ASP.NET Core:** the middleware and filter log through `ILogger` under the category `Crowdhandler`. Every decision is logged at *Debug* level with the action, room slug, token, where the token came from (`param`, `cookie` or `new`) and whether the API was called. Turn on `"Crowdhandler": "Debug"` when investigating a looping visitor.

**MVC 5:** decisions go to `System.Diagnostics.Trace` at information level and errors at error level. Override `LogError` on the attribute to route them elsewhere.

Rejections by the API (typically wrong keys) are logged at *Error* level. Watch for `CrowdHandler API rejected the request`.

### Caching and CDNs

Responses that set the session cookie are marked `Cache-Control: private` unless the application has set its own cache headers. Waiting-room redirects are `no-store`.

## Advanced

### Multi-tenant platforms

If one application serves many sites, each tenant has its own CrowdHandler account and keys, and some tenants may not use CrowdHandler at all. Register a per-request resolver instead of fixed options:

```csharp
builder.Services.AddCrowdhandler(async ctx =>
{
    var tenant = await tenants.FindByHostAsync(ctx.Request.Host.Host);
    if (tenant?.QueueProvider != "crowdhandler")
    {
        return null;                       // not a CrowdHandler tenant: the request passes straight through
    }
    return tenant.CrowdhandlerOptions;     // a CrowdhandlerOptions instance; cache and reuse it per tenant
});
app.UseCrowdhandler();
```

Both the middleware and `[CrowdhandlerFilter]` use the resolver. Returned options may be cached per tenant and shared across requests; the SDK never modifies them. Room configuration, failure backoff and check-in suspension are all tracked per public key, so tenants are isolated from each other's rooms and outages. One HTTP connection pool is shared. Log lines carry the first characters of the public key so tenants can be told apart.

Cookies are host-scoped, so tenants on their own hostnames are isolated automatically. If tenants share a hostname under path-based routing, give each a distinct `CookieName`, and never set `CookieDomain` to the platform's apex domain.

### Library mode

If your application already has its own abstraction for queueing providers (a validate call that returns "allow" or "redirect here"), call the core directly and apply the result yourself:

```csharp
using Crowdhandler.NETsdk;

var gk = new GateKeeper(tenant.PublicKey, tenant.PrivateKey);          // cheap; safe to cache per tenant
var result = await gk.ValidateAsync(url, userAgent, acceptLanguage, clientIp, cookieValue);

if (result.bustCookie == "busted") DeleteCookie("crowdhandler");
else if (result.setCookie)         SetCookie("crowdhandler", result.cookieValue);   // Path=/, Secure, not HttpOnly

if (result.Action == "redirect")   return Redirect302(result.redirectUrl);          // with no-store cache headers

// serve the page, then optionally:
gk.RecordPerformance(result.responseID, statusCode, originMilliseconds, result.checkIn ? 1.0 : 0.2);
```

`ValidateAsync` throws `CrowdhandlerApiException` only for transient API failures. Apply your trust-on-fail policy there. One level up, `CrowdhandlerRequestProcessor.HandleAsync(httpContext, options, gk, logger)` does the cookie and header work for ASP.NET Core and returns a redirect URL or null.

See the [`Crowdhandler.NETsdk` README](https://github.com/Crowdhandler/crowdhandler-dotnet-integration/blob/main/Crowdhandler.NETsdk/README.md) for the full API.

### Skip validation for some requests (ASP.NET Core)

```csharp
app.UseCrowdhandler(ctx => ctx.Request.Method == "OPTIONS" || ctx.Request.Path.StartsWithSegments("/internal"));
```

### Subclass the filter (both frameworks)

```csharp
public class MyCrowdhandlerFilterAttribute : CrowdhandlerFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext filterContext)
    {
        // do not validate the JSON events feed
        var path = filterContext.HttpContext.Request.Path;          // ASP.NET Core
        // var path = filterContext.HttpContext.Request.Url.AbsolutePath;   // ASP.NET MVC 5
        if (path.ToString().StartsWith("/events/feed"))
        {
            return;
        }
        base.OnActionExecuting(filterContext);
    }
}
```

### Supply the room configuration yourself

By default rooms are fetched from the API and cached. To use a local copy (for example in an environment with no outbound access from the web tier), extend `GateKeeper`:

```csharp
using System.Collections.Generic;
using System.IO;
using Crowdhandler.NETsdk;
using Crowdhandler.NETsdk.JSONTypes;
using Newtonsoft.Json;

public class LocalRoomsGateKeeper : GateKeeper
{
    // Expose the base constructor so the configured keys reach the gatekeeper. This is required on ASP.NET Core,
    // where there are no CROWDHANDLER_* appSettings for the parameterless constructor to fall back to.
    public LocalRoomsGateKeeper(string publicKey = null, string privateKey = null, string apiEndpoint = null, string waitingRoomEndpoint = null,
                                string exclusions = null, string apiRequestTimeout = null, string roomCacheTTL = null, string safetyNetSlug = null)
        : base(publicKey, privateKey, apiEndpoint, waitingRoomEndpoint, exclusions, apiRequestTimeout, roomCacheTTL, safetyNetSlug) { }

    public override List<RoomConfig> getRoomConfig()
    {
        return JsonConvert.DeserializeObject<List<RoomConfig>>(File.ReadAllText("rooms.json"));
    }
}
```

Register it with `o.GatekeeperType = typeof(LocalRoomsGateKeeper)` (ASP.NET Core options), `[CrowdhandlerFilter(GatekeeperType = typeof(LocalRoomsGateKeeper))]` (either framework), or build it yourself with `o.GatekeeperFactory = () => new LocalRoomsGateKeeper(pub, priv)`. The JSON is the `result` array of `GET /v1/rooms`.

### Change how the client IP is determined (MVC 5)

Override `getIpAddress(ActionExecutingContext)` on the attribute.

## Troubleshooting

* **Every visitor is sent to the waiting room and the log says the API rejected the request.** The public key is wrong or belongs to a different account. Check **Account > API** in the control panel.
* **Visitors loop between the site and the waiting room.** The cookie is not being stored. Check that the cookie domain matches the site, that the site is served over HTTPS (or set `CookieSecure = false` for local HTTP), and, on ASP.NET Core, that no cookie-consent middleware strips it (the SDK marks it essential).
* **Signatures never validate locally and every request calls the API.** The private key does not match the account the public key belongs to.
* **Visitors are re-queued after a few minutes.** The room's session timeout has passed with no keep-alive. Install the JavaScript integration.
* **The waiting room is bypassed on some URLs.** They match the `Exclusions` regex, or no room's URL pattern matches them. Check the room's *URL pattern* in the control panel.

## Support

* [Knowledge Base](https://www.crowdhandler.com/support)
* [API Documentation](https://admin.crowdhandler.com/account/api)
* [Email Support](mailto:support@crowdhandler.com)
* [Report Issues](https://github.com/Crowdhandler/crowdhandler-dotnet-integration/issues)

## License

BSD 3-Clause. See the LICENSE file for details.
