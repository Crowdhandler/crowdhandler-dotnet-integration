# CrowdHandler .NET integration

![CrowdHandler](https://www.crowdhandler.com/assets/ch_logos/ch-logo-stacked-reversed.png)

Virtual waiting rooms for .NET applications, by [CrowdHandler](https://www.crowdhandler.com).

## Packages

| Package | Use it for | Targets |
|---|---|---|
| [`Crowdhandler.MVCSDK`](https://www.nuget.org/packages/Crowdhandler.MVCSDK) | ASP.NET Core (middleware + action filter) and ASP.NET MVC 5 (action filter). **Start here.** | `net8.0`, `net6.0`, `net472` |
| [`Crowdhandler.NETsdk`](https://www.nuget.org/packages/Crowdhandler.NETsdk) | The validation core, for any other .NET host. | `netstandard2.0`, `net472` |

## Quick start (ASP.NET Core)

```
dotnet add package Crowdhandler.MVCSDK
```

```json
// appsettings.json
{ "Crowdhandler": { "PublicApiKey": "YOUR_PUBLIC_KEY", "PrivateApiKey": "YOUR_PRIVATE_KEY" } }
```

```csharp
// Program.cs
using Crowdhandler.MVCSDK.AspNetCore;

builder.Services.AddCrowdhandler(builder.Configuration.GetSection("Crowdhandler"));
// ...
app.UseCrowdhandler();   // before UseStaticFiles / UseRouting
```

Then add the CrowdHandler JavaScript to your pages and set the domain's deployment type to **.NET** in the control panel. The [integration guide](CrowdhandlerMVCSDK/README.md) covers MVC 5, configuration and failure handling.

## Documentation

* **Integration guide and configuration reference:** [CrowdhandlerMVCSDK/README.md](CrowdhandlerMVCSDK/README.md)
* **SDK API reference:** [Crowdhandler.NETsdk/README.md](Crowdhandler.NETsdk/README.md)
* **Runnable example:** [samples/AspNetCoreSample](samples/AspNetCoreSample)
* **Testing:** [TESTING.md](TESTING.md)
* **Changes:** [CHANGELOG.md](CHANGELOG.md)
* **Quick start on crowdhandler.com:** [.NET Integration - Quick Start Guide](https://www.crowdhandler.com/docs/integrations/net/net-integration-quick-start-guide)

## Design

* **Hybrid validation.** Signatures issued by CrowdHandler are verified locally with the private key. The API is only called when there is no valid signature. Room configuration is cached for 60 s and the last good copy is kept through API outages.
* **Fail-closed on bad input, fail-open only on outages.** Tampered cookies or URL parameters never throw; they fall through to the normal queue. Only transport-level failures reach the `FailTrust` policy. API rejections (4xx) always send the visitor to the waiting room and are logged.
* **Cookie compatibility.** The `crowdhandler` cookie has the same shape as the JavaScript SDK and edge integrations. `touched` is written in seconds, as 1.0.x did, so mixed 1.0.x / 1.1 server farms can be upgraded one node at a time. Both seconds and milliseconds are read.
* **Wire contract:** see [Crowdhandler.NETsdk/README.md](Crowdhandler.NETsdk/README.md#wire-contract).

## Repository layout

```
Crowdhandler.NETsdk/        core library: GateKeeper, ApiClient, JSON types
CrowdhandlerMVCSDK/         ASP.NET adapters: filter attribute, ASP.NET Core middleware and options
  AspNetCore/               ASP.NET Core only (excluded from the net472 build)
tests/                      xunit test projects: unit, ASP.NET Core adapters, and real-API integration (skipped without credentials)
samples/AspNetCoreSample/   minimal ASP.NET Core app wired up end to end
tools/FakeCrowdhandlerApi/  stand-in API for load and failure testing
.github/workflows/ci.yml    build, test and pack on every push and pull request
```

## Building and testing

Requires the .NET 10 SDK. The `net472` targets build on any platform via reference assemblies; Windows and Visual Studio are not needed.

```
dotnet build Crowdhandler.NETsdk.sln -c Release
dotnet test  Crowdhandler.NETsdk.sln -c Release
```

Packages are produced on every Release build in `Crowdhandler.NETsdk/bin/Release/` and `CrowdhandlerMVCSDK/bin/Release/`.

## Releasing

1. Bump `<Version>` in both `.csproj` files (keep them in step) and add a section to `CHANGELOG.md`.
2. `dotnet build -c Release`, then push the `.nupkg` and `.snupkg` from `*/bin/Release/`:
   `dotnet nuget push "**/bin/Release/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key $NUGET_API_KEY`
3. Tag the commit `v<version>`.

Symbols are published as `.snupkg` with SourceLink, so consumers can step into the SDK.

## License

BSD 3-Clause. See [LICENSE.txt](LICENSE.txt).
