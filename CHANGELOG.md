# Changelog

All notable changes to `Crowdhandler.NETsdk` and `Crowdhandler.MVCSDK`. Both packages share a version number.

## 1.1.0 (2026-09-10)

No public API was removed. See *Compatibility* for the behavioural changes to be aware of.

### Security

* **Tampered input no longer fails open.** A request with an unparseable `ch-requested`, or a cookie with `null` tokens or signatures, threw inside validation and was let through under `FailTrust` (the default). All client-controlled input is now validated and falls through to the normal queue.
* **API rejections no longer fail open.** A 4xx from the API (typically an invalid key) was swallowed by `FailTrust`, silently disabling the waiting room. It now sends the visitor to the waiting room and logs an error. `FailTrust` applies only to outages (5xx, 429, timeouts, network).
* A rejected room-configuration request (4xx, typically a wrong or rotated public key) is treated like a rejected session request: the visitor is sent to the waiting room and the error is logged, instead of falling open under `FailTrust`.
* Tokens from the URL or cookie must look like CrowdHandler tokens (`tok...`) before they are used, and are percent-encoded in API paths.
* Signature comparisons are constant-time.
* SHA-256 uses a FIPS-compliant implementation on .NET Framework.

### Fixed

* `GET /v1/requests/{token}` sent `url`, `agent`, `lang` and `ip` unencoded, so any target URL with a query string was truncated at the API and could match the wrong room.
* An empty `ip` was sent to the API, which rejects it with HTTP 400. Empty values are now omitted.
* `ch-code` (priority / access codes) was stripped from the URL but never sent to the API, so codes had no effect. Codes are now forwarded; a rejected code is retried without it rather than locking the visitor out.
* Query strings containing `=` inside a value (`?next=a=b`) were corrupted in the post-validation redirect.
* Non-default ports were dropped from the target and redirect URLs.
* `ch-*` parameters were only recognised in lower case.
* `favicon.ico` was never excluded: the default exclusions listed `ICO` in upper case.
* `queueActivatesOn` and `gen` timestamps without a zone designator were interpreted in the server's local time zone, breaking signature validation on non-UTC hosts. All timestamps are now treated as UTC and formatted with an invariant culture.
* `regex-not` and `contains-not` room pattern types were treated as "never match".
* Room domains with a `*` wildcard, or differing from the request host only by `www.` or port, did not match. Matching now mirrors the API's own rules.
* The MVC filter set a 302 status and `Location` header but let the controller action run to completion (including on POST actions). Redirects now short-circuit with a `RedirectResult`.
* On ASP.NET Core the filter used `Headers.Add`, which throws if the header already exists (for example with `[ResponseCache]`), producing a 500.
* On ASP.NET Core the checkout-busting cookie delete omitted `Path=/`, so the cookie was not reliably removed.
* `Response.Headers` on MVC 5 is unavailable in IIS classic pipeline mode; cache headers now go through `Response.Cache`.
* A zero or negative `APIRequestTimeout` crashed `HttpClient` construction.
* 4xx responses were retried; retries are now limited to transient failures.
* `RemoteIpAddress` being null (some hosting scenarios, tests) threw before validation started.
* The `safetyNetSlug` constructor argument of `GateKeeper` was accepted and ignored.
* TLS configuration on .NET Framework is applied once, guarded, instead of on every request.

### Added

* **Multi-tenant hosts:** `services.AddCrowdhandler(HttpContext => options)` resolves settings per request (null bypasses CrowdHandler for that tenant). Both the middleware and the filter use it. Shared caches are sized for many tenants and log lines carry a key prefix.
* **ASP.NET Core middleware and options:** `services.AddCrowdhandler(Configuration.GetSection("Crowdhandler"))` + `app.UseCrowdhandler()`, with `CrowdhandlerOptions` bindable from `appsettings.json`. The filter attribute also reads these options, so keys no longer need to be hard-coded in attributes on ASP.NET Core.
* `GateKeeper.ValidateAsync`. The ASP.NET Core middleware and filter are fully asynchronous.
* **Performance reporting** to `/v1/responses/{responseID}` (sampled, fire-and-forget), which CrowdHandler uses for capacity autotuning.
* **Periodic check-ins** (`CheckInIntervalMinutes`, default 2, `0` disables): locally validated visitors are re-checked with the API every N minutes so their session stays alive server-side and their page timings are reported, without the JavaScript integration. Operator changes (countdown mode, room boundaries) now reach visitors who are already through. See the README.
* Room configuration is cached as parsed objects and the last good copy is served if a refresh fails, so an API blip does not disable local validation.
* Room configuration is fetched asynchronously with a single in-flight refresh that concurrent requests await rather than block on. After a failed room fetch with nothing to fall back on, further attempts are skipped for 10 seconds. Check-ins are suspended for 10 seconds after any transient API failure so an outage does not slow visitors who are already validated.
* Timing samples measure the origin only: the timer starts after validation, so the SDK's own API calls (session check, room refresh, check-in) are never counted.
* Compiled regular expressions are cached and capped at 250 ms per match.
* Cookie attributes: `Secure` (on HTTPS), `SameSite=Lax`, `IsEssential`, optional `Domain` and `Max-Age`. Values are percent-encoded on MVC 5 as they already were on ASP.NET Core.
* Bare-token cookies (as written by the CrowdHandler client-side script on CDN deployments) are accepted.
* Client IP extraction strips ports and IPv4-mapped IPv6 prefixes and is configurable via `ClientIpHeader`; overridable on MVC 5 via `getIpAddress`.
* `CrowdhandlerApiException` with `StatusCode`, `IsClientError` and `IsTransient`.
* `ValidateResult` gained `slug`, `responseID` and `apiError`.
* `TokenResponse` exposes the remaining API fields (`title`, `position`, `sessionsTimeout`, `deployment`, `domain`, `captchaRequired`, `ttl`); `RoomConfig` gained `id` and `stock`; `CookieData` round-trips `deployment`.
* Signature history in the cookie holds one signature per room (a refreshed signature replaces its predecessor) and is capped at 10 rooms, keeping the cookie well under browser limits.
* Ported from the edge integrations: a `/ch/status` verification route on the middleware, an `x-request-source` header on API calls, per-decision logging, a wider default static-asset list that also matches cache-busted URLs, a byte-size backstop on the cookie, a one-hour limit on serving stale room configuration, and `Cache-Control: private` on responses that set the cookie.
* XML documentation, SourceLink and symbol packages.
* Unit, adapter and live-API test suites, a GitHub Actions workflow, and an ASP.NET Core sample application. See TESTING.md.

### Compatibility

* **Both packages now target `net472` instead of `net45`** for .NET Framework. Applications on 4.5 through 4.7.1 can no longer install this version; 4.7.2, 4.8 and 4.8.1 applications are unaffected. TLS 1.2 (and 1.3 where the runtime supports it) is still enabled once, guarded, because the framework's TLS default follows the *application's* `httpRuntime` target rather than the library's.
* **`Crowdhandler.MVCSDK` now targets `net472`, `net6.0` and `net8.0`** (was `net45`, `net5.0`). The `net5.0` build referenced the out-of-support `Microsoft.AspNetCore.Mvc.Core 2.2.5`; ASP.NET Core builds now reference the shared framework. Applications on .NET 6 or 7 use the `net6.0` build; .NET 8 and later use `net8.0`.
* Cookie `touched` is still written in seconds, as 1.0.x did, so a 1.0.x node in a mixed farm can read 1.1 cookies during a rolling upgrade. Cookies written in milliseconds by the JavaScript SDK are also read.
* Dependencies: `Newtonsoft.Json` 13.0.4; `System.Configuration.ConfigurationManager` and `System.Runtime.Caching` 8.0.1 on `netstandard2.0`. The `System.Net.Http` package reference was dropped in favour of the framework assembly.
* The default `Exclusions` pattern now excludes assets with a query string (`/app.js?v=3`) and covers about 50 extensions (documents, archives and media included). Set `Exclusions = GateKeeper.LegacyDefaultExclusions` to keep the 1.0.x pattern. An invalid custom pattern is logged and ignored rather than thrown.
* `IGateKeeper` is unchanged. The 1.0.x eight-parameter `GateKeeper` constructor is retained (binary compatible); a ninth optional parameter, `checkInIntervalMinutes`, was added on a new overload. `CrowdhandlerFilterAttribute.getCookieValue` / `setCookieValue` remain overridable on both frameworks, and a subclass overriding the synchronous `Validate` is honoured on ASP.NET Core too.
* API failures now surface as `CrowdhandlerApiException` (was a raw `HttpRequestException`, `TaskCanceledException` or `AggregateException`). Code that caught those specifically should catch `CrowdhandlerApiException`.
* On ASP.NET Core, a `GatekeeperType` that derives from `GateKeeper` should expose the base constructor (see the README) so it receives the configured keys. A parameterless subclass would look for `CROWDHANDLER_*` appSettings, which ASP.NET Core applications do not have. `GatekeeperFactory` avoids the question entirely.

## 1.0.11 (2026-03-03)

* Fix constructor parameter ordering in `CrowdhandlerFilterAttribute`.
* Fix `HttpRequestMessage` reuse in the retry loop (retries always failed).
* Fix the static `HttpClient` pool being replaced on every request.
* Reduce retries to one with a 6 s worst case.
* Handle malformed cookies and invalid room regexes without crashing.
* Check HTTP status codes on API responses.

## 1.0.10 and earlier

See the git history.
