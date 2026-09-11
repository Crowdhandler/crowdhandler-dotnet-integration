using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Crowdhandler.NETsdk;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;

namespace Crowdhandler.MVCSDK.AspNetCore
{
    /// <summary>What the processor decided for a request, and what is needed to report its timing afterwards.</summary>
    public sealed class CrowdhandlerOutcome
    {
        /// <summary>Where to send the visitor, or null to let the request proceed.</summary>
        public string RedirectUrl { get; internal set; }

        /// <summary>The CrowdHandler responseID for this request, if the API was called.</summary>
        public string ResponseID { get; internal set; }

        /// <summary>The validation result, for callers that want the detail (null if validation threw and trust-on-fail applied).</summary>
        public GateKeeper.ValidateResult? Result { get; internal set; }

        internal IGateKeeper GateKeeper { get; set; }
        internal double SampleRate { get; set; }
        internal long StartTimestamp { get; set; }

        /// <summary>Report the origin's response time for this request to CrowdHandler (sampled, fire-and-forget).</summary>
        public void RecordPerformance(int httpStatusCode)
        {
            if (ResponseID == null || !(GateKeeper is GateKeeper gk))
            {
                return;
            }
            if (SampleRate <= 0)
            {
                return; // reporting disabled entirely
            }
            long elapsedMs = (Stopwatch.GetTimestamp() - StartTimestamp) * 1000 / Stopwatch.Frequency;
            bool checkIn = Result?.checkIn == true;
            // The check-in is the rate limiter, so every check-in reports; first visits are sampled.
            gk.RecordPerformance(ResponseID, httpStatusCode, elapsedMs, checkIn ? 1.0 : SampleRate);
        }
    }

    /// <summary>
    /// The one place where an ASP.NET Core request is turned into a gatekeeper decision and that decision is applied
    /// to the response (cookies, cache headers). Used by both <see cref="CrowdhandlerFilterAttribute"/> and
    /// <see cref="CrowdhandlerMiddleware"/> so the two cannot drift.
    /// </summary>
    public static class CrowdhandlerRequestProcessor
    {
        /// <param name="context">The request.</param>
        /// <param name="options">Effective options.</param>
        /// <param name="gk">The gatekeeper to validate with.</param>
        /// <param name="logger">Optional logger.</param>
        /// <param name="readCookie">Optional override for reading the session cookie value.</param>
        /// <param name="writeCookie">Optional override for writing (value, delete) the session cookie.</param>
        public static async Task<CrowdhandlerOutcome> HandleAsync(HttpContext context, CrowdhandlerOptions options, IGateKeeper gk, ILogger logger, Func<string> readCookie = null, Action<string, bool> writeCookie = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (gk == null) throw new ArgumentNullException(nameof(gk));

            var outcome = new CrowdhandlerOutcome { GateKeeper = gk, SampleRate = options.EffectivePerformanceSampleRate, StartTimestamp = Stopwatch.GetTimestamp() };
            var request = context.Request;

            // A request with no usable host (old load-balancer probes send HTTP/1.0 without one) cannot be matched to a room: let it through.
            Uri url;
            if (!request.Host.HasValue || !Uri.TryCreate(request.GetEncodedUrl(), UriKind.Absolute, out url))
            {
                logger?.LogDebug("CrowdHandler skipped: request has no usable host");
                return outcome;
            }
            string userAgent = request.Headers["User-Agent"].ToString();
            string language = request.Headers["Accept-Language"].ToString();
            string ipAddress = RequestHelpers.ExtractClientIp(request.Headers[options.EffectiveClientIpHeader].ToString(), context.Connection?.RemoteIpAddress?.ToString());
            string cookieValue = readCookie != null ? readCookie() : RequestHelpers.NormaliseCookieValue(request.Cookies[options.EffectiveCookieName]);

            GateKeeper.ValidateResult result;
            try
            {
                // A subclass overriding only the synchronous Validate keeps that behaviour; everything else runs fully asynchronously.
                result = gk is GateKeeper concrete && !GateKeeper.UsesCustomSynchronousValidate(concrete.GetType())
                    ? await concrete.ValidateAsync(url, userAgent, language, ipAddress, cookieValue, null, context.RequestAborted).ConfigureAwait(false)
                    : await Task.Run(() => gk.Validate(url, userAgent, language, ipAddress, cookieValue)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return outcome; // the client went away; nothing to decide
            }
            catch (Exception ex) when (!(ex is InvalidCastException || ex is MissingFieldException || ex is ArgumentException || ex is InvalidOperationException) && !options.EffectiveDebugMode)
            {
                // Transient failure talking to CrowdHandler: apply the trust-on-fail policy.
                logger?.LogError(ex, "CrowdHandler validation failed for {Url}; FailTrust={FailTrust}", url, options.EffectiveFailTrust);

                if (!options.EffectiveFailTrust)
                {
                    string safetySlug = options.SafetyNetSlug ?? (gk as GateKeeper)?.SafetyNetSlug ?? "";
                    outcome.RedirectUrl = GateKeeper.BuildWaitingRoomUrl(gk.WaitingRoomEndpoint, gk.PublicApiKey, safetySlug, GateKeeper.RemoveCrowdhandlerParameters(url));
                    SetNoCache(context.Response);
                }
                return outcome;
            }

            outcome.Result = result;
            outcome.ResponseID = result.responseID;
            // The timing sample measures the origin, so the clock starts after validation: none of the SDK's own API calls
            // (session check, room refresh, check-in) count towards it.
            outcome.StartTimestamp = Stopwatch.GetTimestamp();

            logger?.LogDebug("CrowdHandler {Action} {Url} key={Key} slug={Slug} token={Token} api={ApiCalled} bust={Bust}", result.Action, url, KeyPrefix(gk.PublicApiKey), result.slug ?? "-", result.token ?? "-", result.responseID != null, result.bustCookie);

            if (result.apiError != null)
            {
                logger?.LogError(result.apiError, "CrowdHandler API rejected the request (HTTP {Status}); visitor sent to the waiting room. Check your API keys.", result.apiError.StatusCode);
            }

            if (result.bustCookie == "busted")
            {
                if (writeCookie != null) writeCookie("", true); else WriteCookie(context, options, "", true);
            }
            else if (result.setCookie)
            {
                if (writeCookie != null) writeCookie(result.cookieValue, false); else WriteCookie(context, options, result.cookieValue, false);
            }

            if (result.Action == "redirect")
            {
                SetNoCache(context.Response);
                outcome.RedirectUrl = result.redirectUrl;
            }
            return outcome;
        }

        /// <summary>Write or delete the session cookie with the SDK's standard attributes.</summary>
        public static void WriteCookie(HttpContext context, CrowdhandlerOptions options, string value, bool delete)
        {
            if (delete)
            {
                context.Response.Cookies.Delete(options.EffectiveCookieName, CookieOptionsFor(context, options));
            }
            else
            {
                context.Response.Cookies.Append(options.EffectiveCookieName, value ?? "", CookieOptionsFor(context, options));
            }
            // A response carrying a per-visitor session cookie must never be cached by a shared cache. The application can still override this.
            if (string.IsNullOrEmpty(context.Response.Headers["Cache-Control"]))
            {
                context.Response.Headers["Cache-Control"] = "private";
            }
        }

        private static CookieOptions CookieOptionsFor(HttpContext context, CrowdhandlerOptions options)
        {
            var cookie = new CookieOptions
            {
                Path = "/",
                HttpOnly = false, // CrowdHandler's client-side script reads it
                Secure = options.CookieSecure ?? context.Request.IsHttps,
                IsEssential = true, // the session must survive cookie-consent policies or visitors loop through the waiting room
                SameSite = SameSiteMode.Lax,
            };
            if (!string.IsNullOrEmpty(options.CookieDomain))
            {
                cookie.Domain = options.CookieDomain;
            }
            if (options.CookieMaxAgeSeconds > 0)
            {
                cookie.MaxAge = TimeSpan.FromSeconds(options.CookieMaxAgeSeconds.Value);
            }
            return cookie;
        }

        /// <summary>First characters of a public key, enough to tell tenants apart in logs without printing the key.</summary>
        private static string KeyPrefix(string publicKey) => string.IsNullOrEmpty(publicKey) ? "-" : publicKey.Length > 8 ? publicKey.Substring(0, 8) + "…" : publicKey;

        private static void SetNoCache(HttpResponse response)
        {
            response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            response.Headers["Expires"] = "Fri, 01 Jan 1970 00:00:00 GMT";
            response.Headers["Pragma"] = "no-cache";
        }
    }
}
