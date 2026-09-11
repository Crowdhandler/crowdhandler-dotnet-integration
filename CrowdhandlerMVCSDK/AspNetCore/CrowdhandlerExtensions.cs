using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crowdhandler.MVCSDK.AspNetCore
{
    public static class CrowdhandlerServiceCollectionExtensions
    {
        /// <summary>Register CrowdHandler options from a configuration section (typically <c>Configuration.GetSection("Crowdhandler")</c>).</summary>
        public static IServiceCollection AddCrowdhandler(this IServiceCollection services, IConfiguration configuration)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            services.AddOptions();
            services.Configure<CrowdhandlerOptions>(configuration);
            return services;
        }

        /// <summary>Register CrowdHandler options in code.</summary>
        public static IServiceCollection AddCrowdhandler(this IServiceCollection services, Action<CrowdhandlerOptions> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            services.AddOptions();
            services.Configure(configure);
            return services;
        }

        /// <summary>
        /// Multi-tenant: resolve CrowdHandler settings per request (typically by looking the tenant up from the host name).
        /// Return null from the resolver to bypass CrowdHandler for that request. Cached tenant settings may be returned
        /// as the same instance each time; the SDK does not modify them.
        /// </summary>
        public static IServiceCollection AddCrowdhandler(this IServiceCollection services, Func<Microsoft.AspNetCore.Http.HttpContext, Task<CrowdhandlerOptions>> resolve)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            services.AddOptions();
            services.AddSingleton<ICrowdhandlerOptionsResolver>(new DelegateOptionsResolver(resolve));
            return services;
        }

        /// <summary>Multi-tenant, synchronous form of the per-request resolver.</summary>
        public static IServiceCollection AddCrowdhandler(this IServiceCollection services, Func<Microsoft.AspNetCore.Http.HttpContext, CrowdhandlerOptions> resolve)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            return services.AddCrowdhandler(ctx => Task.FromResult(resolve(ctx)));
        }

        /// <summary>Register CrowdHandler options from configuration, then adjust them in code.</summary>
        public static IServiceCollection AddCrowdhandler(this IServiceCollection services, IConfiguration configuration, Action<CrowdhandlerOptions> configure)
        {
            services.AddCrowdhandler(configuration);
            services.PostConfigure(configure);
            return services;
        }
    }

    public static class CrowdhandlerApplicationBuilderExtensions
    {
        /// <summary>
        /// Validate every request with CrowdHandler. Place it early in the pipeline, after <c>UseForwardedHeaders</c> if you
        /// use it and before <c>UseStaticFiles</c>/<c>UseRouting</c> so the waiting room covers everything.
        /// </summary>
        public static IApplicationBuilder UseCrowdhandler(this IApplicationBuilder app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            return app.UseMiddleware<CrowdhandlerMiddleware>();
        }

        /// <summary>Validate every request with CrowdHandler, with a predicate to skip some requests entirely.</summary>
        public static IApplicationBuilder UseCrowdhandler(this IApplicationBuilder app, Func<Microsoft.AspNetCore.Http.HttpContext, bool> skip)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            return app.UseMiddleware<CrowdhandlerMiddleware>(new CrowdhandlerMiddlewareOptions { Skip = skip });
        }
    }
}
