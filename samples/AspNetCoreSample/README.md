# ASP.NET Core sample

A minimal ASP.NET Core 8 site protected by CrowdHandler. It shows both the middleware (whole site) and the action filter (single action).

```
cd samples/AspNetCoreSample
dotnet user-secrets set "Crowdhandler:PublicApiKey"  "<your public key>"
dotnet user-secrets set "Crowdhandler:PrivateApiKey" "<your private key>"
dotnet run
```

Open https://localhost:7443/tickets. With a waiting room configured for the domain in the CrowdHandler control panel, you are sent through it on first visit and validated locally afterwards. Watch the `Crowdhandler` log category for decisions and errors.

Set `Crowdhandler:MultiTenant` to `true` to run the sample with the per-request options resolver instead of fixed options. Only requests for `Crowdhandler:TestHost` (default `localhost`) are then validated; other hosts pass straight through.

For an end-to-end run through a public tunnel, and for the real-API integration tests, see [TESTING.md](../../TESTING.md).
