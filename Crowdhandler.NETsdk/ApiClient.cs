using Crowdhandler.NETsdk.JSONTypes;
using Crowdhandler.NETsdk.Utilities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Caching;

namespace Crowdhandler.NETsdk
{
    internal class ApiClient
    {
        // IHttpClientFactory is not available in net45, so we've implemented our own using a timed pool of HttpClient objects.
        //
        // In an ASP Context this list should persist across multiple multiple requests (if this included in an ASP project) and bypass all the
        // problems with creating/disposting of HttpClient objects. For this reason you should be careful when modifying the state of the
        // HttpClient objects in this list, as they may be recycled and used by other threads. For example setting the api key on
        // client.DefaultRequestHeaders would be a bad idea as this could be re-cycled
        internal static LimitedPool<HttpClient> _httpClientPool;

        protected string apiUrl;
        protected string publicApiKey;

        public ApiClient(string apiUrl, string publicApiKey)
        {
            this.publicApiKey = publicApiKey;
            this.apiUrl = apiUrl;

            // From .NET Framework 4.8.0, simply use SecurityProtocolType.Tls13
            // (or rather don't use this code at all from 4.7.1, configure the TLS versions in the OS)
            const SecurityProtocolType tls13 = (SecurityProtocolType)12288;
            ServicePointManager.SecurityProtocol = tls13 | SecurityProtocolType.Tls12;

            var fivemins = new TimeSpan(0, 5, 0);
            _httpClientPool = new LimitedPool<HttpClient>(CreateClientObject, client => client.Dispose(), fivemins);
        }
        protected HttpClient CreateClientObject()
        {
            var client = new HttpClient();

            // All requests to the api should have a shortish timeout to avoid locking up the host application, this can be configured in the AppConfig
            int timeoutSeconds;

            String configTimeout = ConfigurationManager.AppSettings["CROWDHANDLER_API_REQUEST_TIMEOUT"];
            if (configTimeout != null && int.TryParse(configTimeout, out timeoutSeconds))
            {
                timeoutSeconds = int.Parse(configTimeout);
            } else
            {
                // Tryparse will set the value to zero if it's not parsable?!
                timeoutSeconds = 3;
            }
            
            client.Timeout = new TimeSpan(0,0, timeoutSeconds);

            return client;
        }

        public virtual TokenResponse getToken(string url, string token = "notsupplied")
        {
            HttpRequestMessage msg;

            if (token == "notsupplied")
            {
                var postBody = new Dictionary<string, string>();
                postBody.Add("url", url);
                msg = new HttpRequestMessage(HttpMethod.Post, apiUrl + "/v1/requests/")
                {
                    Content = new FormUrlEncodedContent(postBody)
                };
            }
            else

            {
                msg = new HttpRequestMessage(HttpMethod.Get, apiUrl + "/v1/requests/" + token + $"?url={url}");
            }

            msg.Headers.Add("x-api-key", publicApiKey);

            var responseBody = doRequest(msg);

            return JObject.Parse(responseBody)["result"].ToObject<TokenResponse>();

            //return Newtonsoft.Json.JsonConvert.DeserializeObject<TokenResponse>(responseBody);
        }

        public virtual List<RoomConfig> getRoomConfig()
        {
            JObject results = JObject.Parse(getRoomConfigJson());
            // get JSON result objects into a list
            IList<JToken> rawRooms = results["result"].Children().ToList();

            IList<RoomConfig> searchResults = new List<RoomConfig>();
            var rooms = new List<RoomConfig>();
            foreach (JToken result in rawRooms)
            {
                // JToken.ToObject is a helper method that uses JsonSerializer internally
                RoomConfig room = result.ToObject<RoomConfig>();
                rooms.Add(room);
            }

            return rooms;
        }

        /// <summary>
        /// Fetch the Crowdhandler Room Config via the API
        /// </summary>
        /// <returns>JSON Object</returns>
        public virtual string getRoomConfigJson()
        {
            // All requests to the api should have a shortish timeout to avoid locking up the host application, this can be configured in the AppConfig
            int cacheSeconds = 60;

            String configCacheTime = ConfigurationManager.AppSettings["CROWDHANDLER_ROOM_CACHE_TIME"];
            if (configCacheTime != null && int.TryParse(configCacheTime, out cacheSeconds))
            {
                cacheSeconds = int.Parse(configCacheTime);
            }
            else
            {
                // Tryparse will set the value to zero if it's not parsable?!
                cacheSeconds = 60;
            }
            
            var roomCache = MemoryCache.Default;
            var roomCacheKey = $"rooms_${publicApiKey}";
            var json = roomCache[roomCacheKey] as string;

            if (cacheSeconds == 0 || json == null)
            {
                var msg = new HttpRequestMessage(HttpMethod.Get, apiUrl + "/v1/rooms");
                msg.Headers.Add("x-api-key", publicApiKey);
                json = doRequest(msg);

                if (cacheSeconds != 0)
                {
                    var policy = new CacheItemPolicy
                    {
                        AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(cacheSeconds)
                    };
                    var cacheItem = new CacheItem(roomCacheKey, json);
                    roomCache.Set(cacheItem, policy);
                }
            }

            return json;
        }

        // create/fetch a httpClient object from the pool, execute the request and then return it to the pool
        protected string doRequest(HttpRequestMessage request)
        {
            using (var httpClientContainer = _httpClientPool.Get())
            {
                HttpClient client = httpClientContainer.Value;

                // Force this async method to be synchronous, doing this is apparently bad, but I prefer it to making everything async
                var task = Task.Run(() => client.SendAsync(request));
                task.Wait();
                var response = task.Result;

                return response.Content.ReadAsStringAsync().Result;
            }
        }

    }
}
