# Fake CrowdHandler API

A stand-in for `api.crowdhandler.com` for load and failure testing without an account. It serves `/v1/rooms`, `/v1/requests` and `/v1/responses` with the real response shapes, and signs hashes with the private key you give it, so the SDK's local validation works against it. `/stats` reports call counts; `POST /stats/reset` clears them.

```
FAKE_PRIVATE_KEY=<any 64 hex> FAKE_DOMAIN=https://localhost:5080 dotnet run --urls http://localhost:5090
```

## Options

Set as environment variables at startup, or per call as a query string on any request.

| Environment variable | Query string | Default | Effect |
|---|---|---|---|
| `FAKE_PRIVATE_KEY` | | a fixed test key | Private key used to sign hashes. Must match the SDK's `PrivateApiKey`. |
| `FAKE_DOMAIN` | | `https://localhost:5080` | Domain of the single room served by `/v1/rooms`. |
| `FAKE_TIMEOUT_MINUTES` | | `20` | Room timeout. |
| `FAKE_DELAY_MS` | `delay=<ms>` | `0` | Delay before responding. |
| `FAKE_HTTP_STATUS` | `status=<http status>` | `200` | HTTP status to return. |
| `FAKE_PROMOTED` | `promoted=0\|1` | `1` | Whether visitors are promoted. |
| `FAKE_API_STATUS` | `apistatus=<0..6>` | `1` | The `status` field in the session response. |

See [TESTING.md](../../TESTING.md#load) for the load-test recipe.
