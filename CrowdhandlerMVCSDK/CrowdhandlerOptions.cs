using System;
using Crowdhandler.NETsdk;

namespace Crowdhandler.MVCSDK
{
    /// <summary>
    /// Everything the integration needs to know. On ASP.NET Core bind this from configuration
    /// (<c>services.AddCrowdhandler(Configuration.GetSection("Crowdhandler"))</c>); on ASP.NET MVC 5 the
    /// <see cref="CrowdhandlerFilterAttribute"/> properties and <c>Web.config</c> appSettings feed the same values.
    /// Null means "not set, use the next source or the default".
    /// </summary>
    public class CrowdhandlerOptions
    {
        /// <summary>Default name of the session cookie.</summary>
        public const string DefaultCookieName = "crowdhandler";

        /// <summary>Your CrowdHandler public API key. Required.</summary>
        public string PublicApiKey { get; set; }

        /// <summary>Your CrowdHandler private API key. Required; used only to verify signatures locally, never sent anywhere.</summary>
        public string PrivateApiKey { get; set; }

        /// <summary>CrowdHandler API base URL. Default https://api.crowdhandler.com</summary>
        public string ApiEndpoint { get; set; }

        /// <summary>Waiting room base URL. Default https://wait.crowdhandler.com</summary>
        public string WaitingRoomEndpoint { get; set; }

        /// <summary>Regex matched against the request path and query; matches are never validated. Default excludes static assets by extension.</summary>
        public string Exclusions { get; set; }

        /// <summary>Seconds to wait for the CrowdHandler API. Default 3.</summary>
        public int? ApiRequestTimeoutSeconds { get; set; }

        /// <summary>Seconds to cache the room configuration. 0 disables caching. Default 60.</summary>
        public int? RoomCacheSeconds { get; set; }

        /// <summary>Waiting room slug used when the API is unreachable and <see cref="FailTrust"/> is false.</summary>
        public string SafetyNetSlug { get; set; }

        /// <summary>
        /// What to do when the CrowdHandler API cannot be reached (timeout, 5xx, throttled): true lets the visitor
        /// through, false sends them to the <see cref="SafetyNetSlug"/> waiting room. Does not apply to definitive API
        /// rejections (4xx), which always send the visitor to the waiting room. Default true.
        /// </summary>
        public bool? FailTrust { get; set; }

        /// <summary>Rethrow validation errors instead of applying <see cref="FailTrust"/>. For local development only. Default false.</summary>
        public bool? DebugMode { get; set; }

        /// <summary>Name of the session cookie. Default "crowdhandler". Change only if you also change it in the CrowdHandler control panel.</summary>
        public string CookieName { get; set; }

        /// <summary>Domain attribute for the session cookie, e.g. ".example.com" to share a session across subdomains. Default: host-only.</summary>
        public string CookieDomain { get; set; }

        /// <summary>Force the Secure attribute on the cookie. Default: set when the request arrived over HTTPS.</summary>
        public bool? CookieSecure { get; set; }

        /// <summary>Lifetime of the session cookie in seconds. Default: a session cookie (cleared when the browser closes).</summary>
        public int? CookieMaxAgeSeconds { get; set; }

        /// <summary>Header carrying the original client IP when behind a proxy or load balancer. Default "X-Forwarded-For". The first address in the header is used.</summary>
        public string ClientIpHeader { get; set; }

        /// <summary>Fraction (0 to 1) of validated responses whose timing is reported to CrowdHandler for capacity autotuning. 0 disables. Default 0.2.</summary>
        public double? PerformanceSampleRate { get; set; }

        /// <summary>
        /// Minutes between periodic check-ins with the API for visitors who are validated locally. A check-in keeps the
        /// visitor's session alive on CrowdHandler's side and reports a timing sample. Default 2; 0 disables. See the README.
        /// </summary>
        public double? CheckInIntervalMinutes { get; set; }

        /// <summary>A custom <see cref="IGateKeeper"/> implementation to instantiate instead of <see cref="GateKeeper"/>. Must have a parameterless constructor.</summary>
        public Type GatekeeperType { get; set; }

        /// <summary>A factory for the <see cref="IGateKeeper"/> to use. Takes precedence over <see cref="GatekeeperType"/>.</summary>
        public Func<IGateKeeper> GatekeeperFactory { get; set; }

        // ---- effective values ----

        public bool EffectiveFailTrust => FailTrust ?? true;
        public bool EffectiveDebugMode => DebugMode ?? false;
        public string EffectiveCookieName => string.IsNullOrEmpty(CookieName) ? DefaultCookieName : CookieName;
        public string EffectiveClientIpHeader => string.IsNullOrEmpty(ClientIpHeader) ? "X-Forwarded-For" : ClientIpHeader;
        public double EffectivePerformanceSampleRate => PerformanceSampleRate ?? 0.2;

        /// <summary>Return a copy where every value set on <paramref name="overrides"/> replaces the value here.</summary>
        public CrowdhandlerOptions Overlay(CrowdhandlerOptions overrides)
        {
            var o = (CrowdhandlerOptions)MemberwiseClone();
            if (overrides == null)
            {
                return o;
            }
            o.PublicApiKey = overrides.PublicApiKey ?? o.PublicApiKey;
            o.PrivateApiKey = overrides.PrivateApiKey ?? o.PrivateApiKey;
            o.ApiEndpoint = overrides.ApiEndpoint ?? o.ApiEndpoint;
            o.WaitingRoomEndpoint = overrides.WaitingRoomEndpoint ?? o.WaitingRoomEndpoint;
            o.Exclusions = overrides.Exclusions ?? o.Exclusions;
            o.ApiRequestTimeoutSeconds = overrides.ApiRequestTimeoutSeconds ?? o.ApiRequestTimeoutSeconds;
            o.RoomCacheSeconds = overrides.RoomCacheSeconds ?? o.RoomCacheSeconds;
            o.SafetyNetSlug = overrides.SafetyNetSlug ?? o.SafetyNetSlug;
            o.FailTrust = overrides.FailTrust ?? o.FailTrust;
            o.DebugMode = overrides.DebugMode ?? o.DebugMode;
            o.CookieName = overrides.CookieName ?? o.CookieName;
            o.CookieDomain = overrides.CookieDomain ?? o.CookieDomain;
            o.CookieSecure = overrides.CookieSecure ?? o.CookieSecure;
            o.CookieMaxAgeSeconds = overrides.CookieMaxAgeSeconds ?? o.CookieMaxAgeSeconds;
            o.ClientIpHeader = overrides.ClientIpHeader ?? o.ClientIpHeader;
            o.PerformanceSampleRate = overrides.PerformanceSampleRate ?? o.PerformanceSampleRate;
            o.CheckInIntervalMinutes = overrides.CheckInIntervalMinutes ?? o.CheckInIntervalMinutes;
            o.GatekeeperType = overrides.GatekeeperType ?? o.GatekeeperType;
            o.GatekeeperFactory = overrides.GatekeeperFactory ?? o.GatekeeperFactory;
            return o;
        }

        /// <summary>
        /// Build the gatekeeper these options describe. Unset values fall back to <c>ConfigurationManager.AppSettings</c>
        /// (Web.config / App.config) and then to the SDK defaults, exactly as <see cref="GateKeeper"/> does.
        /// </summary>
        public IGateKeeper CreateGateKeeper()
        {
            if (GatekeeperFactory != null)
            {
                return GatekeeperFactory() ?? throw new InvalidOperationException("GatekeeperFactory returned null");
            }

            string checkIn = CheckInIntervalMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (GatekeeperType == null)
            {
                return new GateKeeper(PublicApiKey, PrivateApiKey, ApiEndpoint, WaitingRoomEndpoint, Exclusions,
                    ApiRequestTimeoutSeconds?.ToString(), RoomCacheSeconds?.ToString(), SafetyNetSlug, checkIn);
            }

            if (!typeof(IGateKeeper).IsAssignableFrom(GatekeeperType))
            {
                throw new InvalidCastException("GatekeeperType MUST implement IGateKeeper");
            }

            IGateKeeper gk;
            try
            {
                // A GateKeeper (or subclass exposing the base constructor) gets the configured values up front; a parameterless
                // constructor would otherwise look for CROWDHANDLER_* appSettings, which ASP.NET Core apps do not have.
                // The constructor is matched by parameter *names*, so both the 8- and 9-parameter forms (and any future one) work.
                var values = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["publicKey"] = PublicApiKey, ["privateKey"] = PrivateApiKey, ["apiEndpoint"] = ApiEndpoint, ["waitingRoomEndpoint"] = WaitingRoomEndpoint,
                    ["exclusions"] = Exclusions, ["apiRequestTimeout"] = ApiRequestTimeoutSeconds?.ToString(), ["roomCacheTTL"] = RoomCacheSeconds?.ToString(),
                    ["safetyNetSlug"] = SafetyNetSlug, ["checkInIntervalMinutes"] = checkIn,
                };
                System.Reflection.ConstructorInfo best = null;
                foreach (var ctor in GatekeeperType.GetConstructors())
                {
                    var ps = ctor.GetParameters();
                    if (ps.Length > 0 && Array.TrueForAll(ps, p => p.ParameterType == typeof(string) && values.ContainsKey(p.Name)) && (best == null || ps.Length > best.GetParameters().Length))
                    {
                        best = ctor;
                    }
                }
                if (best != null)
                {
                    gk = (IGateKeeper)best.Invoke(Array.ConvertAll(best.GetParameters(), p => (object)values[p.Name]));
                }
                else
                {
                    try { gk = (IGateKeeper)Activator.CreateInstance(GatekeeperType); }
                    catch (MissingMethodException ex) { throw new InvalidOperationException("GatekeeperType " + GatekeeperType.Name + " needs either a parameterless constructor or one whose string parameters are named like GateKeeper's (publicKey, privateKey, ...).", ex); }
                }
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
            if (ApiEndpoint != null) gk.ApiEndpoint = ApiEndpoint;
            if (PublicApiKey != null) gk.PublicApiKey = PublicApiKey;
            if (PrivateApiKey != null) gk.PrivateApiKey = PrivateApiKey;
            if (WaitingRoomEndpoint != null) gk.WaitingRoomEndpoint = WaitingRoomEndpoint;
            if (Exclusions != null) gk.Exclusions = Exclusions;
            if (ApiRequestTimeoutSeconds != null) gk.APIRequestTimeout = ApiRequestTimeoutSeconds.ToString();
            if (RoomCacheSeconds != null) gk.RoomCacheTTL = RoomCacheSeconds.ToString();
            if (gk is GateKeeper concrete)
            {
                if (SafetyNetSlug != null) concrete.SafetyNetSlug = SafetyNetSlug;
                if (CheckInIntervalMinutes != null) concrete.CheckInInterval = CheckInIntervalMinutes > 0 ? TimeSpan.FromMinutes(CheckInIntervalMinutes.Value) : TimeSpan.Zero;
            }
            return gk;
        }
    }
}
