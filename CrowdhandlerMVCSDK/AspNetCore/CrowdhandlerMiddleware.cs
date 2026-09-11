using System;
using System.Threading.Tasks;
using Crowdhandler.NETsdk;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crowdhandler.MVCSDK.AspNetCore
{
    /// <summary>
    /// Validates every request against CrowdHandler before it reaches the rest of the pipeline. Register with
    /// <c>services.AddCrowdhandler(...)</c> and <c>app.UseCrowdhandler()</c>. Prefer this over the action filter on
    /// ASP.NET Core: it covers Razor Pages, minimal APIs and static files too, and it is fully asynchronous.
    /// </summary>
    public class CrowdhandlerMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IOptionsMonitor<CrowdhandlerOptions> _options;
        private readonly ILogger _logger;
        private readonly CrowdhandlerMiddlewareOptions _middlewareOptions;
        private static readonly string SdkVersion = typeof(CrowdhandlerMiddleware).Assembly.GetName().Version.ToString(3);

        public CrowdhandlerMiddleware(RequestDelegate next, IOptionsMonitor<CrowdhandlerOptions> options, ILoggerFactory loggerFactory, CrowdhandlerMiddlewareOptions middlewareOptions = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = loggerFactory?.CreateLogger("Crowdhandler");
            _middlewareOptions = middlewareOptions ?? new CrowdhandlerMiddlewareOptions();
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var skip = _middlewareOptions.Skip;
            if (skip != null && skip(context))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Multi-tenant hosts resolve settings per request; null means this tenant does not use CrowdHandler.
            CrowdhandlerOptions options;
            var resolver = context.RequestServices.GetService(typeof(ICrowdhandlerOptionsResolver)) as ICrowdhandlerOptionsResolver;
            if (resolver != null)
            {
                options = await resolver.ResolveAsync(context).ConfigureAwait(false);
                if (options == null)
                {
                    await _next(context).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                options = _options.CurrentValue;
            }

            // Verification route used by CrowdHandler support to confirm the integration is live on a domain.
            string statusPath = _middlewareOptions.StatusPath;
            if (!string.IsNullOrEmpty(statusPath) && context.Request.Path.Equals(statusPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.Headers["Cache-Control"] = "public, max-age=60";
                await context.Response.WriteAsync("{\"integration\":\"dotnet\",\"status\":\"ok\",\"version\":\"" + SdkVersion + "\"}").ConfigureAwait(false);
                return;
            }


            IGateKeeper gk;
            try
            {
                gk = options.CreateGateKeeper();
            }
            catch (Exception ex) when (ex is MissingFieldException || ex.InnerException is MissingFieldException)
            {
                throw new InvalidOperationException("CrowdHandler is not configured: set Crowdhandler:PublicApiKey and Crowdhandler:PrivateApiKey (services.AddCrowdhandler(Configuration.GetSection(\"Crowdhandler\"))).", ex);
            }

            var outcome = await CrowdhandlerRequestProcessor.HandleAsync(context, options, gk, _logger).ConfigureAwait(false);
            if (outcome.RedirectUrl != null)
            {
                context.Response.Redirect(outcome.RedirectUrl);
                return;
            }

            await _next(context).ConfigureAwait(false);

            outcome.RecordPerformance(context.Response.StatusCode);
        }
    }

    /// <summary>Middleware-only settings.</summary>
    public class CrowdhandlerMiddlewareOptions
    {
        /// <summary>Return true to bypass CrowdHandler for a request entirely (e.g. health checks, webhooks, internal APIs). Path-based exclusions are usually better expressed with <see cref="CrowdhandlerOptions.Exclusions"/>.</summary>
        public Func<HttpContext, bool> Skip { get; set; }

        /// <summary>Path that answers <c>{"integration":"dotnet","status":"ok"}</c> so CrowdHandler support can verify the integration is deployed. Default <c>/ch/status</c>; null disables it.</summary>
        public string StatusPath { get; set; } = "/ch/status";
    }
}
