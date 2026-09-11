# Testing

Four layers, cheapest first. All run with `dotnet test Crowdhandler.NETsdk.sln`. The real-API layer skips itself when no credentials are configured.

| Layer | Project | What it covers | Needs |
|---|---|---|---|
| Unit | `tests/Crowdhandler.NETsdk.Tests` | Signature formula (pinned against an independent SHA-256 implementation), cookie round-trips, room matching, exclusions, fail-open regressions, API failure semantics. The API is a scripted in-process stub. | nothing |
| Adapter | `tests/Crowdhandler.MVCSDK.Tests` | ASP.NET Core middleware, filter and request processor: cookies, headers, redirects, trust-on-fail, DI option precedence, subclass overrides, performance sampling on the wire. | nothing |
| Real API | `tests/Crowdhandler.NETsdk.IntegrationTests` | The wire contract against a live CrowdHandler account: the hash the API issues validates with the SDK's formula, tokens are echoed and minted as expected, 404 on a bad key, empty `ip` accepted, performance PUT accepted, transient failures within the timeout budget. | credentials (below) |
| End to end | `samples/AspNetCoreSample` behind a tunnel | The whole visitor journey in a browser and with curl, including the hosted waiting room. | credentials, a public tunnel (for example `cloudflared`) |

## Credentials for the real-API and end-to-end layers

Use a test account. Store the values as user secrets. They are read by both the sample app and the integration tests; nothing is written to the repo.

```
cd samples/AspNetCoreSample
dotnet user-secrets set "Crowdhandler:PublicApiKey"        "<public key>"
dotnet user-secrets set "Crowdhandler:PrivateApiKey"       "<private key>"
dotnet user-secrets set "Crowdhandler:TestHost"            "<hostname of your tunnel or test site>"
```

`Crowdhandler:ApiEndpoint` and `Crowdhandler:WaitingRoomEndpoint` may also be set if you are testing against a non-production CrowdHandler environment. They default to `https://api.crowdhandler.com` and `https://wait.crowdhandler.com`.

The account needs a domain named `https://<TestHost>` with deployment type `.net`, a checkout regex of `^/order/complete`, and two rooms: one with pattern `all` that is live, and one with pattern `contains` `/queue` whose `queueActivatesOn` is in the future, so it always queues. All of this can be created through the private API (`POST /v1/domains`, `PUT /v1/domains/{id}`, `POST /v1/rooms` with `domainID`). The API 302s any `/v1/rooms/` with a trailing slash.

## End to end

```
cd samples/AspNetCoreSample
DOTNET_ROLL_FORWARD=LatestMajor ASPNETCORE_URLS=http://localhost:5080 dotnet run --no-launch-profile
cloudflared tunnel --url http://localhost:5080      # register the public hostname it prints as the domain
```

Then with curl (`-c jar -b jar` to carry the cookie):

| Request | Expect |
|---|---|
| `GET /tickets` | 200, `Set-Cookie: crowdhandler=...` containing a signature, log line `allow ... api=True` |
| `GET /tickets` again with cookie | 200, log line `api=False` (validated locally) |
| `GET /queue/x` | 302 to `<WaitingRoomEndpoint>/<queue slug>?url=...&ch-id=tok...`, `Cache-Control: no-store` |
| `GET /site.css?v=1` | no CrowdHandler involvement (`slug=-`) |
| `GET /order/complete?id=1` with cookie | `Set-Cookie: crowdhandler=; expires=1970...` and log `bust=busted` |
| `GET /tickets?ch-id=x&ch-id-signature=deadbeef&ch-requested=garbage` | 302 to the clean URL with a new cookie, never a 500 |
| `GET /ch/status` | `{"integration":"dotnet","status":"ok","version":"..."}` |
| with `CheckInIntervalMinutes` set, `GET /tickets` with a cookie older than the interval | 200, log line `allow-checkin ... api=True`, cookie gains a signature |

In a browser: set the domain `rate` to 0 via the API and open `/tickets`; you land in the waiting room. Set the rate back to 100 and the room promotes you back to `/tickets` with `ch-id`, `ch-id-signature` and `ch-requested` in the URL. The SDK validates the signature locally, stores it in the cookie and strips the parameters with a redirect.

Two behaviours of the hosted service to be aware of when testing manually:

* The hosted waiting room sends `ch-code=null` as a literal string. The SDK treats it as absent.
* `GET /v1/requests/{token}` is cached at CrowdHandler's edge for the response's `ttl`, keyed on the `url` value. A repeated probe must vary `url` or it reports the earlier state (for example, the pre-checkout state after a checkout bust).

## What has been verified

* The unit, adapter and real-API suites pass on the .NET 6, 8 and 10 runtimes; the `net472` build has been executed under Mono against a live account.
* The browser loop: domain rate set to 0, browser queued in the hosted waiting room, rate restored, room promoted the browser back with `ch-id` / `ch-id-signature` / `ch-requested`, signature validated locally, cookie written, parameters stripped.
* Check-ins: first visit calls the API, the next request is local, the request after the interval is `allow-checkin` with the same token and the room's signature refreshed in the cookie, then local again.
* Cross-room sessions: one visitor moving between a live `all` room, a live `contains /events` room and a countdown `/queue` room holds one token with one signature per room. Every contact appears in the token's request log on the API (`GET /v1/sessions/{token}`) with origin milliseconds and HTTP code, and sampled pages appear in the domain URL report.
* Checkout busting: with `destroySessionsOnCheckout` on and the domain checkout regex matching `/order/complete`, a buyer's visit to that page deleted their cookie and the API stopped recognising their token; a visitor who did not check out kept theirs.
* Session touch: with the domain timeout set to 2 minutes, an idle visitor's token was replaced after the timeout while an active visitor making a request every 20 seconds kept the same token across repeated check-ins.
* Configuration paths: singleton options from a configuration section (the sample); the per-request multi-tenant resolver (unit tests with three tenants, and end to end with one host validated and another host bypassed); attribute properties over DI options, `GatekeeperType` and `GatekeeperFactory` (unit tests); `Web.config` / `App.config` appSettings for every `CROWDHANDLER_*` key, including precedence of explicit arguments (`AppSettingsTests`); library mode (the real-API integration tests and `tools/NetFrameworkSmoke`).

## Load

`tools/FakeCrowdhandlerApi` stands in for the API so the SDK can be driven at high concurrency and through outage modes without an account. Recipe (see the tool's README for the options):

```
FAKE_PRIVATE_KEY=<64 hex> FAKE_DOMAIN=https://127.0.0.1 dotnet tools/FakeCrowdhandlerApi/bin/Release/net8.0/FakeCrowdhandlerApi.dll --urls http://127.0.0.1:5090
Crowdhandler__PublicApiKey=<64 chars> Crowdhandler__PrivateApiKey=<same 64 hex> Crowdhandler__ApiEndpoint=http://127.0.0.1:5090 \
  dotnet samples/AspNetCoreSample/bin/Release/net8.0/AspNetCoreSample.dll --urls http://127.0.0.1:5081
hey -n 3000 -c 100 http://127.0.0.1:5081/tickets                       # cold start, every request calls the API
hey -n 10000 -c 200 -H "Cookie: crowdhandler=<value from a first visit>" http://127.0.0.1:5081/tickets   # local validation
```

Then restart the fake API with `FAKE_HTTP_STATUS=503`, `FAKE_DELAY_MS=1500` or `FAKE_DELAY_MS=5000` and repeat, with and without the cookie.

Expected in every scenario:

* Never a 500.
* Cookie holders are unaffected by API failures. With check-ins due and the API hanging, only the first wave waits; check-ins are suspended after the first failure.
* Requests without a cookie are bounded by twice the API timeout (trust-on-fail after one retry).
* After the first failed room fetch on a cold cache, further attempts fail fast for 10 seconds.
* A cold cache under concurrent first visits performs a single room fetch.

## Not yet covered

* **ASP.NET MVC 5 on Windows.** The `net472` SDK build is exercised by `tools/NetFrameworkSmoke` against the real API. The MVC 5 filter itself needs a Windows machine: run it in IIS Express in both integrated and classic pipeline modes.
* **Linux.** The suites have not yet been run in a Linux container.
