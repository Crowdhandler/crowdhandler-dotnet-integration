using Crowdhandler.NETsdk.JSONTypes;
using Crowdhandler.NETsdk.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crowdhandler.NETsdk
{
    /// <summary>
    /// Thin client for the CrowdHandler public API. One instance per <see cref="GateKeeper"/>; the underlying HttpClient pool,
    /// room cache and TLS setup are process-wide statics so per-request construction is cheap.
    /// </summary>
    internal class ApiClient
    {
        public const int DefaultApiRequestTimeoutSeconds = 3;
        public const int DefaultRoomCacheSeconds = 60;
        public const string TokenNotSupplied = "notsupplied";

        /// <summary>Performance samples are fire-and-forget; never let a slow API hold a thread longer than this.</summary>
        private static readonly TimeSpan PerformanceRecordTimeout = TimeSpan.FromMilliseconds(1500);

        /// <summary>Samples in flight at once, process-wide. Beyond this, samples are dropped rather than queued: a slow API must not become a connection or thread spike.</summary>
        internal const int MaxPerformanceSamplesInFlight = 64; // process-wide; sized so many tenants sharing a host still get samples through
        private static int _performanceSamplesInFlight;

        // IHttpClientFactory is not available on .NET Framework or netstandard2.0, so we've implemented our own using a timed pool of HttpClient objects.
        // Items live for 5 minutes so DNS changes are picked up, and are shared across requests and GateKeeper instances.
        // Never mutate a pooled client's DefaultRequestHeaders: it may be in use by another thread.
        internal static LimitedPool<HttpClient> _httpClientPool;
        private static readonly object _poolLock = new object();

        /// <summary>
        /// Test seam: when set, every pooled HttpClient is built over the handler this returns instead of the default one.
        /// Set it before the first request in the process (the pool is created lazily and reused).
        /// </summary>
        internal static Func<HttpMessageHandler> HttpMessageHandlerFactory;

        /// <summary>Last successfully fetched room config per public key. Served if a refresh fails, so a CrowdHandler API blip never turns into a full failure.</summary>
        private static readonly ConcurrentDictionary<string, KeyValuePair<DateTime, List<RoomConfig>>> _lastGoodRooms = new ConcurrentDictionary<string, KeyValuePair<DateTime, List<RoomConfig>>>();

        /// <summary>How long a previously fetched room config may stand in for a live one. Beyond this an outage is reported to the caller (trust-on-fail) rather than acting on day-old configuration.</summary>
        internal static readonly TimeSpan MaxStaleRooms = TimeSpan.FromHours(1);

        /// <summary>After a failed room refresh with nothing to fall back on, further attempts are skipped for this long so an API outage costs each request one fast failure rather than the full timeout budget.</summary>
        internal static readonly TimeSpan RoomFailureBackoff = TimeSpan.FromSeconds(10);
        private static readonly ConcurrentDictionary<string, KeyValuePair<DateTime, CrowdhandlerApiException>> _recentRoomFailures = new ConcurrentDictionary<string, KeyValuePair<DateTime, CrowdhandlerApiException>>();

        /// <summary>Per endpoint: when a transient failure last exhausted its retries. Optional work (check-ins) is suspended for <see cref="RoomFailureBackoff"/> after it, so an outage never taxes locally validated visitors.</summary>
        private static readonly ConcurrentDictionary<string, DateTime> _lastTransientFailure = new ConcurrentDictionary<string, DateTime>();

        /// <summary>True shortly after a transient API failure: skip optional calls, keep serving from local state.</summary>
        internal bool IsDegraded => _lastTransientFailure.TryGetValue(RoomsCacheKey, out var at) && DateTime.UtcNow - at < RoomFailureBackoff;

        /// <summary>Identifies this SDK to the API for support and traffic attribution.</summary>
        internal static readonly string RequestSource = "dotnet-sdk/" + typeof(ApiClient).Assembly.GetName().Version.ToString(3);
        /// <summary>Single-flight gate for room refreshes on the async path. Awaited, never blocked on, so waiting requests hold no thread.</summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _roomFetchGates = new ConcurrentDictionary<string, SemaphoreSlim>();

        protected string apiUrl;
        protected string publicApiKey;
        protected string apiRequestTimeout;
        protected string roomCacheTTL;

#if NETFRAMEWORK
        static ApiClient()
        {
            // The framework's TLS default is governed by the *application's* target (httpRuntime targetFramework), not by this
            // library's. An MVC 5 app still targeting 4.5 or 4.6 keeps the legacy SSL3/TLS1.0 default and would fail every
            // handshake with the API. Enabling TLS 1.2 (and 1.3 where the runtime knows it) is additive and safe.
            try
            {
                const System.Net.SecurityProtocolType tls13 = (System.Net.SecurityProtocolType)12288;
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                try { System.Net.ServicePointManager.SecurityProtocol |= tls13; } catch (NotSupportedException) { }
            }
            catch (NotSupportedException)
            {
                // Runtime does not know TLS 1.2 at all (pre-4.5): nothing we can do.
            }
        }
#endif

        public ApiClient(string apiUrl, string publicApiKey, string apiRequestTimeout, string roomCacheTTL)
        {
            this.apiUrl = (apiUrl ?? "").TrimEnd('/');
            this.publicApiKey = publicApiKey;
            this.apiRequestTimeout = apiRequestTimeout;
            this.roomCacheTTL = roomCacheTTL;

            if (_httpClientPool == null)
            {
                lock (_poolLock)
                {
                    if (_httpClientPool == null)
                    {
                        _httpClientPool = new LimitedPool<HttpClient>(CreateClientObject, client => client.Dispose(), TimeSpan.FromMinutes(5));
                    }
                }
            }
        }

        /// <summary>Seconds to wait for the API. Invalid or non-positive configuration falls back to the default.</summary>
        internal int TimeoutSeconds => ParsePositiveIntOrDefault(apiRequestTimeout, DefaultApiRequestTimeoutSeconds);

        /// <summary>Seconds to cache the room config. 0 disables caching (every request refetches). Invalid falls back to the default.</summary>
        internal int RoomCacheSeconds => ParseNonNegativeIntOrDefault(roomCacheTTL, DefaultRoomCacheSeconds);

        protected HttpClient CreateClientObject()
        {
            var factory = HttpMessageHandlerFactory;
            var client = factory != null ? new HttpClient(factory(), disposeHandler: true) : new HttpClient();

            // The pool is shared by every GateKeeper in the process, each of which may be configured with a different
            // timeout, so the client itself has none: every request carries its own CancellationToken deadline.
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        // ---------------------------------------------------------------------------------------------------------
        // /v1/requests
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Check a visitor in with CrowdHandler. Creates a new session (POST) when no token is supplied, otherwise
        /// refreshes the existing one (GET /v1/requests/{token}).
        /// </summary>
        /// <exception cref="CrowdhandlerApiException">The API refused or could not be reached. Check <see cref="CrowdhandlerApiException.IsClientError"/>.</exception>
        public virtual TokenResponse getToken(string url, String userAgent, String language, String ipAddress, string token = TokenNotSupplied, string code = null)
        {
            return RunSync(() => getTokenAsync(url, userAgent, language, ipAddress, token, code, CancellationToken.None));
        }

        public virtual async Task<TokenResponse> getTokenAsync(string url, String userAgent, String language, String ipAddress, string token, string code, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(publicApiKey))
            {
                throw new CrowdhandlerApiException("CrowdHandler API URL or public API key is not configured");
            }

            var parameters = new List<KeyValuePair<string, string>>();
            AddIfPresent(parameters, "url", url);
            AddIfPresent(parameters, "agent", userAgent);
            AddIfPresent(parameters, "lang", language);
            AddIfPresent(parameters, "ip", ipAddress);
            AddIfPresent(parameters, "code", code);

            string responseBody;
            if (string.IsNullOrEmpty(token) || token == TokenNotSupplied)
            {
                responseBody = await doRequestAsync(HttpMethod.Post, apiUrl + "/v1/requests/", parameters, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var uri = apiUrl + "/v1/requests/" + Uri.EscapeDataString(token) + "?" + Util.BuildQuery(parameters);
                responseBody = await doRequestAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
            }

            var result = ParseResult(responseBody);
            var tokenResponse = result.ToObject<TokenResponse>();

            // status 6 means the API is throttling this key: an infrastructure condition, not a decision about the visitor.
            if (tokenResponse.status == 6)
            {
                _lastTransientFailure[RoomsCacheKey] = DateTime.UtcNow;
                throw new CrowdhandlerApiException("CrowdHandler API is throttling requests (status 6)", 429);
            }
            return tokenResponse;
        }

        // ---------------------------------------------------------------------------------------------------------
        // /v1/rooms
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The account's room configuration, cached for <see cref="RoomCacheSeconds"/>. If a refresh fails and a previous
        /// copy exists, the previous copy is returned so local signature validation keeps working through an API outage.
        /// </summary>
        /// <exception cref="CrowdhandlerApiException">No cached copy exists and the API could not be reached.</exception>
        public virtual List<RoomConfig> getRoomConfig()
        {
            // Synchronous path (Validate, MVC 5, custom callers). Deliberately no lock: a thread that blocks while holding
            // a lock other request threads are queuing on starves the thread pool under load. Concurrent refreshes on
            // expiry are cheap; a stalled pool is not.
            string cacheKey = RoomsCacheKey;
            if (RoomCacheSeconds > 0 && MemoryCache.Default[cacheKey] is List<RoomConfig> cached)
            {
                return cached;
            }
            ThrowIfRecentlyFailed(cacheKey);
            return ParseRoomsOrStale(cacheKey, () => StoreRooms(cacheKey, ParseRooms(getRoomConfigJson())));
        }

        /// <summary>
        /// Asynchronous room configuration: same cache and stale fallback as <see cref="getRoomConfig"/>, with a single
        /// in-flight refresh per key that other requests await rather than block on.
        /// </summary>
        public virtual async Task<List<RoomConfig>> GetRoomConfigAsync(CancellationToken cancellationToken)
        {
            string cacheKey = RoomsCacheKey;
            if (RoomCacheSeconds > 0 && MemoryCache.Default[cacheKey] is List<RoomConfig> cached)
            {
                return cached;
            }
            if (RoomCacheSeconds == 0)
            {
                // Caching disabled: every request fetches, concurrently; a single-flight gate would only serialise them.
                return await FetchRoomsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            }
            var gate = _roomFetchGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (MemoryCache.Default[cacheKey] is List<RoomConfig> refreshed)
                {
                    return refreshed;
                }
                return await FetchRoomsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<List<RoomConfig>> FetchRoomsAsync(string cacheKey, CancellationToken cancellationToken)
        {
            ThrowIfRecentlyFailed(cacheKey);
            List<RoomConfig> rooms;
            try
            {
                rooms = ParseRooms(await doRequestAsync(HttpMethod.Get, apiUrl + "/v1/rooms", null, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is CrowdhandlerApiException || ex is JsonException)
            {
                // A definitive rejection (bad or revoked key) must never be papered over with an old copy of the rooms.
                if (ex is CrowdhandlerApiException rejected && rejected.IsClientError) throw;
                if (TryGetStale(cacheKey, ex, out var stale)) return StoreRooms(cacheKey, stale, isStale: true);
                RecordFailure(cacheKey, ex);
                throw;
            }
            return StoreRooms(cacheKey, rooms);
        }

        private string RoomsCacheKey => "rooms|" + apiUrl + "|" + publicApiKey; // per endpoint as well as per key: a staging gatekeeper must never serve production rooms

        private List<RoomConfig> ParseRoomsOrStale(string cacheKey, Func<List<RoomConfig>> fetch)
        {
            try
            {
                return fetch();
            }
            catch (Exception ex) when (ex is CrowdhandlerApiException || ex is JsonException)
            {
                // A definitive rejection (bad or revoked key) must never be papered over with an old copy of the rooms.
                if (ex is CrowdhandlerApiException rejected && rejected.IsClientError) throw;
                if (TryGetStale(cacheKey, ex, out var stale)) return StoreRooms(cacheKey, stale, isStale: true);
                RecordFailure(cacheKey, ex);
                throw;
            }
        }

        private static void RecordFailure(string cacheKey, Exception ex)
        {
            var apiEx = ex as CrowdhandlerApiException ?? new CrowdhandlerApiException("CrowdHandler /v1/rooms response could not be parsed", null, ex);
            _recentRoomFailures[cacheKey] = new KeyValuePair<DateTime, CrowdhandlerApiException>(DateTime.UtcNow, apiEx);
        }

        /// <summary>During an outage with no usable copy, fail fast for <see cref="RoomFailureBackoff"/> instead of re-paying the timeout budget on every request.</summary>
        private static void ThrowIfRecentlyFailed(string cacheKey)
        {
            if (_recentRoomFailures.TryGetValue(cacheKey, out var recent))
            {
                if (DateTime.UtcNow - recent.Key < RoomFailureBackoff)
                {
                    throw new CrowdhandlerApiException("CrowdHandler room configuration unavailable (retry suppressed for " + RoomFailureBackoff.TotalSeconds + "s after: " + recent.Value.Message + ")", recent.Value.StatusCode, recent.Value);
                }
                _recentRoomFailures.TryRemove(cacheKey, out _);
            }
        }

        private static bool TryGetStale(string cacheKey, Exception cause, out List<RoomConfig> stale)
        {
            if (_lastGoodRooms.TryGetValue(cacheKey, out var last) && DateTime.UtcNow - last.Key < MaxStaleRooms)
            {
                System.Diagnostics.Trace.TraceWarning("CrowdHandler: room config refresh failed, serving previous copy. " + cause.Message);
                stale = last.Value;
                return true;
            }
            stale = null;
            return false;
        }

        /// <summary>Cache a fresh feed (and remember it as the last good copy), or a stale copy for the cache period only so an outage is not re-probed on every request.</summary>
        private List<RoomConfig> StoreRooms(string cacheKey, List<RoomConfig> rooms, bool isStale = false)
        {
            if (ReferenceEquals(rooms, null)) return rooms;
            if (!isStale)
            {
                _recentRoomFailures.TryRemove(cacheKey, out _);
                _lastGoodRooms[cacheKey] = new KeyValuePair<DateTime, List<RoomConfig>>(DateTime.UtcNow, rooms);
            }
            if (RoomCacheSeconds > 0)
            {
                int seconds = isStale ? Math.Min(RoomCacheSeconds, (int)RoomFailureBackoff.TotalSeconds) : RoomCacheSeconds;
                MemoryCache.Default.Set(cacheKey, rooms, new CacheItemPolicy { AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(seconds) });
            }
            return rooms;
        }

        /// <summary>Fetch the raw room config JSON from the API (no caching).</summary>
        public virtual string getRoomConfigJson()
        {
            return RunSync(() => doRequestAsync(HttpMethod.Get, apiUrl + "/v1/rooms", null, CancellationToken.None));
        }

        internal static List<RoomConfig> ParseRooms(string json)
        {
            var result = ParseResult(json);
            if (result.Type != JTokenType.Array)
            {
                throw new CrowdhandlerApiException("CrowdHandler /v1/rooms response 'result' is not an array");
            }
            return result.Children().Select(r => r.ToObject<RoomConfig>()).Where(r => r != null).ToList();
        }

        // ---------------------------------------------------------------------------------------------------------
        // /v1/responses (performance samples)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Report how long the origin took to serve a CrowdHandler-validated response. CrowdHandler uses these samples to
        /// autotune room capacity. Fire-and-forget: never throws, never blocks the caller.
        /// </summary>
        /// <param name="responseID">The responseID returned by /v1/requests for this visit.</param>
        /// <param name="httpCode">HTTP status the origin returned.</param>
        /// <param name="elapsedMilliseconds">Time from request start to response, in ms.</param>
        /// <param name="sampleRate">How many real responses this sample represents (5 for a 20% sample).</param>
        public virtual void recordPerformance(string responseID, int httpCode, long elapsedMilliseconds, int sampleRate)
        {
            if (string.IsNullOrEmpty(responseID))
            {
                return;
            }
            if (Interlocked.Increment(ref _performanceSamplesInFlight) > MaxPerformanceSamplesInFlight)
            {
                Interlocked.Decrement(ref _performanceSamplesInFlight);
                return;
            }
            var body = JsonConvert.SerializeObject(new { httpCode = httpCode, sampleRate = sampleRate, time = elapsedMilliseconds });
            var uri = apiUrl + "/v1/responses/" + Uri.EscapeDataString(responseID);

            // Not awaited by design; the method owns its exceptions and its deadline.
            _ = RecordPerformanceAsync(uri, body);
        }

        private async Task RecordPerformanceAsync(string uri, string body)
        {
            try
            {
                using (var cts = new CancellationTokenSource(PerformanceRecordTimeout))
                {
                    await SendOnceAsync(HttpMethod.Put, uri, new StringContent(body, Encoding.UTF8, "application/json"), cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceInformation("CrowdHandler: performance sample not recorded. " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _performanceSamplesInFlight);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Transport
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Send a request and return the body. Transient failures (5xx, 429, timeout, network) are retried once with a fresh
        /// message; definitive rejections (other 4xx) are not. Worst case wall time is 2 x <see cref="TimeoutSeconds"/>.
        /// </summary>
        protected async Task<string> doRequestAsync(HttpMethod method, string uri, List<KeyValuePair<string, string>> formBody, CancellationToken cancellationToken)
        {
            const int maxAttempts = 2;
            CrowdhandlerApiException last = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // HttpRequestMessage can only be sent once, so build a fresh one per attempt.
                HttpContent content = formBody != null ? new FormUrlEncodedContent(formBody) : null;
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                        return await SendOnceAsync(method, uri, content, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (CrowdhandlerApiException ex) when (ex.IsClientError)
                {
                    throw;
                }
                catch (CrowdhandlerApiException ex) when (ex.StatusCode == 429)
                {
                    // Throttled: transient (trust-on-fail applies) but never retried immediately, which would add to the load being shed.
                    last = ex;
                    break;
                }
                catch (CrowdhandlerApiException ex)
                {
                    last = ex;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    last = new CrowdhandlerApiException($"CrowdHandler API request timed out after {TimeoutSeconds}s: {method} {uri}", null, ex);
                }
                catch (HttpRequestException ex)
                {
                    last = new CrowdhandlerApiException($"CrowdHandler API request failed: {method} {uri}: {ex.Message}", null, ex);
                }
            }
            _lastTransientFailure[RoomsCacheKey] = DateTime.UtcNow;
            throw last;
        }

        private async Task<string> SendOnceAsync(HttpMethod method, string uri, HttpContent content, CancellationToken cancellationToken)
        {
            using (var msg = new HttpRequestMessage(method, uri))
            {
                msg.Headers.TryAddWithoutValidation("x-api-key", publicApiKey);
                msg.Headers.TryAddWithoutValidation("x-request-source", RequestSource);
                msg.Content = content;

                using (var container = _httpClientPool.Get())
                using (var response = await container.Value.SendAsync(msg, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status >= 400)
                    {
                        throw new CrowdhandlerApiException($"CrowdHandler API returned HTTP {status} for {method} {StripQuery(uri)}", status);
                    }
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>Extract the 'result' element of a standard API envelope, or throw if the body is not what we expect.</summary>
        private static JToken ParseResult(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new CrowdhandlerApiException("CrowdHandler API returned an empty body");
            }
            JObject envelope;
            try
            {
                envelope = JObject.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new CrowdhandlerApiException("CrowdHandler API returned a non-JSON body", null, ex);
            }
            var result = envelope["result"];
            if (result == null || result.Type == JTokenType.Null)
            {
                throw new CrowdhandlerApiException("CrowdHandler API response has no 'result'");
            }
            return result;
        }

        /// <summary>
        /// Run an async operation synchronously without deadlocking under a synchronization context (classic ASP.NET).
        /// Unwraps AggregateException so callers see the real failure.
        /// </summary>
        internal static T RunSync<T>(Func<Task<T>> operation)
        {
            return Task.Run(operation).GetAwaiter().GetResult();
        }

        private static void AddIfPresent(List<KeyValuePair<string, string>> list, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                list.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        private static string StripQuery(string uri)
        {
            int q = uri.IndexOf('?');
            return q < 0 ? uri : uri.Substring(0, q);
        }

        private static int ParsePositiveIntOrDefault(string value, int fallback)
        {
            return value != null && int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
        }

        private static int ParseNonNegativeIntOrDefault(string value, int fallback)
        {
            return value != null && int.TryParse(value, out int parsed) && parsed >= 0 ? parsed : fallback;
        }
    }
}
