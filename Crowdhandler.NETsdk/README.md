# CrowdHandler .NET SDK

The framework-agnostic core of the CrowdHandler .NET integration: room matching, signature verification, the session cookie and the API client.

If you are on ASP.NET MVC 5 or ASP.NET Core, use [`Crowdhandler.MVCSDK`](https://www.nuget.org/packages/Crowdhandler.MVCSDK/), which wraps this package in a middleware and an action filter. Use this package directly for other hosts: a custom HTTP handler, OWIN, or a non-web process that needs to validate CrowdHandler signatures.

Targets `netstandard2.0` and `net472` (.NET Framework 4.7.2 and later).

## Installation

```
dotnet add package Crowdhandler.NETsdk
```

## Usage

```csharp
using Crowdhandler.NETsdk;

var gk = new GateKeeper(publicKey, privateKey);

GateKeeper.ValidateResult result = gk.Validate(
    url:        new Uri("https://www.example.com/tickets?event=42"),
    userAgent:  request.UserAgent,
    language:   request.AcceptLanguage,
    ipAddress:  clientIp,                 // first hop of X-Forwarded-For when behind a proxy
    CookieJSON: request.Cookies["crowdhandler"]);

if (result.bustCookie == "busted")
    DeleteCookie("crowdhandler");
else if (result.setCookie)
    SetCookie("crowdhandler", result.cookieValue, path: "/", secure: true, httpOnly: false);

if (result.Action == "redirect")
{
    SetNoCacheHeaders();
    Redirect302(result.redirectUrl);
    return;
}

// serve the page, then optionally:
gk.RecordPerformance(result.responseID, statusCode, elapsedMilliseconds);
```

`ValidateAsync` is the asynchronous equivalent. Both throw `CrowdhandlerApiException` only for transient failures (API unreachable, 5xx, throttled). Apply your trust-on-fail policy there. Definitive rejections (4xx) come back as a redirect with `result.apiError` set.

## GateKeeper

### Constructor

```csharp
GateKeeper(string publicKey = null, string privateKey = null, string apiEndpoint = null, string waitingRoomEndpoint = null,
           string exclusions = null, string apiRequestTimeout = null, string roomCacheTTL = null, string safetyNetSlug = null)
```

Null arguments fall back to `ConfigurationManager.AppSettings` (`CROWDHANDLER_PUBLIC_KEY`, `CROWDHANDLER_PRIVATE_KEY`, `CROWDHANDLER_API_ENDPOINT`, `CROWDHANDLER_WR_ENDPOINT`, `CROWDHANDLER_EXCLUSIONS_REGEX`, `CROWDHANDLER_API_REQUEST_TIMEOUT`, `CROWDHANDLER_ROOM_CACHE_TIME`, `CROWDHANDLER_SAFETYNET_SLUG`) and then to defaults. Missing keys throw `MissingFieldException`.

### Properties

| Property | Default | Description |
|---|---|---|
| `PublicApiKey` | required | Sent to the API as `x-api-key`. |
| `PrivateApiKey` | required | Used only to verify signatures locally. |
| `ApiEndpoint` | `https://api.crowdhandler.com` | API base URL. |
| `WaitingRoomEndpoint` | `https://wait.crowdhandler.com` | Waiting room base URL. |
| `Exclusions` | static assets by extension (`GateKeeper.DefaultExclusions`) | Regex tested against path + query; matches bypass validation. |
| `APIRequestTimeout` | `"3"` | Seconds. |
| `RoomCacheTTL` | `"60"` | Seconds; `"0"` disables caching. |
| `SafetyNetSlug` | none | For hosts that implement trust-on-fail. |

### Validate / ValidateAsync

```csharp
ValidateResult Validate(Uri url, string userAgent, string language, string ipAddress, string CookieJSON = "", RoomConfig room = null)
Task<ValidateResult> ValidateAsync(Uri url, string userAgent, string language, string ipAddress, string CookieJSON = "", RoomConfig room = null, CancellationToken cancellationToken = default)
```

Pass `room` to skip room matching and validate against a known room.

`ValidateResult`:

| Field | Meaning |
|---|---|
| `Action` | `"allow"` or `"redirect"`. |
| `redirectUrl` | Waiting room URL, or the same URL with the `ch-*` parameters removed after a successful return from the waiting room. |
| `targetUrl` | The absolute URL that was validated. |
| `setCookie`, `cookieValue` | Whether and what to store in the `crowdhandler` cookie. |
| `bustCookie` | `"busted"` when the URL is a checkout-complete page: delete the cookie instead of setting it. |
| `token`, `slug`, `code` | The session token, matched room and priority code in play. |
| `expired` | A signature was present but older than the room's timeout. |
| `responseID` | Set when the API was called; pass to `RecordPerformance`. |
| `apiError` | Set when the API rejected the request (4xx). The visitor is redirected. Log this; it usually means bad keys. |

Order of operations:

1. Parse `ch-*` query parameters.
2. Parse the cookie.
3. Select the token (URL wins; must look like `tok...`).
4. Checkout busting.
5. Exclusions.
6. Room matching.
7. Local signature check (URL, then cookie history).
8. API call if needed.
9. Cookie.

### Signatures

A CrowdHandler signature is

```
sha256( sha256(privateKey) + room.slug + queueActivatesOn + token + requested )
```

with both timestamps formatted `yyyy-MM-ddTHH:mm:ssZ` (UTC, second precision), exactly as the API emits them. A signature is valid for `room.timeout` minutes from `requested`.

The cookie also stores `touched` (last-seen time, seconds since the epoch; milliseconds as written by the JavaScript SDK are also read) and `touchedSig = sha256(sha256(privateKey) + touched)`, so the session can roll forward without a new signature.

```csharp
ValidateSignatureResponse ValidateSignature(string signature, DateTime requested, string token, RoomConfig room)          // from URL parameters
ValidateSignatureResponse ValidateSignature(List<CookieSignature> candidates, CookieData cookie, string token, RoomConfig room) // from the cookie
```

Both return `{ success, expired }` and never throw on bad input.

### Cookie

`CookieData getCookieData(string json)` parses the cookie. Returns null for anything malformed. Accepts a bare `tok...` value as well as the JSON form:

```json
{"integration":"dotnet","tokens":[{"token":"tok0M7SBFAp9J8kK","touched":1717243200000,"touchedSig":"...","signatures":[{"gen":"2024-06-01T12:00:00Z","sig":"..."}]}]}
```

The shape is shared with the JavaScript SDK and edge integrations. `touched` is written in seconds; both seconds and milliseconds are read. The SDK keeps one signature per room (a newer signature for a room replaces the older one), up to ten rooms.

### Rooms

```csharp
List<RoomConfig> getRoomConfig()                                   // cached /v1/rooms; override to supply your own
RoomConfig IsRoomMatch(string host, string path)                    // MatchRoom over getRoomConfig()
RoomConfig MatchRoom(string host, string path, List<RoomConfig> rooms)
```

Rooms are tested in feed order; the first match wins. `domain` is compared to the request host ignoring scheme, port and a leading `www.`, and may contain a `*` wildcard (`https://*.example.com`). `patternType` is one of `regex`, `contains`, `regex-not`, `contains-not`, `all`, tested against the path and query string. Invalid patterns are skipped and logged.

If the feed cannot be refreshed, the last good copy is served. Regex matches are capped at 250 ms.

### Checkout busting

```csharp
string IsCheckoutBuster(string host, string path, List<RoomConfig> rooms)   // "busted" | "not-busted"
string CheckoutBuster(string host, string path, string targetUrl, string userAgent, string language, string ipAddress, string token)
```

When the URL matches a room's `checkout` regex, the API is told (best-effort) and `bustCookie` is `"busted"`. The host deletes the cookie and the visitor's next visit starts a new session.

### Performance reporting

```csharp
void RecordPerformance(string responseID, int httpStatusCode, long elapsedMilliseconds, double sampleRate = 0.2)
```

Reports origin timing to `/v1/responses/{responseID}` so CrowdHandler can autotune capacity. Sampled, fire-and-forget, never throws. At most 64 samples are in flight process-wide; the rest are dropped.

### Extension points

All of the above are `virtual`. `getConfigValue(name, required)` and `Log(message, exception)` are `protected virtual` for configuration and logging integration.

## CrowdhandlerApiException

Thrown by the API client.

| Member | Meaning |
|---|---|
| `StatusCode` | HTTP status code, or null when no response was received. |
| `IsClientError` | 4xx other than 429: a definitive rejection, never retried. |
| `IsTransient` | Everything else: 5xx, 429, timeouts, network errors, throttling. Retried once. |

## Wire contract

* `POST /v1/requests/` (form-encoded `url`, `agent`, `lang`, `ip`, `code`) creates a session; `GET /v1/requests/{token}?...` refreshes one. Empty values are omitted. Unknown tokens are replaced by the API; the SDK always adopts the returned token.
* `GET /v1/rooms` returns the room feed.
* `PUT /v1/responses/{responseID}` with `{httpCode, sampleRate, time}` records a performance sample.
* All calls send `x-api-key: <public key>`.

## License

BSD 3-Clause. See the LICENSE file for details.
