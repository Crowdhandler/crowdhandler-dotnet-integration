# Crowdhandler MVC .NET SDK

Protect your .NET MVC Applications with [Crowdhandler](https://www.crowdhandler.com).

[Nuget Package](https://www.nuget.org/packages/Crowdhandler.MVCSDK)
[Github](<RepositoryUrl>https://github.com/Crowdhandler/crowdhandler-dotnet-integration</RepositoryUrl>)

## Getting Started

### 1) Add Reference to your project

This is easiest done using NuGet, find the `Crowdhandler.MVCSDK` package in the NuGet package manager, or via the `dotnet` CLI

```
dotnet add package Crowdhandler.MVCSDK
```

### 2a) Add required Crowdhandler configuration options

There are two different recommended ways of doing this.

*app/web.config*
```cs
<appSettings>
    <add key="CROWDHANDLER_PUBLIC_KEY" value="YOUR_PUBLIC_KEY" />
    <add key="CROWDHANDLER_PRIVATE_KEY" value="YOUR_PRIVATE_KEY" />
</appSettings>
```

Alternatively configuration options can be injected into the CrowdHandler Filter Attribute (step 3).

*ExampleTicketingController.cs*
```cs
  [CrowdhandlerFilter(PublicApiKey = "YOUR_PUBLIC_KEY", PrivateApiKey = "YOUR_PRIVATE_KEY")]
```

Your API keys can found in your Crowdhandler dashboard. [Click here for more information](https://www.crowdhandler.com/support/solutions/articles/80000138228-introduction-to-the-api)

### 2b) Full CrowdHandler configuration options

app/web.config options:

| Value | Description | Required | Type |
| ----- | ----------- | -------- | ---- |
| CROWDHANDLER_PUBLIC_KEY | Your Crowdhandler public API key. | Yes | String |
| CROWDHANDLER_PRIVATE_KEY | Your Crowdhandler private API key. | Yes | String |
| CROWDHANDLER_API_ENDPOINT | The Crowdhandler API URL. Default: https://api.crowdhandler.com | No | String |
| CROWDHANDLER_WR_ENDPOINT | Your Crowdhandler waiting room URL. Default: https://wait.crowdhandler.com | No | String |
| CROWDHANDLER_EXCLUSIONS_REGEX | Regex pattern for URLs that should not be sent to the waiting room. Default: `@"^((?!.*\?).*(\.(avi|css|eot|gif|ICO|jpg|jpeg|js|json|mov|mp4|mpeg|mpg|og[g|v]|pdf|png|svg|ttf|txt|wmv|woff|woff2|xml)))$"` | No | String |
| CROWDHANDLER_API_REQUEST_TIMEOUT | How many seconds to wait for the Crowdhandler API to respond before failing. Default: 3 | No | String |
| CROWDHANDLER_ROOM_CACHE_TIME | How many seconds to cache your Crowdhandler room configuration for. Set the value to 0 to never cache. Default: 60 | No | String |
| CROWDHANDLER_SAFETYNET_SLUG | If failTrust is set to false, this waiting room slug will be used as the safety net room | No | String |

CrowdHandler Filter Attribute options:

| Value | Description | Required | Type |
| ----- | ----------- | -------- | ---- |
| PublicApiKey | Your Crowdhandler public API key. | Yes | String |
| PrivateApiKey | Your Crowdhandler private API key. | Yes | String |
| ApiEndpoint | The Crowdhandler API URL. Default: https://api.crowdhandler.com | No | String |
| WaitingRoomEndpoint | Your Crowdhandler waiting room URL. Default: https://wait.crowdhandler.com | No | String |
| Exclusions | Regex pattern for URLs that should not be sent to the waiting room. Default: `@"^((?!.*\?).*(\.(avi|css|eot|gif|ICO|jpg|jpeg|js|json|mov|mp4|mpeg|mpg|og[g|v]|pdf|png|svg|ttf|txt|wmv|woff|woff2|xml)))$"` | No | String |
| APIRequestTimeout | How many seconds to wait for the Crowdhandler API to respond before failing. Default: 3 | No | String |
| RoomCacheTTL | How many seconds to cache your Crowdhandler room configuration for. Set the value to 0 to never cache. Default: 60 | No | String |
| FailTrust | If true, users that fail to check-in with CrowdHandler's API will be trusted. Read more about Trust on Fail - https://www.crowdhandler.com/docs/80000984411-trust-on-fail. Default: true | No | Boolean |
| SafetyNetSlug | If failTrust is set to false, this waiting room slug will be used as the safety net room. | No | String |
| DebugMode | For local development. Default: false | No | Boolean |
| GateKeeperType | If set, this Class of GateKeeper will be used instead of the default when validating users. Used to implement custom behavior in the GateKeeper validation process. Default: null | No | Type implementing IGateKeeper |

\* FailTrust can only be set via Filter Attribute
** DebugMode can only be set via Filter Attribute
*** GateKeeperType can only be set via Filter Attribute

### 3) Apply the Crowdhandler Filter Attribute to your Controller Actions

*ExampleTicketingController.cs*
```cs
using Crowdhandler.MVCSDK;

namespace MyTicketingApp.Controllers
{
    public class TicketingController : Controller
    {
        [CrowdhandlerFilter]
        public ActionResult Index()
        {
            return View();
        }
    }
}
```

Custom URL exclusion pattern example:

*ExampleTicketingController.cs*
```cs
using Crowdhandler.MVCSDK;

namespace MyTicketingApp.Controllers
{
    public class TicketingController : Controller
    {
        [CrowdhandlerFilter(Exclusions = @"^(\/contact-us.*)|((?!.*\?).*(\.(avi|css|eot|gif|ICO|jpg|jpeg|js|json|mov|mp4|mpeg|mpg|og[g|v]|pdf|png|svg|ttf|txt|wmv|woff|woff2|xml)))$")]
        public ActionResult Index()
        {
            return View();
        }
    }
}
```

### 4) Advanced Customisation

If you need finer control from the Action Filter, you can extend the `CrowdhandlerFilterAttribute` class, implement your own logic, and then execute it. For example:

*MyCustomLogicFilterAttribute.cs*
```cs
public class MyCustomLogicFilterAttribute : CrowdhandlerFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext filterContext)
    {
        var url = filterContext.HttpContext.Request.Url;

        // do not attempt to validate the json events feed
        if (url.AbsolutePath.StartsWith("/events/feed"))
        {
            // return to controller
            return;
        }

        // Run validation
        base.OnActionExecuting(filterContext);
    }
}
```

Then update your controller to use this new Filter Attribute

*ExampleTicketingController.cs*
```cs
public class TicketingController : Controller
{
    [MyCustomLogicFilter]
    public ActionResult Index()
    {
        return View();
    }
}
```

### Extend/Override the IGateKeeper class

By default the Crowdhandler room configuration is fetched via the API and then cached in memory for 1 minute. If you would like to bypass this and keep static copy of the Configuation locally, you can do this by extending/implementing your own IGateKeeper class, and passing it's type to the `GatekeeperType` property of the `CrowdhandlerFilterAttribute`. For example:

*MyCustomGateKeeper.cs*
```cs
using Crowdhandler.NETsdk;
using Crowdhandler.NETsdk.JSONTypes;
using Newtonsoft.Json;

public class MyCustomGateKeeper : GateKeeper
{
    public override List<RoomConfig> getRoomConfig()
    {
        return JsonConvert.DeserializeObject<List<RoomConfig>>(System.IO.File.ReadAllText(@"path_to_roomconfig.json"));
    }
}
```

*ExampleTicketingController.cs*
```cs
public class TicketingController : Controller
{
    [CrowdhandlerFilter(GatekeeperType = typeof(MyCustomGateKeeper))]
    public ActionResult Index()
    {
        return View();
    }
}