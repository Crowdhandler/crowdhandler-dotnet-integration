using System;
using System.Text.RegularExpressions;
using Crowdhandler.NETsdk;
using System.Configuration;

// .net 4 web APIs come out of System.Web
#if OLDDOTNET
using System.Web;
using System.Web.Mvc;
#endif

// .net 5/core assemblies use Microsoft.AspNetCore, but they have the API so everything else should work
#if NEWDOTNET
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
#endif

namespace Crowdhandler.MVCSDK
{
    /// <summary>
    /// A filter attribute to apply Crowdhandler waiting rooms to your MVC Controller actions
    /// </summary>
    public class CrowdhandlerFilterAttribute : ActionFilterAttribute
    {
        public Type GatekeeperType
        {
            get;
            set;
        }
        public bool FailTrust
        {
            get;
            set;
        } = true;
        public bool DebugMode
        {
            get;
            set;
        } = false;

        public string ApiEndpoint
        {
            get;
            set;
        }
        public string PublicApiKey
        {
            get;
            set;
        }
        public string PrivateApiKey
        {
            get;
            set;
        }
        public string Exclusions
        {
            get;
            set;
        }
        public string APIRequestTimeout
        {
            get;
            set;
        }
        public string RoomCacheTTL
        {
            get;
            set;
        }
        public string SafetyNetSlug
        {
            get;
            set;
        }

        protected virtual IGateKeeper getGatekeeper()
        {
            // if no gatekeeper is specified, use our default implementation
            if (GatekeeperType == null)
            {
                // If the api properties are not set on this object they should be null, and therefore allow the gatekeeper defaults to kick in
                return new GateKeeper(ApiEndpoint, PublicApiKey, PrivateApiKey, Exclusions, APIRequestTimeout, RoomCacheTTL);
            }

            if (!typeof(IGateKeeper).IsAssignableFrom(GatekeeperType))
            {
                throw new InvalidCastException("GatekeeperType MUST implement IGateKeeper");
            }

            var gk = (IGateKeeper)Activator.CreateInstance(GatekeeperType);

            // Activator.CreateInstance requires some whackiness when passing constructor params, we can avoid it by modifying the properties directly
            if (ApiEndpoint != null)
            {
                gk.ApiEndpoint = ApiEndpoint;
            }
            if (PublicApiKey != null)
            {
                gk.PublicApiKey = PublicApiKey;
            }
            if (PrivateApiKey != null)
            {
                gk.PrivateApiKey = PrivateApiKey;
            }
            if (Exclusions != null)
            {
                gk.Exclusions = Exclusions;
            }
            if (APIRequestTimeout != null)
            {
                gk.APIRequestTimeout = APIRequestTimeout;
            }
            if (RoomCacheTTL != null)
            {
                gk.RoomCacheTTL = RoomCacheTTL;
            }

            return gk;
        }

        public override void OnActionExecuted(ActionExecutedContext filterContext)
        {
            // TODO: Performance tracking could go here
        }

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
#if NEWDOTNET
      var url = new Uri(Microsoft.AspNetCore.Http.Extensions.UriHelper.GetDisplayUrl(filterContext.HttpContext.Request));
      string userAgent = filterContext.HttpContext.Request.Headers["User-Agent"].ToString();
      string language = filterContext.HttpContext.Request.Headers["Accept-Language"].ToString();
      string ipAddress = String.Empty;

      string forwardedForHeader = filterContext.HttpContext.Request.Headers["X-Forwarded-For"];
      if (!string.IsNullOrEmpty(forwardedForHeader)) {
        // Get the list of IP addresses from HTTP_X_FORWARDED_FOR header
        string[] ipList = forwardedForHeader.ToString().Split(',');

        if (ipList.Length > 0) {
          // Get the first IP in the list, which should be the original client IP
          ipAddress = ipList[0].Trim();
        }
      }

      // If we didn't get an IP from HTTP_X_FORWARDED_FOR, or if it was empty, use UserHostAddress
      if (String.IsNullOrEmpty(ipAddress)) {
        ipAddress = filterContext.HttpContext.Connection.RemoteIpAddress.ToString();
      }

#else
            var url = filterContext.HttpContext.Request.Url;
            string userAgent = filterContext.HttpContext.Request.UserAgent;
            string language = filterContext.HttpContext.Request.UserLanguages != null ? string.Join(",", filterContext.HttpContext.Request.UserLanguages) : null;
            string ipAddress = String.Empty;

            if (filterContext.HttpContext.Request.ServerVariables["HTTP_X_FORWARDED_FOR"] != null)
            {
                // Get the list of IP addresses from HTTP_X_FORWARDED_FOR header
                string[] ipList = filterContext.HttpContext.Request.ServerVariables["HTTP_X_FORWARDED_FOR"].Split(',');

                if (ipList.Length > 0)
                {
                    // Get the first IP in the list, which should be the original client IP
                    ipAddress = ipList[0].Trim();
                }
            }

            // If we didn't get an IP from HTTP_X_FORWARDED_FOR, or if it was empty, use UserHostAddress
            if (String.IsNullOrEmpty(ipAddress))
            {
                ipAddress = HttpContext.Current.Request.UserHostAddress;
            }
#endif
            string CookieData = this.getCookieValue(filterContext);

            IGateKeeper gk = this.getGatekeeper();

            GateKeeper.ValidateResult result;

            try
            {
                result = gk.Validate(url, userAgent, language, ipAddress, CookieData);
            }
            catch (Exception ex)
            {
                // We explicitly throw two exceptions based on bad configuration values
                if (ex is InvalidCastException || ex is MissingFieldException)
                {
                    throw;
                }

                // If we're in debug mode, throw the exception
                if (this.DebugMode)
                {
                    throw;
                }

                // Write to the error log
                Console.Error.WriteLine("Exception in CrowdHandlerFilterAttribute: {0}", ex);

                if (this.FailTrust)
                {
                    // we immediatley cancel execution and return to the controller to carry on with the request
                    return;
                }

                // At this point we've failed and need to redirect to the safety waiting room
                var safetySlug = SafetyNetSlug ?? ConfigurationManager.AppSettings["CROWDHANDLER_SAFETYNET_SLUG"] ?? "";
                var failureWaitingroomUrl = gk.WaitingRoomEndpoint + $"/{safetySlug}?url={Uri.EscapeDataString(url.ToString())}&ch-code=&ch-id=&ch-public-key={gk.PublicApiKey}";

                filterContext.HttpContext.Response.StatusCode = 302;
                filterContext.HttpContext.Response.Headers["Location"] = failureWaitingroomUrl;
                return;
            }

            if (result.setCookie)
            {
                if (result.bustCookie != null && result.bustCookie != "busted")
                {
                    setCookieValue(filterContext, result.cookieValue);
                }
                else if (result.bustCookie == "busted")
                {
                    setCookieValue(filterContext, result.cookieValue, true); // Delete the cookie
                }
            //Handle checkout busting with no room match
            } else if (result.bustCookie == "busted")
            {
                setCookieValue(filterContext, "", true);
            }


            if (result.Action == "allow")
            {
                // success and/or no validation required
                return;
            }

            // Set the no cache headers
            filterContext.HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            filterContext.HttpContext.Response.Headers.Add("Expires", "Fri, 01 Jan 1970 00:00:00 GMT");
            filterContext.HttpContext.Response.Headers.Add("Pragma", "no-cache");

            if (result.Action == "redirect")
            {
                filterContext.HttpContext.Response.StatusCode = 302;
                filterContext.HttpContext.Response.Headers["Location"] = result.redirectUrl;
                return;
            }
        }

        public virtual String getCookieValue(ActionExecutingContext filterContext)
        {
            String JSONString = "";

            string cookieName = this.getCookieName();
            if (filterContext.HttpContext.Request.Cookies[cookieName] != null)
            {
#if OLDDOTNET
                JSONString = filterContext.HttpContext.Request.Cookies[cookieName].Value.ToString() ?? "";
#else
        JSONString = filterContext.HttpContext.Request.Cookies[cookieName] ?? "";
#endif
                JSONString = Uri.UnescapeDataString(JSONString);
            }

            return JSONString;
        }

        public virtual void setCookieValue(ActionExecutingContext filterContext, string JSONString, bool deleteCookie = false)
        {
            // Check if filterContext and filterContext.HttpContext.Response are not null
            if (filterContext == null || filterContext.HttpContext?.Response == null)
            {
                // Handle null case
                return;
            }

            string cookieName = this.getCookieName();

#if OLDDOTNET
            HttpCookie cookie = new HttpCookie(cookieName);
            cookie.Value = JSONString;
            cookie.Path = "/";
            if (deleteCookie)
            {
                cookie.Expires = DateTime.Now.AddDays(-1); // Delete cookie
            }
            filterContext.HttpContext.Response.Cookies.Add(cookie);
#else
          CookieOptions cookieOptions = new CookieOptions();
          if (deleteCookie) {
            cookieOptions.Expires = DateTimeOffset.Now.AddDays(-1);
          } else {
            cookieOptions.Path = "/";
          }
          filterContext.HttpContext.Response.Cookies.Append(cookieName, JSONString, cookieOptions);
#endif
        }


        public virtual String getCookieName()
        {
            return "crowdhandler";
        }
    }
}