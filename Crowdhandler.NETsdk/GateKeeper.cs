using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Crowdhandler.NETsdk.JSONTypes;
using Newtonsoft.Json;

namespace Crowdhandler.NETsdk
{
    /// <summary>
    /// The core of the CrowdHandler .NET SDK: decides, for one request, whether the visitor may proceed or must be sent
    /// to a waiting room. Framework-agnostic; the MVC filter and ASP.NET Core middleware are thin adapters over it.
    /// </summary>
    /// <remarks>
    /// Validation is "hybrid": a signature issued by CrowdHandler (in the URL after a waiting-room visit, then in the
    /// <c>crowdhandler</c> cookie) is verified locally with the private key, and the API is only called when there is
    /// no valid signature. Room configuration is fetched from <c>/v1/rooms</c> and cached.
    /// <para>
    /// Failure semantics: malformed or tampered input from the visitor (URL parameters, cookie) never throws — it is
    /// treated as "not validated" and the visitor goes through the normal queue. A definitive rejection by the API
    /// (4xx) is returned as a redirect to the waiting room. Only transport failures and API outages (5xx, 429,
    /// timeouts) throw <see cref="CrowdhandlerApiException"/>, so that the caller's trust-on-fail policy applies
    /// exclusively to infrastructure problems.
    /// </para>
    /// </remarks>
    public class GateKeeper : IGateKeeper
    {
        /// <summary>The value written to the cookie's <c>integration</c> field.</summary>
        public const string IntegrationName = "dotnet";

        /// <summary>Default exclusion pattern: static assets by path extension are never queued, with or without a cache-busting query string.</summary>
        public const string DefaultExclusions = @"^[^?]*\.(3gp|7z|avi|avif|bmp|css|csv|doc|docx|eot|flac|gif|gz|ico|ics|jpeg|jpg|js|json|jsonld|map|mid|mjs|mov|mp3|mp4|mpeg|mpg|odt|og[gv]|otf|pdf|png|ppt|pptx|rar|rtf|svg|tar|tif|tiff|tsv|ttf|txt|wav|webm|webp|wmv|woff|woff2|xls|xlsx|xml|zip)(\?.*)?$";

        /// <summary>The 1.0.x default exclusion pattern, for consumers who want the previous behaviour (fewer extensions, no query strings): <c>Exclusions = GateKeeper.LegacyDefaultExclusions</c>.</summary>
        public const string LegacyDefaultExclusions = @"^((?!.*\?).*(\.(avi|css|eot|gif|ico|jpg|jpeg|js|json|mov|mp4|mpeg|mpg|og[g|v]|pdf|png|svg|ttf|txt|wmv|woff|woff2|xml)))$";

        /// <summary>Signature history kept per token in the cookie: one per room, so this is the number of rooms a visitor can hold a local session in. Older entries are dropped first.</summary>
        public const int MaxStoredSignatures = 10;

        /// <summary>Backstop on the serialised cookie: above this the history is cut to the newest signature. Browsers drop cookies over 4 KB, and the failure mode is a silent waiting-room loop.</summary>
        public const int MaxCookieBytes = 2048;

        /// <summary>Upper bound on a single regex match, so a pathological pattern from configuration cannot stall a request thread.</summary>
        private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

        private static readonly string[] CrowdhandlerQueryParams = { "ch-code", "ch-fresh", "ch-id", "ch-id-signature", "ch-public-key", "ch-requested" };

        private static readonly ConcurrentDictionary<string, Regex> RegexCache = new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, bool> InvalidPatternsReported = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        private const int RegexCacheLimit = 4096; // sized for many tenants x many rooms in one process

        /// <summary>Crowdhandler API URL</summary>
        virtual public string ApiEndpoint { get; set; }

        /// <summary>Crowdhandler public API Key</summary>
        virtual public string PublicApiKey { get; set; }

        /// <summary>Crowdhandler private API Key</summary>
        virtual public string PrivateApiKey { get; set; }

        /// <summary>Crowdhandler Waiting room URL</summary>
        virtual public string WaitingRoomEndpoint { get; set; }

        /// <summary>Regular expression matched against the request path and query; matching URLs bypass validation entirely.</summary>
        virtual public String Exclusions { get; set; }

        /// <summary>Crowdhandler API Request Timeout in Seconds</summary>
        virtual public String APIRequestTimeout { get; set; }

        /// <summary>Crowdhandler RoomCache TTL in seconds (0 disables caching)</summary>
        virtual public String RoomCacheTTL { get; set; }

        /// <summary>Waiting room slug to send visitors to when the API cannot be reached and trust-on-fail is disabled.</summary>
        virtual public String SafetyNetSlug { get; set; }

        /// <summary>
        /// How often a locally validated visitor is re-checked with the API so their session stays alive server-side and
        /// their page timings are reported. Default 2 minutes; zero disables check-ins. Applied with a per-token jitter of ±25%.
        /// Config key <c>CROWDHANDLER_CHECK_IN_INTERVAL</c> (minutes).
        /// </summary>
        virtual public TimeSpan CheckInInterval { get; set; }

        /// <summary>Default check-in interval.</summary>
        public static readonly TimeSpan DefaultCheckInInterval = TimeSpan.FromMinutes(2);

        /// <summary>1.0.x constructor, kept so assemblies compiled against 1.0.x keep working.</summary>
        public GateKeeper(String publicKey, String privateKey, String apiEndpoint, String waitingRoomEndpoint, String exclusions, String apiRequestTimeout, String roomCacheTTL, String safetyNetSlug)
            : this(publicKey, privateKey, apiEndpoint, waitingRoomEndpoint, exclusions, apiRequestTimeout, roomCacheTTL, safetyNetSlug, null)
        {
        }

        public GateKeeper(String publicKey = null, String privateKey = null, String apiEndpoint = null, String waitingRoomEndpoint = null, String exclusions = null, String apiRequestTimeout = null, String roomCacheTTL = null, String safetyNetSlug = null, String checkInIntervalMinutes = null)
        {
            this.PublicApiKey = publicKey ?? this.getConfigValue("CROWDHANDLER_PUBLIC_KEY", true);
            this.PrivateApiKey = privateKey ?? this.getConfigValue("CROWDHANDLER_PRIVATE_KEY", true);
            this.ApiEndpoint = apiEndpoint ?? this.getConfigValue("CROWDHANDLER_API_ENDPOINT", false) ?? "https://api.crowdhandler.com";
            this.WaitingRoomEndpoint = waitingRoomEndpoint ?? this.getConfigValue("CROWDHANDLER_WR_ENDPOINT", false) ?? "https://wait.crowdhandler.com";
            this.Exclusions = exclusions ?? this.getConfigValue("CROWDHANDLER_EXCLUSIONS_REGEX", false) ?? DefaultExclusions;
            this.APIRequestTimeout = apiRequestTimeout ?? this.getConfigValue("CROWDHANDLER_API_REQUEST_TIMEOUT", false) ?? ApiClient.DefaultApiRequestTimeoutSeconds.ToString();
            this.RoomCacheTTL = roomCacheTTL ?? this.getConfigValue("CROWDHANDLER_ROOM_CACHE_TIME", false) ?? ApiClient.DefaultRoomCacheSeconds.ToString();
            this.SafetyNetSlug = safetyNetSlug ?? this.getConfigValue("CROWDHANDLER_SAFETYNET_SLUG", false);
            string interval = checkInIntervalMinutes ?? this.getConfigValue("CROWDHANDLER_CHECK_IN_INTERVAL", false);
            if (interval == null)
            {
                this.CheckInInterval = DefaultCheckInInterval;
            }
            else
            {
                this.CheckInInterval = double.TryParse(interval, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minutes) && minutes > 0
                    ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero;
            }
        }

        /// <summary>The outcome of validating one request.</summary>
        public struct ValidateResult
        {
            /// <summary>"allow" or "redirect".</summary>
            public string Action { get; set; }
            /// <summary>Where to send the visitor when <see cref="Action"/> is "redirect".</summary>
            public string redirectUrl { get; set; }
            /// <summary>The absolute URL that was validated.</summary>
            public string targetUrl { get; set; }
            /// <summary>"busted" when the request hit a checkout-complete page and the session cookie should be deleted; otherwise "not-busted".</summary>
            public string bustCookie { get; set; }
            /// <summary>Whether <see cref="cookieValue"/> should be written to the <c>crowdhandler</c> cookie.</summary>
            public bool setCookie { get; set; }
            /// <summary>JSON to store in the cookie.</summary>
            public string cookieValue { get; set; }
            /// <summary>The <c>ch-code</c> (priority code) seen on the request, if any.</summary>
            public string code { get; set; }
            /// <summary>The session token in play after validation.</summary>
            public string token { get; set; }
            /// <summary>True when a signature was present but had expired.</summary>
            public bool expired { get; set; }
            /// <summary>The waiting room slug the visitor was matched to, if any.</summary>
            public string slug { get; set; }
            /// <summary>Set when the API was called during validation; pass to <see cref="RecordPerformance"/> after the response is produced.</summary>
            public string responseID { get; set; }
            /// <summary>Set when the API definitively rejected the request (4xx). The visitor is redirected; log this loudly, it usually means misconfiguration.</summary>
            public CrowdhandlerApiException apiError { get; set; }
            /// <summary>True when this request performed a periodic check-in with the API (see <see cref="CheckInInterval"/>). Timing samples for check-ins are not subject to the sample rate.</summary>
            public bool checkIn { get; set; }
            /// <summary>Time spent on the check-in call, to subtract from the origin timing sample.</summary>
            public long checkInMilliseconds { get; set; }
        }

        /// <summary>
        /// Validate a request against the account's waiting rooms.
        /// </summary>
        /// <param name="url">The absolute URL requested.</param>
        /// <param name="userAgent">The visitor's User-Agent header.</param>
        /// <param name="language">The visitor's Accept-Language header.</param>
        /// <param name="ipAddress">The visitor's IP address (first hop of X-Forwarded-For when behind a proxy).</param>
        /// <param name="CookieJSON">The current value of the <c>crowdhandler</c> cookie, or empty. Malformed values are ignored.</param>
        /// <param name="room">A room to validate against; when null, the room is matched from the cached <c>/v1/rooms</c> feed.</param>
        /// <returns>A <see cref="ValidateResult"/> describing what to do with the request.</returns>
        /// <exception cref="CrowdhandlerApiException">The CrowdHandler API could not be reached or returned a server error (transient failure).</exception>
        public virtual ValidateResult Validate(Uri url, String userAgent, String language, String ipAddress, String CookieJSON = "", RoomConfig room = null)
        {
            return ApiClient.RunSync(() => ValidateAsync(url, userAgent, language, ipAddress, CookieJSON, room, CancellationToken.None));
        }

        /// <summary>Asynchronous form of <see cref="Validate"/>. Prefer this from ASP.NET Core.</summary>
        public virtual async Task<ValidateResult> ValidateAsync(Uri url, String userAgent, String language, String ipAddress, String CookieJSON = "", RoomConfig room = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (url == null)
            {
                throw new ArgumentNullException(nameof(url));
            }

            /*
             After a waiting-room visit the URL looks like:
             https://www.example.com/?ch-id=tok0M7SBFAp9J8kK&ch-id-signature=73264cf4…&ch-requested=2022-07-27T11%3A16%3A13Z&ch-code=&ch-fresh=true
             */

            DateTime requestStartTime = DateTime.UtcNow;

            // Step 1: parse the URL and pull out the CrowdHandler query params
            String authority = url.GetLeftPart(UriPartial.Authority);
            String targetUrl = authority + url.PathAndQuery;
            string cleanedUrl = targetUrl;
            bool redirectToCleanUrl = false;

            String chCode = "";
            String chId = "";
            String chIdSignature = "";
            String chRequestedStr = "";

            if (!string.IsNullOrEmpty(url.Query) && url.Query != "?")
            {
                // Work on the raw segments so the clean URL preserves the original encoding byte-for-byte.
                string[] segments = url.Query.Substring(1).Split('&');
                var remaining = new List<string>(segments.Length);

                foreach (string segment in segments)
                {
                    int eq = segment.IndexOf('=');
                    string key = (eq < 0 ? segment : segment.Substring(0, eq)).ToLowerInvariant();
                    if (Array.IndexOf(CrowdhandlerQueryParams, key) < 0)
                    {
                        remaining.Add(segment);
                        continue;
                    }
                    string value = SanitiseQueryValue(eq < 0 ? "" : segment.Substring(eq + 1));
                    switch (key)
                    {
                        case "ch-code": chCode = value; break;
                        case "ch-id": chId = value; break;
                        case "ch-id-signature": chIdSignature = value; break;
                        case "ch-requested": chRequestedStr = value; break;
                    }
                }

                if (remaining.Count < segments.Length)
                {
                    // We removed CrowdHandler params: once validated, send the visitor to the clean URL so it is never bookmarked or shared with a token in it.
                    redirectToCleanUrl = true;
                    string cleanQuery = string.Join("&", remaining);
                    cleanedUrl = authority + url.AbsolutePath + (cleanQuery.Length > 0 ? "?" + cleanQuery : "");
                }
            }

            // Step 2: cookie
            CookieData cookieData = this.getCookieData(CookieJSON);
            CookieToken activeCookieToken = LastToken(cookieData);

            // Step 3: pick the token: URL wins over cookie. Anything that does not look like a token is ignored.
            String token = "";
            string tokenSource = "new";
            if (Util.IsValidToken(chId))
            {
                token = chId;
                tokenSource = "param";
            }
            else if (activeCookieToken != null && Util.IsValidToken(activeCookieToken.token))
            {
                token = activeCookieToken.token;
                tokenSource = "cookie";
            }

            // Step 4: checkout busting — the visitor completed a purchase, so end their session. Runs before the
            // exclusions so a confirmation endpoint that also matches the exclusions regex still ends the session.
            // Best-effort: a rooms-feed failure here must not turn an excluded asset request into an error.
            // Rooms are fetched once, asynchronously: nothing on this path blocks a thread waiting for the API.
            // A subclass overriding getRoomConfig / IsRoomMatch / CheckoutBuster keeps its behaviour via the sync virtuals.
            List<RoomConfig> rooms = null;
            bool useSyncOverrides = OverridesRoomLookup;
            if (!useSyncOverrides)
            {
                try
                {
                    rooms = await GetApiClient().GetRoomConfigAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (CrowdhandlerApiException ex)
                {
                    if (IsExcluded(url.PathAndQuery))
                    {
                        Log("Checkout-bust check skipped: room configuration unavailable", ex);
                        return new ValidateResult { Action = "allow", targetUrl = targetUrl, bustCookie = "not-busted", token = token, code = chCode };
                    }
                    if (ex.IsClientError)
                    {
                        return RoomsRejected(ex, targetUrl, token, chCode);
                    }
                    throw;
                }
            }

            string checkoutBusted;
            try
            {
                checkoutBusted = useSyncOverrides
                    ? this.CheckoutBuster(url.Host, url.PathAndQuery, targetUrl, userAgent, language, ipAddress, token)
                    : await CheckoutBustAsync(rooms, url.Host, url.PathAndQuery, targetUrl, userAgent, language, ipAddress, token, cancellationToken).ConfigureAwait(false);
            }
            catch (CrowdhandlerApiException ex)
            {
                Log("Checkout-bust check skipped: room configuration unavailable", ex);
                checkoutBusted = "not-busted";
            }

            // Step 5: excluded URLs (static assets by default) are never validated and never get a cookie
            if (IsExcluded(url.PathAndQuery))
            {
                return new ValidateResult { Action = "allow", targetUrl = targetUrl, bustCookie = checkoutBusted, token = token, code = chCode };
            }

            // Step 6: which room, if any, protects this URL?
            if (room == null)
            {
                try
                {
                    room = useSyncOverrides ? this.IsRoomMatch(url.Host, url.PathAndQuery) : MatchRoom(url.Host, url.PathAndQuery, rooms);
                }
                catch (CrowdhandlerApiException ex) when (ex.IsClientError)
                {
                    return RoomsRejected(ex, targetUrl, token, chCode);
                }
            }
            if (room == null)
            {
                LogDecision("allow-no-room", null, token, tokenSource, false);
                return new ValidateResult { Action = "allow", targetUrl = targetUrl, bustCookie = checkoutBusted, token = token, code = chCode };
            }

            // Step 7: validate the signature we have (URL first, then cookie history)
            String exactSignature = "";
            DateTime? requestedUtc = null;
            ValidateSignatureResponse sigResponse = new ValidateSignatureResponse { expired = false, success = false };

            if (!string.IsNullOrEmpty(chIdSignature) && token != "")
            {
                if (Util.TryParseUtc(chRequestedStr, out DateTime parsedRequested))
                {
                    exactSignature = chIdSignature;
                    requestedUtc = parsedRequested;
                    sigResponse = this.ValidateSignature(chIdSignature, parsedRequested, token, room);
                }
                // else: a signature without a parseable ch-requested cannot be valid — treat as unsigned, do not throw
            }
            else if (activeCookieToken != null && activeCookieToken.token == token && activeCookieToken.signatures != null && activeCookieToken.signatures.Count > 0)
            {
                sigResponse = this.ValidateSignature(activeCookieToken.signatures, cookieData, token, room);
            }

            string responseID = null;

            // Step 7a: no valid signature — ask the API
            if (!sigResponse.success)
            {
                TokenResponse api;
                try
                {
                    api = await GetTokenFromApiAsync(targetUrl, userAgent, language, ipAddress, token, chCode, cancellationToken).ConfigureAwait(false);
                }
                catch (CrowdhandlerApiException ex) when (ex.IsClientError)
                {
                    // The API understood and refused the request (bad key, bad parameters). Never trust; send to the waiting room.
                    Log("CrowdHandler API rejected the request; sending visitor to the waiting room", ex);
                    return new ValidateResult
                    {
                        Action = "redirect",
                        redirectUrl = BuildWaitingRoomUrl(room.Slug, targetUrl, chCode, token),
                        targetUrl = targetUrl,
                        token = token,
                        code = chCode,
                        slug = room.Slug,
                        expired = sigResponse.expired,
                        bustCookie = checkoutBusted,
                        apiError = ex,
                    };
                }

                responseID = api.responseID;

                if (!api.promoted)
                {
                    LogDecision("redirect-queue", api.slug ?? room.Slug, api.token, tokenSource, true);
                    return new ValidateResult
                    {
                        Action = "redirect",
                        redirectUrl = BuildWaitingRoomUrl(api.slug ?? room.Slug, targetUrl, chCode, api.token),
                        targetUrl = targetUrl,
                        token = api.token,
                        code = chCode,
                        slug = api.slug ?? room.Slug,
                        expired = sigResponse.expired,
                        bustCookie = checkoutBusted,
                        responseID = responseID,
                    };
                }

                // Promoted. Adopt whatever token the API settled on (it mints a new one for unknown/expired tokens).
                if (Util.IsValidToken(api.token))
                {
                    token = api.token;
                }
                exactSignature = api.hash ?? "";
                requestedUtc = api.requested;
            }

            // Step 7a': periodic check-in. The visitor is validated locally, but if their newest signature (any room) is older
            // than the interval, refresh the session with the API in the request path so a token change can be applied to the
            // cookie. The cadence is per visitor, not per room: the API refreshes every room's slot for the token on any
            // request, so one check-in from whichever page they are on keeps all their rooms alive. Any API failure is
            // skipped: the local signature is still valid.
            bool checkedIn = false;
            long checkInMs = 0;
            if (sigResponse.success && sigResponse.matched != null && IsCheckInDue(NewestSignature(activeCookieToken), token))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                TokenResponse api = null;
                try
                {
                    api = await GetTokenFromApiAsync(targetUrl, userAgent, language, ipAddress, token, chCode, cancellationToken).ConfigureAwait(false);
                }
                catch (CrowdhandlerApiException ex)
                {
                    Log("Check-in skipped; visitor keeps their locally validated session", ex);
                }
                checkInMs = sw.ElapsedMilliseconds;

                if (api != null)
                {
                    checkedIn = true;
                    responseID = api.responseID;
                    if (!api.promoted)
                    {
                        LogDecision("redirect-checkin", api.slug ?? room.Slug, api.token, tokenSource, true);
                        return new ValidateResult
                        {
                            Action = "redirect",
                            redirectUrl = BuildWaitingRoomUrl(api.slug ?? room.Slug, targetUrl, chCode, api.token),
                            targetUrl = targetUrl,
                            token = api.token,
                            code = chCode,
                            slug = api.slug ?? room.Slug,
                            bustCookie = checkoutBusted,
                            responseID = responseID,
                            checkIn = true,
                            checkInMilliseconds = checkInMs,
                        };
                    }
                    if (Util.IsValidToken(api.token))
                    {
                        token = api.token;
                    }
                    exactSignature = api.hash ?? "";
                    requestedUtc = api.requested;
                }
            }

            // Step 7b: validated — write a fresh cookie carrying the token and its signature history

            var newCookie = new CookieData { integration = IntegrationName, deployment = cookieData?.deployment, tokens = new List<CookieToken>() };

            bool isNewToken = activeCookieToken == null || activeCookieToken.token != token;
            if (!isNewToken && cookieData?.tokens != null)
            {
                newCookie.tokens.AddRange(cookieData.tokens.Where(t => t != null));
            }

            // Seconds, as 1.0.x wrote them: a 1.0.x node in a mixed farm can still read this cookie during a rolling upgrade.
            // Both seconds and milliseconds (the JS SDK's unit) are accepted when reading.
            ulong touched = Util.DateTimeToUnixTimeStamp(requestStartTime);
            string touchedSig = Util.SHA256Hash(Util.SHA256Hash(this.PrivateApiKey) + touched);

            CookieToken current;
            if (isNewToken || newCookie.tokens.Count == 0)
            {
                current = new CookieToken { token = token, signatures = new List<CookieSignature>() };
                newCookie.tokens = new List<CookieToken> { current };
            }
            else
            {
                current = newCookie.tokens.Last();
                current.signatures = (current.signatures ?? new List<CookieSignature>()).Where(s => s != null && !string.IsNullOrEmpty(s.sig)).ToList();
            }
            current.touched = touched;
            current.touchedSig = touchedSig;

            if (exactSignature != "" && requestedUtc.HasValue && !current.signatures.Any(s => s.sig == exactSignature))
            {
                // A signature is only ever used for the room that issued it, and session expiry is judged on `touched`, so an
                // older signature for the same room is dead weight: drop it. The history is then one signature per room.
                string hashedKey = Util.SHA256Hash(this.PrivateApiKey);
                string roomActive = Util.FormatUtc(room.queueActivatesOn);
                current.signatures.RemoveAll(s => Util.FixedTimeEquals(s.sig, Util.SHA256Hash($"{hashedKey}{room.Slug}{roomActive}{token}{Util.FormatUtc(s.gen)}")));
                current.signatures.Add(new CookieSignature { gen = requestedUtc.Value, sig = exactSignature });
                if (current.signatures.Count > MaxStoredSignatures)
                {
                    current.signatures.RemoveRange(0, current.signatures.Count - MaxStoredSignatures);
                }
            }

            var cookieStr = JsonConvert.SerializeObject(newCookie);
            if (cookieStr.Length > MaxCookieBytes && current.signatures.Count > 1)
            {
                current.signatures = new List<CookieSignature> { current.signatures.Last() };
                cookieStr = JsonConvert.SerializeObject(newCookie);
            }

            LogDecision(redirectToCleanUrl ? "redirect-clean" : checkedIn ? "allow-checkin" : "allow", room.Slug, token, tokenSource, responseID != null);

            if (redirectToCleanUrl)
            {
                return new ValidateResult
                {
                    Action = "redirect",
                    redirectUrl = cleanedUrl,
                    targetUrl = targetUrl,
                    bustCookie = checkoutBusted,
                    setCookie = true,
                    cookieValue = cookieStr,
                    token = token,
                    code = chCode,
                    slug = room.Slug,
                    responseID = responseID,
                    checkIn = checkedIn,
                    checkInMilliseconds = checkInMs,
                };
            }

            return new ValidateResult
            {
                Action = "allow",
                targetUrl = targetUrl,
                bustCookie = checkoutBusted,
                setCookie = true,
                cookieValue = cookieStr,
                token = token,
                code = chCode,
                slug = room.Slug,
                responseID = responseID,
                checkIn = checkedIn,
                checkInMilliseconds = checkInMs,
            };
        }

        /// <summary>
        /// Report origin response time for a validated request. CrowdHandler uses these samples to autotune capacity.
        /// Fire-and-forget and sampled; safe to call on every request. No-op when <paramref name="responseID"/> is null.
        /// </summary>
        /// <param name="responseID">From <see cref="ValidateResult.responseID"/>.</param>
        /// <param name="httpStatusCode">The status code the application returned.</param>
        /// <param name="elapsedMilliseconds">Time from request start to response.</param>
        /// <param name="sampleRate">Fraction of requests to report, 0 to 1. Default 0.2.</param>
        public virtual void RecordPerformance(string responseID, int httpStatusCode, long elapsedMilliseconds, double sampleRate = 0.2)
        {
            if (string.IsNullOrEmpty(responseID) || sampleRate <= 0)
            {
                return;
            }
            if (sampleRate < 1 && ThreadSafeRandom.NextDouble() >= sampleRate)
            {
                return;
            }
            int multiplier = Math.Max(1, (int)Math.Round(1 / sampleRate));
            GetApiClient().recordPerformance(responseID, httpStatusCode, elapsedMilliseconds, multiplier);
        }

        public struct ValidateSignatureResponse
        {
            public Boolean success;
            public Boolean expired;
            /// <summary>On success from the cookie path, the stored signature that matched.</summary>
            public CookieSignature matched;
            /// <summary>On success from the cookie path, whether <see cref="matched"/> is the most recently issued signature in the cookie.</summary>
            public Boolean isNewestSignature;
        }

        /// <summary>
        /// Validate using the signature history stored in the cookie. Succeeds if any stored signature was issued for this
        /// token and room, the token was touched within the room's timeout, and the touch timestamp is authentic.
        /// </summary>
        public virtual ValidateSignatureResponse ValidateSignature(List<CookieSignature> CandidateSignatures, CookieData cookie, String token, RoomConfig room)
        {
            var failed = new ValidateSignatureResponse { success = false, expired = false };
            if (CandidateSignatures == null || CandidateSignatures.Count == 0 || string.IsNullOrEmpty(token) || room == null)
            {
                return failed;
            }
            CookieToken activeCookie = LastToken(cookie);
            if (activeCookie == null)
            {
                return failed;
            }

            String hashedPrivateKey = Util.SHA256Hash(this.PrivateApiKey);
            String roomActiveDateFormatted = Util.FormatUtc(room.queueActivatesOn);

            // Newest first: the most recent signature is the likeliest match.
            var candidates = CandidateSignatures.Where(c => c != null && !string.IsNullOrEmpty(c.sig)).Reverse().ToList();

            foreach (var candidate in candidates)
            {
                String expected = Util.SHA256Hash($"{hashedPrivateKey}{room.Slug}{roomActiveDateFormatted}{token}{Util.FormatUtc(candidate.gen)}");
                if (!candidates.Any(c => Util.FixedTimeEquals(c.sig, expected)))
                {
                    continue;
                }

                // A signature we issued. Now check the session is still live and the touch timestamp was not tampered with.
                String expectedTouchedSig = Util.SHA256Hash($"{hashedPrivateKey}{activeCookie.touched}");
                if (!Util.FixedTimeEquals(expectedTouchedSig, activeCookie.touchedSig) || !Util.TryTouchedToDateTime(activeCookie.touched, out DateTime touchedAt))
                {
                    return new ValidateSignatureResponse { success = false, expired = true };
                }
                double minsSince = (DateTime.UtcNow - touchedAt).TotalMinutes;
                if (minsSince < room.timeout)
                {
                    return new ValidateSignatureResponse { success = true, expired = false, matched = candidate, isNewestSignature = ReferenceEquals(candidate, candidates[0]) };
                }
                return new ValidateSignatureResponse { success = false, expired = true };
            }

            return failed;
        }

        /// <summary>
        /// Validate a signature supplied in the URL after a waiting-room visit.
        /// </summary>
        public virtual ValidateSignatureResponse ValidateSignature(String Signature, DateTime requested, String token, RoomConfig room)
        {
            if (string.IsNullOrEmpty(Signature) || string.IsNullOrEmpty(token) || room == null)
            {
                return new ValidateSignatureResponse { success = false, expired = false };
            }

            String hashedPrivateKey = Util.SHA256Hash(this.PrivateApiKey);

            // Both timestamps must be formatted exactly as the API formats them, in UTC, or the hash will never match.
            String requestDateFormatted = Util.FormatUtc(requested);
            String roomActiveDateFormatted = Util.FormatUtc(room.queueActivatesOn);
            String requiredHash = Util.SHA256Hash($"{hashedPrivateKey}{room.Slug}{roomActiveDateFormatted}{token}{requestDateFormatted}");

            if (!Util.FixedTimeEquals(requiredHash, Signature))
            {
                return new ValidateSignatureResponse { success = false, expired = false };
            }

            double minsSince = (DateTime.UtcNow - (requested.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(requested, DateTimeKind.Utc) : requested.ToUniversalTime())).TotalMinutes;
            if (minsSince < room.timeout)
            {
                return new ValidateSignatureResponse { success = true, expired = false };
            }
            return new ValidateSignatureResponse { success = false, expired = true };
        }

        /// <summary>
        /// Parse the <c>crowdhandler</c> cookie. Returns null for missing, malformed or structurally invalid values;
        /// a bare token (as written by CrowdHandler's client-side script on CDN deployments) is accepted.
        /// </summary>
        public virtual CookieData getCookieData(String JSONCookieData)
        {
            if (string.IsNullOrWhiteSpace(JSONCookieData))
            {
                return null;
            }
            string value = JSONCookieData.Trim();
            if (Util.IsValidToken(value))
            {
                return new CookieData { integration = IntegrationName, tokens = new List<CookieToken> { new CookieToken { token = value, signatures = new List<CookieSignature>() } } };
            }
            try
            {
                var data = JsonConvert.DeserializeObject<CookieData>(value);
                if (data?.tokens == null)
                {
                    return null;
                }
                data.tokens = data.tokens.Where(t => t != null && !string.IsNullOrEmpty(t.token)).ToList();
                return data.tokens.Count == 0 ? null : data;
            }
            catch (Exception)
            {
                // Malformed or tampered cookie: behave as if there were none.
                return null;
            }
        }

        /// <summary>
        /// Find the first room whose domain and URL pattern match. Rooms are tested in feed order, which the API sorts so
        /// that specific patterns come before catch-alls.
        /// </summary>
        /// <param name="host">Hostname of the request (no scheme or port).</param>
        /// <param name="path">URL path and query string.</param>
        /// <param name="rooms">Room configuration, normally from <see cref="getRoomConfig"/>.</param>
        /// <returns>The first matched room, or null.</returns>
        public virtual RoomConfig MatchRoom(string host, string path, List<RoomConfig> rooms)
        {
            if (rooms == null || string.IsNullOrEmpty(host))
            {
                return null;
            }
            path = path ?? "";

            foreach (RoomConfig room in rooms)
            {
                if (room == null || !DomainMatches(room.domain, host))
                {
                    continue;
                }

                bool matched;
                switch ((room.patternType ?? "").ToLowerInvariant())
                {
                    case "regex":
                        matched = !string.IsNullOrEmpty(room.urlPattern) && SafeIsMatch(room.urlPattern, path);
                        break;
                    case "regex-not":
                        matched = !string.IsNullOrEmpty(room.urlPattern) && !SafeIsMatch(room.urlPattern, path);
                        break;
                    case "contains":
                        matched = !string.IsNullOrEmpty(room.urlPattern) && path.Contains(room.urlPattern);
                        break;
                    case "contains-not":
                        matched = !string.IsNullOrEmpty(room.urlPattern) && !path.Contains(room.urlPattern);
                        break;
                    case "all":
                        matched = true;
                        break;
                    default:
                        matched = false;
                        break;
                }

                if (matched)
                {
                    return room;
                }
            }
            return null;
        }

        /// <summary>
        /// Whether the request is for a checkout-complete page of any room on this host.
        /// </summary>
        /// <param name="host">Hostname of the request.</param>
        /// <param name="path">URL path and query string.</param>
        /// <param name="rooms">Room configuration.</param>
        /// <returns>"busted" if a checkout pattern matched, otherwise "not-busted".</returns>
        public virtual string IsCheckoutBuster(string host, string path, List<RoomConfig> rooms)
        {
            if (rooms == null || string.IsNullOrEmpty(host))
            {
                return "not-busted";
            }
            foreach (RoomConfig room in rooms)
            {
                if (room == null || string.IsNullOrEmpty(room.checkout) || !DomainMatches(room.domain, host))
                {
                    continue;
                }
                if (SafeIsMatch(room.checkout, path ?? ""))
                {
                    return "busted";
                }
            }
            return "not-busted";
        }

        /// <summary>Match the request against the rooms fetched from the API.</summary>
        /// <param name="host">Hostname of the request.</param>
        /// <param name="path">URL path and query string.</param>
        /// <returns>The first matched room, or null.</returns>
        public virtual RoomConfig IsRoomMatch(string host, string path)
        {
            return MatchRoom(host, path, this.getRoomConfig());
        }

        /// <summary>
        /// If the request is for a checkout-complete page, tell the API so the session is ended, and report "busted".
        /// API failures here are logged and ignored: busting is best-effort and must never block a confirmation page.
        /// </summary>
        public virtual string CheckoutBuster(string host, string path, string targetUrl, String userAgent, String language, String ipAddress, string token)
        {
            string result = IsCheckoutBuster(host, path, this.getRoomConfig());
            if (result == "busted" && Util.IsValidToken(token))
            {
                try
                {
                    GetApiClient().getToken(targetUrl, userAgent, language, ipAddress, token);
                }
                catch (Exception ex)
                {
                    Log("Error communicating checkout bust to CrowdHandler API", ex);
                }
            }
            return result;
        }

        /// <summary>
        /// Look up an application configuration value from Web.config or App.config.
        /// </summary>
        /// <param name="settingName">The config value name to look up</param>
        /// <param name="required">Throw <see cref="MissingFieldException"/> if the value is absent</param>
        /// <returns>Config value, or null if absent and not required</returns>
        protected virtual String getConfigValue(String settingName, Boolean required)
        {
            String value = ConfigurationManager.AppSettings[settingName];

            if (value == null && required)
            {
                throw new MissingFieldException("Value not found in ConfigurationManager.AppSettings: " + settingName);
            }

            return value;
        }

        /// <summary>Diagnostic output. Writes to <see cref="System.Diagnostics.Trace"/>; override to route into your logging.</summary>
        protected virtual void Log(string message, Exception exception = null)
        {
            System.Diagnostics.Trace.TraceWarning("CrowdHandler: " + message + (exception != null ? " " + exception.Message : ""));
        }

        /// <summary>
        /// One line per validation decision, for debugging a live queue. Writes to <see cref="System.Diagnostics.Trace"/> at
        /// information level; the ASP.NET Core adapters also log the outcome through <c>ILogger</c>.
        /// </summary>
        protected virtual void LogDecision(string action, string slug, string token, string tokenSource, bool apiCalled)
        {
            string key = string.IsNullOrEmpty(PublicApiKey) ? "-" : PublicApiKey.Length > 8 ? PublicApiKey.Substring(0, 8) + "…" : PublicApiKey;
            System.Diagnostics.Trace.TraceInformation($"CrowdHandler: {action} key={key} slug={slug ?? "-"} token={token ?? "-"} src={tokenSource} api={(apiCalled ? "yes" : "no")}");
        }

        /// <summary>Async twin of <see cref="CheckoutBuster"/> for the default (non-overridden) path.</summary>
        private async Task<string> CheckoutBustAsync(List<RoomConfig> rooms, string host, string path, string targetUrl, string userAgent, string language, string ipAddress, string token, CancellationToken cancellationToken)
        {
            string result = IsCheckoutBuster(host, path, rooms);
            if (result == "busted" && Util.IsValidToken(token))
            {
                try
                {
                    await GetApiClient().getTokenAsync(targetUrl, userAgent, language, ipAddress, token, null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log("Error communicating checkout bust to CrowdHandler API", ex);
                }
            }
            return result;
        }

        /// <summary>The API refused the room feed (typically a wrong or rotated public key): never trust, send to the waiting room without a slug, and surface the error.</summary>
        private ValidateResult RoomsRejected(CrowdhandlerApiException ex, string targetUrl, string token, string chCode)
        {
            Log("CrowdHandler API rejected the room configuration request (HTTP " + ex.StatusCode + "); sending visitor to the waiting room. Check your public API key.", ex);
            LogDecision("redirect-rejected", null, token, "-", true);
            return new ValidateResult
            {
                Action = "redirect",
                redirectUrl = BuildWaitingRoomUrl(null, targetUrl, chCode, token),
                targetUrl = targetUrl,
                token = token,
                code = chCode,
                bustCookie = "not-busted",
                apiError = ex,
            };
        }

        private static readonly ConcurrentDictionary<Type, bool> SyncValidateOverrides = new ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// True when a subclass overrides the synchronous <see cref="Validate"/> but not <see cref="ValidateAsync"/>. Hosts that
        /// would otherwise call <see cref="ValidateAsync"/> use this to keep honouring such overrides.
        /// </summary>
        public static bool UsesCustomSynchronousValidate(Type gateKeeperType)
        {
            if (gateKeeperType == null || gateKeeperType == typeof(GateKeeper)) return false;
            return SyncValidateOverrides.GetOrAdd(gateKeeperType, t =>
            {
                bool syncOverridden = false, asyncOverridden = false;
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    if (m.DeclaringType == typeof(GateKeeper) || !typeof(GateKeeper).IsAssignableFrom(m.DeclaringType)) continue;
                    if (m.Name == nameof(Validate)) syncOverridden = true;
                    if (m.Name == nameof(ValidateAsync)) asyncOverridden = true;
                }
                return syncOverridden && !asyncOverridden;
            });
        }

        private static readonly ConcurrentDictionary<Type, bool> RoomLookupOverrides = new ConcurrentDictionary<Type, bool>();

        /// <summary>True if a subclass overrides any of the synchronous room-lookup virtuals, in which case the async path defers to them.</summary>
        private bool OverridesRoomLookup => RoomLookupOverrides.GetOrAdd(GetType(), t =>
        {
            if (t == typeof(GateKeeper)) return false;
            foreach (string name in new[] { nameof(getRoomConfig), nameof(IsRoomMatch), nameof(CheckoutBuster), nameof(IsCheckoutBuster), nameof(MatchRoom) })
            {
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (m.Name == name && m.DeclaringType != typeof(GateKeeper) && typeof(GateKeeper).IsAssignableFrom(m.DeclaringType)) return true;
                }
            }
            return false;
        });

        private ApiClient _crowdhandlerApi;
        private ApiClient GetApiClient()
        {
            if (this._crowdhandlerApi == null)
            {
                this._crowdhandlerApi = new ApiClient(this.ApiEndpoint, this.PublicApiKey, this.APIRequestTimeout, this.RoomCacheTTL);
            }
            return this._crowdhandlerApi;
        }

        /// <summary>The account's room configuration (cached). Override to supply rooms from elsewhere.</summary>
        public virtual List<RoomConfig> getRoomConfig()
        {
            return GetApiClient().getRoomConfig();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------------------------------------------

        private async Task<TokenResponse> GetTokenFromApiAsync(string targetUrl, string userAgent, string language, string ipAddress, string token, string code, CancellationToken cancellationToken)
        {
            var api = GetApiClient();
            string apiToken = string.IsNullOrEmpty(token) ? ApiClient.TokenNotSupplied : token;
            try
            {
                return await api.getTokenAsync(targetUrl, userAgent, language, ipAddress, apiToken, string.IsNullOrEmpty(code) ? null : code, cancellationToken).ConfigureAwait(false);
            }
            catch (CrowdhandlerApiException ex) when (ex.IsClientError && !string.IsNullOrEmpty(code))
            {
                // An invalid priority code should not lock the visitor out: queue them normally instead.
                Log("CrowdHandler API rejected priority code; retrying without it", ex);
                return await api.getTokenAsync(targetUrl, userAgent, language, ipAddress, apiToken, null, cancellationToken).ConfigureAwait(false);
            }
        }

        private string BuildWaitingRoomUrl(string slug, string targetUrl, string code, string token)
        {
            return BuildWaitingRoomUrl(this.WaitingRoomEndpoint, this.PublicApiKey, slug, targetUrl, code, token);
        }

        private static CookieSignature NewestSignature(CookieToken cookieToken)
        {
            return cookieToken?.signatures?.LastOrDefault(s => s != null && !string.IsNullOrEmpty(s.sig));
        }

        /// <summary>Whether the visitor's newest signature is older than the (jittered) check-in interval.</summary>
        internal bool IsCheckInDue(CookieSignature newest, string token)
        {
            if (CheckInInterval <= TimeSpan.Zero || newest == null)
            {
                return false;
            }
            if (GetApiClient().IsDegraded)
            {
                return false; // the API is failing: a check-in would only add the timeout budget to a visitor who is already validated
            }
            // Deterministic per-token jitter in [0.75, 1.25] spreads check-ins so an outage does not end in a stampede.
            int h = 0;
            foreach (char c in token ?? "") { h = unchecked(h * 31 + c); }
            double factor = 0.75 + ((h & 0x7fffffff) % 5001) / 10000.0;
            return DateTime.UtcNow - newest.gen.ToUniversalTime() >= TimeSpan.FromTicks((long)(CheckInInterval.Ticks * factor));
        }

        /// <summary>
        /// The URL that sends a visitor to a waiting room and brings them back to <paramref name="targetUrl"/>.
        /// Hosts implementing trust-on-fail use this with their safety-net slug.
        /// </summary>
        public static string BuildWaitingRoomUrl(string waitingRoomEndpoint, string publicApiKey, string slug, string targetUrl, string code = null, string token = null)
        {
            return $"{(waitingRoomEndpoint ?? "").TrimEnd('/')}/{Uri.EscapeDataString(slug ?? "")}?url={Uri.EscapeDataString(targetUrl ?? "")}&ch-code={Uri.EscapeDataString(code ?? "")}&ch-id={Uri.EscapeDataString(token ?? "")}&ch-public-key={Uri.EscapeDataString(publicApiKey ?? "")}";
        }

        private bool IsExcluded(string pathAndQuery)
        {
            if (string.IsNullOrEmpty(this.Exclusions))
            {
                return false;
            }
            Regex exclusions;
            try
            {
                exclusions = GetRegex(this.Exclusions);
            }
            catch (ArgumentException ex)
            {
                // A bad regex must not take the site down during an on-sale: protection stays on, only the asset shortcut is lost.
                if (InvalidPatternsReported.TryAdd(this.Exclusions, true))
                {
                    Log("Exclusions value is not a valid regular expression; nothing will be excluded until it is fixed", ex);
                }
                return false;
            }
            try
            {
                return exclusions.IsMatch(pathAndQuery);
            }
            catch (RegexMatchTimeoutException)
            {
                Log("Exclusions regex timed out; treating URL as not excluded");
                return false;
            }
        }

        private static string SanitiseQueryValue(string rawValue)
        {
            if (string.IsNullOrEmpty(rawValue))
            {
                return "";
            }
            string value;
            try
            {
                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            }
            catch (Exception)
            {
                return "";
            }
            return value == "undefined" || value == "null" ? "" : value;
        }

        private static CookieToken LastToken(CookieData cookie)
        {
            return cookie?.tokens != null && cookie.tokens.Count > 0 ? cookie.tokens[cookie.tokens.Count - 1] : null;
        }

        /// <summary>
        /// Compare a room's domain (<c>https://host</c>, possibly with a <c>*</c> wildcard) to a request host.
        /// Scheme, port and a leading <c>www.</c> are ignored, mirroring the API's own matching.
        /// </summary>
        internal static bool DomainMatches(string roomDomain, string host)
        {
            if (string.IsNullOrEmpty(roomDomain) || string.IsNullOrEmpty(host))
            {
                return false;
            }
            string roomHost = NormaliseHost(roomDomain, stripWww: true);
            string requestHost = NormaliseHost(host, stripWww: true);
            if (roomHost.IndexOf('*') < 0)
            {
                return string.Equals(roomHost, requestHost, StringComparison.OrdinalIgnoreCase);
            }
            // Wildcards: "*.example.com" should cover www.example.com as well as shop.example.com, so test the host both ways.
            string pattern = "^" + Regex.Escape(roomHost).Replace("\\*", "[^/]*") + "$";
            return Regex.IsMatch(requestHost, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexMatchTimeout)
                || Regex.IsMatch(NormaliseHost(host, stripWww: false), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexMatchTimeout);
        }

        private static string NormaliseHost(string value, bool stripWww)
        {
            string v = value.Trim();
            int schemeEnd = v.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
            {
                v = v.Substring(schemeEnd + 3);
            }
            int slash = v.IndexOf('/');
            if (slash >= 0)
            {
                v = v.Substring(0, slash);
            }
            int colon = v.LastIndexOf(':');
            if (colon >= 0 && v.IndexOf(']') < colon)
            {
                v = v.Substring(0, colon);
            }
            if (stripWww && v.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(4);
            }
            return v.ToLowerInvariant();
        }

        /// <summary>Regex match that cannot throw: invalid patterns and timeouts count as "no match".</summary>
        private bool SafeIsMatch(string pattern, string input)
        {
            try
            {
                return GetRegex(pattern).IsMatch(input);
            }
            catch (ArgumentException)
            {
                Log("Ignoring invalid regular expression from room configuration: " + pattern);
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                Log("Regular expression timed out; treating as no match: " + pattern);
                return false;
            }
        }

        /// <summary>Compiled patterns are cached process-wide; every request would otherwise recompile every room's regex.</summary>
        private static Regex GetRegex(string pattern)
        {
            if (RegexCache.TryGetValue(pattern, out Regex cached))
            {
                return cached;
            }
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexMatchTimeout);
            if (RegexCache.Count >= RegexCacheLimit)
            {
                RegexCache.Clear();
            }
            RegexCache[pattern] = regex;
            return regex;
        }

        private static class ThreadSafeRandom
        {
            private static readonly Random Global = new Random();
            [ThreadStatic] private static Random _local;

            public static double NextDouble()
            {
                if (_local == null)
                {
                    int seed;
                    lock (Global) { seed = Global.Next(); }
                    _local = new Random(seed);
                }
                return _local.NextDouble();
            }
        }
    }
}
