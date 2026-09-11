using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Crowdhandler.MVCSDK.AspNetCore
{
    /// <summary>
    /// Supplies CrowdHandler settings per request, for hosts that serve many tenants from one application.
    /// Register one with <c>services.AddCrowdhandler(HttpContext =&gt; ...)</c>. Return null to bypass CrowdHandler for
    /// the request entirely (a tenant that does not use it).
    /// </summary>
    public interface ICrowdhandlerOptionsResolver
    {
        Task<CrowdhandlerOptions> ResolveAsync(HttpContext context);
    }

    internal sealed class DelegateOptionsResolver : ICrowdhandlerOptionsResolver
    {
        private readonly Func<HttpContext, Task<CrowdhandlerOptions>> _resolve;
        public DelegateOptionsResolver(Func<HttpContext, Task<CrowdhandlerOptions>> resolve) { _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve)); }
        public Task<CrowdhandlerOptions> ResolveAsync(HttpContext context) => _resolve(context);
    }
}
