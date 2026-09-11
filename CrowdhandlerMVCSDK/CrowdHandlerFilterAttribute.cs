using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Crowdhandler.NETsdk;

#if OLDDOTNET
using System.Configuration;
using System.Web;
using System.Web.Mvc;
#endif

#if NEWDOTNET
using Crowdhandler.MVCSDK.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#endif

namespace Crowdhandler.MVCSDK
{
    /// <summary>
    /// Apply CrowdHandler waiting rooms to MVC controller actions: <c>[CrowdhandlerFilter]</c>.
    /// Works on ASP.NET MVC 5 and ASP.NET Core MVC. On ASP.NET Core, unset properties fall back to the
    /// <see cref="CrowdhandlerOptions"/> registered with <c>services.AddCrowdhandler(...)</c>; on both, to
    /// <c>appSettings</c> (<c>CROWDHANDLER_*</c> keys) and then the SDK defaults.
    /// </summary>
    public class CrowdhandlerFilterAttribute : ActionFilterAttribute
    {
        internal const string PerformanceItemKey = "Crowdhandler.Performance";

        /// <summary>A custom <see cref="IGateKeeper"/> implementation to use instead of the default.</summary>
        public Type GatekeeperType { get; set; }

        private bool _failTrust = true, _failTrustExplicit;
        private bool _debugMode, _debugModeExplicit;
        private double _performanceSampleRate = 0.2;
        private bool _performanceSampleRateExplicit;

        /// <summary>Let visitors through when the CrowdHandler API is unreachable. Default true. See <see cref="CrowdhandlerOptions.FailTrust"/>.</summary>
        public bool FailTrust { get { return _failTrust; } set { _failTrust = value; _failTrustExplicit = true; } }

        /// <summary>Rethrow validation errors. Local development only.</summary>
        public bool DebugMode { get { return _debugMode; } set { _debugMode = value; _debugModeExplicit = true; } }

        public string ApiEndpoint { get; set; }
        public string PublicApiKey { get; set; }
        public string PrivateApiKey { get; set; }
        public string WaitingRoomEndpoint { get; set; }
        public string Exclusions { get; set; }
        public string APIRequestTimeout { get; set; }
        public string RoomCacheTTL { get; set; }
        public string SafetyNetSlug { get; set; }

        /// <summary>Cookie domain, e.g. ".example.com" to share the session across subdomains.</summary>
        public string CookieDomain { get; set; }

        /// <summary>Header carrying the original client IP behind a proxy. Default X-Forwarded-For.</summary>
        public string ClientIpHeader { get; set; }

        /// <summary>Fraction of responses whose timing is reported to CrowdHandler. Default 0.2; 0 disables.</summary>
        public double PerformanceSampleRate { get { return _performanceSampleRate; } set { _performanceSampleRate = value; _performanceSampleRateExplicit = true; } }

        private double _checkInIntervalMinutes;
        private bool _checkInIntervalExplicit;

        /// <summary>Minutes between periodic API check-ins for locally validated visitors. Default 2 (from the gatekeeper) when not set; 0 disables. See <see cref="CrowdhandlerOptions.CheckInIntervalMinutes"/>.</summary>
        public double CheckInIntervalMinutes { get { return _checkInIntervalMinutes; } set { _checkInIntervalMinutes = value; _checkInIntervalExplicit = true; } }

        /// <summary>The options this attribute's own properties express (unset properties are null).</summary>
        protected virtual CrowdhandlerOptions GetAttributeOptions()
        {
            return new CrowdhandlerOptions
            {
                PublicApiKey = PublicApiKey,
                PrivateApiKey = PrivateApiKey,
                ApiEndpoint = ApiEndpoint,
                WaitingRoomEndpoint = WaitingRoomEndpoint,
                Exclusions = Exclusions,
                ApiRequestTimeoutSeconds = ParseInt(APIRequestTimeout),
                RoomCacheSeconds = ParseInt(RoomCacheTTL),
                SafetyNetSlug = SafetyNetSlug,
                FailTrust = _failTrustExplicit ? FailTrust : (bool?)null,
                DebugMode = _debugModeExplicit ? DebugMode : (bool?)null,
                CookieName = Overrides(nameof(getCookieName), Type.EmptyTypes) ? getCookieName() : null,
                CookieDomain = CookieDomain,
                ClientIpHeader = ClientIpHeader,
                PerformanceSampleRate = _performanceSampleRateExplicit ? PerformanceSampleRate : (double?)null,
                CheckInIntervalMinutes = _checkInIntervalExplicit ? CheckInIntervalMinutes : (double?)null,
                GatekeeperType = GatekeeperType,
            };
        }

        /// <summary>Create the gatekeeper from this attribute's properties. Override to supply your own.</summary>
        protected virtual IGateKeeper getGatekeeper()
        {
            return GetAttributeOptions().CreateGateKeeper();
        }

        /// <summary>Name of the session cookie. Override only if you also change it in the CrowdHandler control panel.</summary>
        public virtual String getCookieName()
        {
            return CrowdhandlerOptions.DefaultCookieName;
        }

        private static int? ParseInt(string value)
        {
            return value != null && int.TryParse(value, out int parsed) ? parsed : (int?)null;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _overrideCache = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();

        /// <summary>Whether a subclass overrides the named virtual method. Existing subclasses keep working on both frameworks.</summary>
        private bool Overrides(string methodName, params Type[] parameterTypes)
        {
            return _overrideCache.GetOrAdd(methodName + "/" + parameterTypes.Length, _ =>
            {
                var m = GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, parameterTypes, null);
                return m != null && m.DeclaringType != typeof(CrowdhandlerFilterAttribute) && typeof(CrowdhandlerFilterAttribute).IsAssignableFrom(m.DeclaringType);
            });
        }

#if NEWDOTNET
        // ------------------------------------------------------------------------------------------------------
        // ASP.NET Core
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Options for this request: attribute properties on top of any <see cref="CrowdhandlerOptions"/> registered in DI.
        /// With a per-request <see cref="ICrowdhandlerOptionsResolver"/> registered (multi-tenant hosts), the resolved
        /// options are used instead; a null resolution means the request bypasses CrowdHandler.
        /// </summary>
        protected virtual CrowdhandlerOptions ResolveOptions(HttpContext context)
        {
            return ResolveOptionsAsync(context).GetAwaiter().GetResult();
        }

        private async Task<CrowdhandlerOptions> ResolveOptionsAsync(HttpContext context)
        {
            var resolver = context?.RequestServices?.GetService<ICrowdhandlerOptionsResolver>();
            if (resolver != null)
            {
                var resolved = await resolver.ResolveAsync(context).ConfigureAwait(false);
                return resolved?.Overlay(GetAttributeOptions());
            }
            var registered = context?.RequestServices?.GetService<IOptions<CrowdhandlerOptions>>()?.Value;
            return (registered ?? new CrowdhandlerOptions()).Overlay(GetAttributeOptions());
        }

        /// <summary>Gatekeeper for this request. A subclass's <c>getGatekeeper()</c> override always wins; otherwise the merged options build it.</summary>
        protected virtual IGateKeeper getGatekeeper(HttpContext context, CrowdhandlerOptions options)
        {
            return Overrides(nameof(getGatekeeper), Type.EmptyTypes) ? getGatekeeper() : options.CreateGateKeeper();
        }

        public override Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            // A subclass that overrides the synchronous OnActionExecuting (the documented customisation recipe) keeps
            // the synchronous pipeline; everyone else gets fully asynchronous validation.
            if (Overrides(nameof(OnActionExecuting), typeof(ActionExecutingContext)))
            {
                return base.OnActionExecutionAsync(context, next);
            }
            return ExecuteAsync(context, next);
        }

        private async Task ExecuteAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (await ValidateAsync(context).ConfigureAwait(false))
            {
                await next().ConfigureAwait(false);
            }
        }

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            Task.Run(() => ValidateAsync(filterContext)).GetAwaiter().GetResult();
        }

        /// <summary>Run validation and apply the outcome to the response. Returns true if the action should run.</summary>
        private async Task<bool> ValidateAsync(ActionExecutingContext context)
        {
            var options = await ResolveOptionsAsync(context.HttpContext).ConfigureAwait(false);
            if (options == null)
            {
                return true; // not a CrowdHandler tenant
            }
            var gk = getGatekeeper(context.HttpContext, options);
            var logger = context.HttpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("Crowdhandler");

            Func<string> readCookie = Overrides(nameof(getCookieValue), typeof(ActionExecutingContext)) ? () => getCookieValue(context) : (Func<string>)null;
            Action<string, bool> writeCookie = Overrides(nameof(setCookieValue), typeof(ActionExecutingContext), typeof(string), typeof(bool)) ? (value, delete) => setCookieValue(context, value, delete) : (Action<string, bool>)null;

            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(context.HttpContext, options, gk, logger, readCookie, writeCookie).ConfigureAwait(false);

            if (outcome.RedirectUrl != null)
            {
                context.Result = new RedirectResult(outcome.RedirectUrl);
                return false;
            }
            if (outcome.ResponseID != null)
            {
                context.HttpContext.Items[PerformanceItemKey] = outcome;
            }
            return true;
        }

        public override void OnResultExecuted(ResultExecutedContext context)
        {
            if (context.HttpContext.Items.TryGetValue(PerformanceItemKey, out var stored) && stored is CrowdhandlerOutcome outcome)
            {
                context.HttpContext.Items.Remove(PerformanceItemKey);
                outcome.RecordPerformance(context.HttpContext.Response.StatusCode);
            }
        }

        /// <summary>Read the session cookie. Override to source it from elsewhere (e.g. a header).</summary>
        public virtual String getCookieValue(ActionExecutingContext filterContext)
        {
            return RequestHelpers.NormaliseCookieValue(filterContext.HttpContext.Request.Cookies[this.getCookieName()]);
        }

        /// <summary>Write (or delete) the session cookie. Override to change how it is stored.</summary>
        public virtual void setCookieValue(ActionExecutingContext filterContext, string JSONString, bool deleteCookie = false)
        {
            if (filterContext?.HttpContext?.Response == null)
            {
                return;
            }
            CrowdhandlerRequestProcessor.WriteCookie(filterContext.HttpContext, ResolveOptions(filterContext.HttpContext), JSONString, deleteCookie);
        }

#else
        // ------------------------------------------------------------------------------------------------------
        // ASP.NET MVC 5 (System.Web)
        // ------------------------------------------------------------------------------------------------------

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            var request = filterContext.HttpContext.Request;
            var response = filterContext.HttpContext.Response;
            Uri url = request.Url;
            string userAgent = request.UserAgent;
            string language = request.Headers["Accept-Language"];
            string ipAddress = getIpAddress(filterContext);
            string cookieData = this.getCookieValue(filterContext);

            IGateKeeper gk = this.getGatekeeper();
            GateKeeper.ValidateResult result;

            try
            {
                result = gk.Validate(url, userAgent, language, ipAddress, cookieData);
            }
            catch (Exception ex)
            {
                // Configuration problems are never swallowed: surface them.
                if (ex is InvalidCastException || ex is MissingFieldException || ex is ArgumentException || ex is InvalidOperationException || this.DebugMode)
                {
                    throw;
                }

                LogError("CrowdHandler validation failed", ex);

                if (this.FailTrust)
                {
                    return; // trust on fail: carry on with the request
                }

                var safetySlug = SafetyNetSlug ?? (gk as GateKeeper)?.SafetyNetSlug ?? ConfigurationManager.AppSettings["CROWDHANDLER_SAFETYNET_SLUG"] ?? "";
                SetNoCache(response);
                filterContext.Result = new RedirectResult(GateKeeper.BuildWaitingRoomUrl(gk.WaitingRoomEndpoint, gk.PublicApiKey, safetySlug, url.ToString()));
                return;
            }

            if (result.apiError != null)
            {
                LogError("CrowdHandler API rejected the request (HTTP " + result.apiError.StatusCode + "); visitor sent to the waiting room. Check your API keys.", result.apiError);
            }

            if (result.bustCookie == "busted")
            {
                setCookieValue(filterContext, "", true);
            }
            else if (result.setCookie)
            {
                setCookieValue(filterContext, result.cookieValue);
            }

            if (result.Action == "redirect")
            {
                SetNoCache(response);
                filterContext.Result = new RedirectResult(result.redirectUrl);
                return;
            }

            if (result.responseID != null && gk is GateKeeper concrete && PerformanceSampleRate > 0)
            {
                // Origin timing starts after validation so the SDK's own API calls are not counted.
                filterContext.HttpContext.Items[PerformanceItemKey] = new Tuple<GateKeeper, GateKeeper.ValidateResult, long>(concrete, result, Stopwatch.GetTimestamp());
            }
        }

        public override void OnResultExecuted(ResultExecutedContext filterContext)
        {
            if (filterContext.HttpContext.Items[PerformanceItemKey] is Tuple<GateKeeper, GateKeeper.ValidateResult, long> perf)
            {
                filterContext.HttpContext.Items.Remove(PerformanceItemKey);
                long elapsedMs = (Stopwatch.GetTimestamp() - perf.Item3) * 1000 / Stopwatch.Frequency;
                var result = perf.Item2;
                perf.Item1.RecordPerformance(result.responseID, filterContext.HttpContext.Response.StatusCode, elapsedMs, result.checkIn ? 1.0 : PerformanceSampleRate);
            }
        }

        /// <summary>The visitor's IP address: first entry of <see cref="ClientIpHeader"/> if present, else the socket address. Override for other proxy conventions.</summary>
        protected virtual string getIpAddress(ActionExecutingContext filterContext)
        {
            var request = filterContext.HttpContext.Request;
            string headerName = string.IsNullOrEmpty(ClientIpHeader) ? "X-Forwarded-For" : ClientIpHeader;
            string forwarded = request.Headers[headerName];
            return RequestHelpers.ExtractClientIp(forwarded, request.UserHostAddress);
        }

        public virtual String getCookieValue(ActionExecutingContext filterContext)
        {
            var cookie = filterContext.HttpContext.Request.Cookies[this.getCookieName()];
            return RequestHelpers.NormaliseCookieValue(cookie?.Value);
        }

        public virtual void setCookieValue(ActionExecutingContext filterContext, string JSONString, bool deleteCookie = false)
        {
            if (filterContext?.HttpContext?.Response == null)
            {
                return;
            }
            var request = filterContext.HttpContext.Request;
            var cookie = new HttpCookie(this.getCookieName())
            {
                Path = "/",
                HttpOnly = false, // CrowdHandler's client-side script reads it
                Secure = request.IsSecureConnection,
            };
            if (!string.IsNullOrEmpty(CookieDomain))
            {
                cookie.Domain = CookieDomain;
            }
            if (deleteCookie)
            {
                cookie.Value = "";
                cookie.Expires = DateTime.UtcNow.AddYears(-1);
            }
            else
            {
                cookie.Value = Uri.EscapeDataString(JSONString ?? "");
            }
            // A response that sets or deletes the per-visitor session cookie must never be cached by a shared cache.
            filterContext.HttpContext.Response.Cache.SetCacheability(HttpCacheability.Private);
            filterContext.HttpContext.Response.Cookies.Set(cookie);
        }

        private static void SetNoCache(HttpResponseBase response)
        {
            // Works in both classic and integrated pipeline modes (Response.Headers does not).
            response.Cache.SetCacheability(HttpCacheability.NoCache);
            response.Cache.SetNoStore();
            response.Cache.SetRevalidation(HttpCacheRevalidation.AllCaches);
            response.Cache.SetExpires(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        /// <summary>Error output. Writes to <see cref="Trace"/>; override to route into your logging.</summary>
        protected virtual void LogError(string message, Exception exception)
        {
            Trace.TraceError("CrowdHandler: {0}: {1}", message, exception);
        }
#endif
    }
}
