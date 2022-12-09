using System;
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
        public Type GatekeeperType { get; set; }
        public bool FailTrust { get; set; } = true;
        public bool DebugMode { get; set; } = false;
        
        public string ApiEndpoint { get; set; }
        public string PublicApiKey { get; set; }
        public string PrivateApiKey { get; set; }

        

        protected virtual IGateKeeper getGatekeeper()
        {
            // if no gatekeeper is specified, use our default implementation
            if (GatekeeperType == null)
            {
                // If the api properties are not set on this object they should be null, and therefore allow the gatekeeper defaults to kick in
                return new GateKeeper(ApiEndpoint, PublicApiKey, PrivateApiKey);
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

            return gk;
        }

        public override void OnActionExecuted(ActionExecutedContext filterContext)
        {
            // TODO: Performance tracking could go here
        }

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
#if NEWDOTNET
            var url = new Uri( Microsoft.AspNetCore.Http.Extensions.UriHelper.GetDisplayUrl(filterContext.HttpContext.Request) );
#else
            var url = filterContext.HttpContext.Request.Url;
#endif
            string CookieData = this.getCookieValue(filterContext);

            IGateKeeper gk = this.getGatekeeper();

            GateKeeper.ValidateResult result;

            try
            {
                result = gk.Validate(url, CookieData);
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
                var safetySlug = ConfigurationManager.AppSettings["CROWDHANDLER_SAFETYNET_SLUG"] ?? "";
                var failureWaitingroomUrl = gk.WaitingRoomEndpoint + $"/{safetySlug}?url={Uri.EscapeDataString(url.ToString())}&ch-code=&ch-id=&ch-public-key={gk.PublicApiKey}";
                filterContext.HttpContext.Response.Redirect(failureWaitingroomUrl, true);
                return;
            }

            // The result asks we set a cookie
            if (result.setCookie)
            {
                setCookieValue(filterContext, result.cookieValue);
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
                filterContext.HttpContext.Response.Redirect(result.redirectUrl, true);
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

        public virtual void setCookieValue(ActionExecutingContext filterContext, String JSONString)
        {
            string cookieName = this.getCookieName();
            string cookieValue = Uri.EscapeDataString(JSONString);

#if OLDDOTNET
            HttpCookie newCookie = new HttpCookie(cookieName, cookieValue);
            newCookie.Path = "/";

            filterContext.HttpContext.Response.Cookies.Add(newCookie);
#else
            CookieOptions cookieOptions = new CookieOptions();
            cookieOptions.Path = "/";

            filterContext.HttpContext.Response.Cookies.Append(cookieName, cookieValue, cookieOptions);
#endif
        }

        public virtual String getCookieName()
        {
            return "crowdhandler";
        }
    }
}