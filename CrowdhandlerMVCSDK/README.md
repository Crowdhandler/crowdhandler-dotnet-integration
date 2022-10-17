# Crowdhandler MVC .NET SDK

Protect your .NET MVC Applications with [Crowdhandler](https://www.crowdhandler.com).

[Nuget Package](https://www.nuget.org/packages/Crowdhandler.MVCSDK)
[Github](https://github.com/Crowdhandler)

## Getting Started

### 1) Add Reference to your project

This is easist done using NuGet, find the `Crowdhandler.MVCSDK` package in the NuGet package manager, or via the `dotnet` CLI

```
dotnet add package Crowdhandler.MVCSDK
```

### 2) Add your Crowdhandler API settings

*Web.config*
```
<appSettings>
    <add key="CROWDHANDLER_PUBLIC_KEY" value="YOUR_PUBLIC_KEY" />
    <add key="CROWDHANDLER_PRIVATE_KEY" value="YOUR_PRIVATE_KEY" />
    <add key="CROWDHANDLER_API_ENDPOINT" value="http://api.crowdhandler.com" />
    <add key="CROWDHANDLER_WR_ENDPOINT" value="https://wait.crowdhandler.com"/>
</appSettings>
```

Your API keys can found in your Crowdhandler dashboard. [Click here for more information](https://www.crowdhandler.com/support/solutions/articles/80000138228-introduction-to-the-api)


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

### 4) Configure the `CrowdhandlerFilter` Attribute

The default CrowdhandlerFilterAttribute properties should work in most environments, but if neccessary, custom behaviour can be configured with the following:

**`ApiEndpoint`, `PrivateApiKey` & `PublicApiKey` (string) (default null)**

These API configuration options can be directly injected into the Action Filter, bypassing their default configuration options, which are pulled from `Web.Config`. For more information see the *Configuration* section below

**`FailTrust` (boolean) (default true)**

If false, a user that fails to check-in with CrowdHandler's API will be sent to a safety net waiting room until CrowdHandler is able to make a decision on what to do with them. In this option false you may also want to set the `CROWDHANDLER_SAFETYNET_SLUG` setting (see Configuration section)

If true, users that fail to check-in with CrowdHandler's API will be trusted

[Read more about Trust on Fail](https://www.crowdhandler.com/support/solutions/articles/80000984411-trust-on-fail)

**`GatekeeperType` (Type implementing IGateKeeper) (default null)**

If set, this Class of GateKeeper will be used instead of the default when validating users. Used to implement custom behavior in the GateKeeper validation process.

## Configuration

The following can be configured in the appSettings property of your applications Web.config file

| Value | Description | Required |
| ----- | ----------- | -------- |
| CROWDHANDLER_PUBLIC_KEY | Your Crowdhandler public API key. | Yes |
| CROWDHANDLER_PRIVATE_KEY | Your Crowdhandler private API key. | Yes |
| CROWDHANDLER_API_ENDPOINT | The Crowdhandler API URL | Yes |
| CROWDHANDLER_WR_ENDPOINT | Your Crowdhandler waiting room URL | Yes |
| CROWDHANDLER_API_REQUEST_TIMEOUT | How many seconds to wait for the Crowdhandler API to respond before failing. Default: 3 | No |
| CROWDHANDLER_ROOM_CACHE_TIME | How many seconds to cache your Crowdhandler room configuration for. Set the value to 0 to never cache. Default: 60 | No |
| CROWDHANDLER_SAFETYNET_SLUG | If failTrust is set to false, this waiting room slug will be used as the safety net room | No |

## Customisation
### I only need to validate certain urls on my Controller Action

If you need finer control from the Action Filter, for example you'd like to exclude some urls from validation, you can extend the `CrowdhandlerFilterAttribute` class, implement your own url filtering logic, and then execute it. For example:

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

### I want to store my Room Configuration locally

By default the Crowdhandler room configuration is fetched via the API and then cached in memory for 1 minute. If you would like to bypass this and keep static copy of the Configuation locally, you can do this by implementing/extending your own IGateKeeper class, and passing it's type to the `GatekeeperType` property of the `CrowdhandlerFilterAttribute`. For example:

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