using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crowdhandler.NETsdk;
using Crowdhandler.NETsdk.JSONTypes;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Crowdhandler.NETsdk.Tests
{
    /// <summary>Records every request the SDK makes and answers with whatever the current test scripted.</summary>
    public class StubHandler : HttpMessageHandler
    {
        public static Func<HttpRequestMessage, string, HttpResponseMessage> Respond = (req, body) => Json(200, "{\"result\":{}}");
        public static List<(HttpRequestMessage request, string body)> Requests = new List<(HttpRequestMessage, string)>();
        /// <summary>Delay applied to /v1/requests calls, honouring cancellation, to simulate a slow API.</summary>
        public static int DelayRequestsMs;

        static StubHandler()
        {
            ApiClient.HttpMessageHandlerFactory = () => new StubHandler();
        }

        public static void Reset()
        {
            Requests.Clear();
            DelayRequestsMs = 0;
            Respond = (req, body) => Json(200, "{\"result\":{}}");
        }

        public static HttpResponseMessage Json(int status, string json)
        {
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            Requests.Add((request, body));
            if (DelayRequestsMs > 0 && request.RequestUri.AbsolutePath.Contains("/v1/requests"))
            {
                await Task.Delay(DelayRequestsMs, cancellationToken);
            }
            return Respond(request, body);
        }
    }

    public static class Fixture
    {
        public const string PrivateKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        public const string Slug = "main-sale";
        public const string QueueActivatesOn = "2024-01-01T00:00:00Z";
        public const string Token = "tok0M7SBFAp9J8kK";

        private static int _keyCounter;

        /// <summary>A unique public key per test so the process-wide room cache never leaks between tests.</summary>
        public static string NewPublicKey() => "pub" + Interlocked.Increment(ref _keyCounter).ToString("D6") + new string('x', 55);

        public static GateKeeper NewGateKeeper(string publicKey = null, string roomCacheTTL = "0", string exclusions = null)
        {
            StubHandler.Reset();
            return new GateKeeper(publicKey ?? NewPublicKey(), PrivateKey, "https://api.test", "https://wait.test", exclusions, "3", roomCacheTTL, null);
        }

        public static RoomConfig Room(string domain = "https://www.example.com", string patternType = "all", string urlPattern = null, int timeout = 15, string checkout = null, string slug = Slug)
        {
            return new RoomConfig
            {
                id = "rom_1",
                Slug = slug,
                domain = domain,
                patternType = patternType,
                urlPattern = urlPattern,
                queueActivatesOn = DateTime.Parse(QueueActivatesOn, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                timeout = timeout,
                checkout = checkout,
            };
        }

        /// <summary>Independent implementation of the CrowdHandler signature, mirroring the API's req_generate_hash_2.</summary>
        public static string Sha256(string s)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string Signature(string requested, string token = Token, string slug = Slug, string queueActivatesOn = QueueActivatesOn, string privateKey = PrivateKey)
        {
            return Sha256(Sha256(privateKey) + slug + queueActivatesOn + token + requested);
        }

        public static string TouchedSig(ulong touched) => Sha256(Sha256(PrivateKey) + touched);

        public static string RoomsJson(params RoomConfig[] rooms)
        {
            var sb = new StringBuilder("{\"result\":[");
            for (int i = 0; i < rooms.Length; i++)
            {
                var r = rooms[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"rom_").Append(i).Append("\",\"slug\":\"").Append(r.Slug).Append("\",\"urlPattern\":").Append(r.urlPattern == null ? "\"\"" : "\"" + r.urlPattern.Replace("\\", "\\\\") + "\"")
                  .Append(",\"patternType\":\"").Append(r.patternType).Append("\",\"queueActivatesOn\":\"").Append(QueueActivatesOn)
                  .Append("\",\"domain\":\"").Append(r.domain).Append("\",\"safetyMode\":0,\"timeout\":").Append(r.timeout)
                  .Append(",\"stock\":null,\"checkout\":").Append(r.checkout == null ? "\"\"" : "\"" + r.checkout.Replace("\\", "\\\\") + "\"").Append(",\"ttl\":59}");
            }
            return sb.Append("]}").ToString();
        }

        public static string TokenJson(bool promoted, string token = Token, string requested = null, string hash = null, string slug = Slug, string responseID = "resp0000000000000000000000000001", int status = 1)
        {
            requested = requested ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            hash = hash ?? (promoted ? Signature(requested, token, slug) : null);
            return "{\"result\":{\"status\":" + status + ",\"token\":" + (token == null ? "null" : "\"" + token + "\"") + ",\"title\":\"Main sale\",\"position\":" + (promoted ? "null" : "42")
                + ",\"promoted\":" + (promoted ? 1 : 0) + ",\"urlRedirect\":null,\"onsale\":\"" + QueueActivatesOn + "\",\"message\":null,\"slug\":\"" + slug + "\",\"priority\":null,\"priorityAvailable\":0,\"logo\":null,\"stock\":null,\"responseID\":"
                + (responseID == null ? "null" : "\"" + responseID + "\"") + ",\"captchaRequired\":0,\"rate\":100,\"sessionsExpire\":0,\"sessionsTimeout\":15,\"deployment\":\".net\",\"domain\":\"https://www.example.com\",\"emailAvailable\":0,\"requested\":\"" + requested + "\",\"hash\":"
                + (hash == null ? "null" : "\"" + hash + "\"") + ",\"ttl\":60}}";
        }

        public static string CookieJson(string token = Token, string[] sigs = null, string[] gens = null, ulong? touched = null, string touchedSig = null)
        {
            ulong t = touched ?? Util.DateTimeToUnixTimeStampMs(DateTime.UtcNow);
            string ts = touchedSig ?? TouchedSig(t);
            var sb = new StringBuilder("{\"integration\":\"dotnet\",\"tokens\":[{\"token\":\"" + token + "\",\"touched\":" + t + ",\"touchedSig\":\"" + ts + "\",\"signatures\":[");
            if (sigs != null)
            {
                for (int i = 0; i < sigs.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"gen\":\"").Append(gens[i]).Append("\",\"sig\":\"").Append(sigs[i]).Append("\"}");
                }
            }
            return sb.Append("]}]}").ToString();
        }

        /// <summary>Route /v1/rooms to the given rooms and /v1/requests to the given token response.</summary>
        public static void ScriptApi(string roomsJson, string tokenJson, int tokenStatus = 200)
        {
            StubHandler.Respond = (req, body) =>
            {
                if (req.RequestUri.AbsolutePath.EndsWith("/v1/rooms")) return StubHandler.Json(200, roomsJson);
                if (req.RequestUri.AbsolutePath.Contains("/v1/requests")) return StubHandler.Json(tokenStatus, tokenJson);
                return StubHandler.Json(200, "{\"result\":{}}");
            };
        }

        public static IEnumerable<HttpRequestMessage> RequestsTo(string pathFragment)
        {
            foreach (var r in StubHandler.Requests)
            {
                if (r.request.RequestUri.AbsolutePath.Contains(pathFragment)) yield return r.request;
            }
        }
    }
}
