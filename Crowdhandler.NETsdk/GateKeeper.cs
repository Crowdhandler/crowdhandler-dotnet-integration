using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Configuration;
using Crowdhandler.NETsdk.JSONTypes;
using Newtonsoft.Json.Linq;
using System.Net;

namespace Crowdhandler.NETsdk
{
    /// <summary>
    /// The core of the Crowdhandler .NET SDK, containing a generic validation API that can be dropped into other projects. 
    /// </summary>
    public class GateKeeper : IGateKeeper
    {
        /// <summary>
        /// Crowdhandler API URL
        /// </summary>
        virtual public string ApiEndpoint { get; set; }

        /// <summary>
        /// Crowdhandler public API Key
        /// </summary>
        virtual public string PublicApiKey { get; set; }

        /// <summary>
        /// Crowdhandler private API Key
        /// </summary>
        virtual public string PrivateApiKey { get; set; }

        /// <summary>
        /// Crowdhandler Waiting room URL
        /// </summary>
        virtual public string WaitingRoomEndpoint { get; set; }

        /// <summary>
        /// Crowdhandler Waiting room URL
        /// </summary>
        virtual public String Exclusions { get; set; }

        /// <summary>
        /// Crowdhandler API Request Timeout in Seconds
        /// </summary>
        virtual public String APIRequestTimeout { get; set; }

        /// <summary>
        /// Crowdhandler RoomCache TTL
        /// </summary>
        virtual public String RoomCacheTTL { get; set; }



        public GateKeeper(String publicKey = null, String privateKey = null, String apiEndpoint = null, String waitingRoomEndpoint = null, String exclusions = null, String apiRequestTimeout = null, String roomCacheTTL = null, String safetyNetSlug = null)
        {
            this.PublicApiKey = publicKey ?? this.getConfigValue("CROWDHANDLER_PUBLIC_KEY", true);
            this.PrivateApiKey = privateKey ?? this.getConfigValue("CROWDHANDLER_PRIVATE_KEY", true);
            this.ApiEndpoint = apiEndpoint ?? this.getConfigValue("CROWDHANDLER_API_ENDPOINT", false) ?? "https://api.crowdhandler.com";
            this.WaitingRoomEndpoint = waitingRoomEndpoint ?? this.getConfigValue("CROWDHANDLER_WR_ENDPOINT", false) ?? "https://wait.crowdhandler.com";
            this.Exclusions = exclusions ?? this.getConfigValue("CROWDHANDLER_EXCLUSIONS_REGEX", false) ?? @"^((?!.*\?).*(\.(avi|css|eot|gif|ICO|jpg|jpeg|js|json|mov|mp4|mpeg|mpg|og[g|v]|pdf|png|svg|ttf|txt|wmv|woff|woff2|xml)))$";
            this.APIRequestTimeout = apiRequestTimeout ?? this.getConfigValue("CROWDHANDLER_API_REQUEST_TIMEOUT", false) ?? "3";
            this.RoomCacheTTL = roomCacheTTL ?? this.getConfigValue("CROWDHANDLER_ROOM_CACHE_TIME", false) ?? "60";
        }

        public struct ValidateResult
        {

            public string Action { get; set; }
            public string redirectUrl { get; set; }
            public string targetUrl { get; set; }
            public string bustCookie { get; set; }
            public bool setCookie { get; set; }
            public string cookieValue { get; set; }
            public string code { get; set; }
            public string token { get; set; }
            public bool expired { get; set; }
        }
        /// <summary>
        /// Validate a url against a set of Crowdhandler rooms
        /// </summary>
        /// <param name="url">The URL to test</param>
        /// <param name="CookieJSON">A JSON formatted string containing validation information. If not provided, validation is attempted against parameters provided in the URL query string</param>
        /// <param name="room">A set of Crowdhandler room configurations, if not provided, these are fetched using your API key via HTTP</param>
        /// <returns>A <see cref="ValidateResult"/> object containing the result of the validation and additional data</returns>
        public virtual ValidateResult Validate(Uri url, String userAgent, String language, String ipAddress, String CookieJSON = "", RoomConfig room = null)
        {
            /*
             Urls look like this: https://www.crowdchef.net/?ch-id=tok0M7SBFAp9J8kK&ch-id-signature=73264cf4d7c5609377fd5ce3e1b7f55189c2f432e83ec11a49757b93a1eda1d8&ch-requested=2022-07-27T11%3A16%3A13Z&ch-code=&ch-fresh=true
             */

            DateTime requestStartTime = DateTime.UtcNow;

            // Step 1: parse the URL and pull out any query params we want
            String targetUrl = url.Scheme + "://" + url.Host + url.PathAndQuery;
            string cleanedUrl = null;
            bool redirectToCleanUrl = false;

            String chCode = "";
            String chId = "";
            String chIdSignature = "";
            String chPublicKey = "";
            String chRequestedStr = "";

            // pull out a bunch of query params from the url if they exist
            if (url.Query != null && url.Query != "")
            {
                var queryDictionary = Util.parseUrlQueries(url.Query);

                chCode = queryDictionary.ContainsKey("ch-code") ? queryDictionary["ch-code"] : "";
                chId = queryDictionary.ContainsKey("ch-id") ? queryDictionary["ch-id"] : "";
                chIdSignature = queryDictionary.ContainsKey("ch-id-signature") ? queryDictionary["ch-id-signature"] : "";
                chPublicKey = queryDictionary.ContainsKey("ch-public-key") ? queryDictionary["ch-public-key"] : "";
                chRequestedStr = queryDictionary.ContainsKey("ch-requested") ? Uri.UnescapeDataString(queryDictionary["ch-requested"]) : "";


                if (chCode == "undefined" || chCode == "null")
                {
                    chCode = "";
                }
                if (chId == "undefined" || chId == "null")
                {
                    chId = "";
                }
                if (chIdSignature == "undefined" || chIdSignature == "null")
                {
                    chIdSignature = "";
                }
                if (chPublicKey == "undefined" || chPublicKey == "null")
                {
                    chPublicKey = "";
                }
                if (chRequestedStr == "undefined" || chRequestedStr == "null")
                {
                    chRequestedStr = "";
                }

                var queryParamCount = queryDictionary.Count();

                // Recreate the url but with the ch parts removed for the cleaned version
                queryDictionary.Remove("ch-code");
                queryDictionary.Remove("ch-fresh");
                queryDictionary.Remove("ch-id");
                queryDictionary.Remove("ch-id-signature");
                queryDictionary.Remove("ch-public-key");
                queryDictionary.Remove("ch-requested");

                String cleanQueryString = String.Join("&", queryDictionary.Select(p => $"{p.Key}={p.Value}"));

                if (cleanQueryString.Length > 0)
                {
                    cleanedUrl = url.Scheme + "://" + url.Host + url.AbsolutePath + "?" + cleanQueryString;
                }
                else
                {
                    cleanedUrl = url.Scheme + "://" + url.Host + url.AbsolutePath;
                }

                // if we ended up clearing ch query params up, that means we should redirect
                if (queryParamCount > queryDictionary.Count())
                {
                    redirectToCleanUrl = true;
                }
            }

            // Step 2: Get cookie data
            CookieData cookieData = this.getCookieData(CookieJSON);

            // Step 3: Figure out the signature and token based on the URL or the cookie and pick the right one

            String token = "";
            String exactSignature = "";
            List<CookieSignature> candidateSignatures = null;


            //If the chID is not present, use the crowdhandlerCookieValue
            if (chId != "")
            {
                token = chId;
            }
            else if (cookieData != null && cookieData.tokens.Count > 0)
            {
                token = cookieData.tokens.Last().token;
            }

            // Step 4: Decide if we should proceed or not

            // Handle checkout busting 
            string checkoutBusted = this.CheckoutBuster(url.Host, url.PathAndQuery, targetUrl, userAgent, language, ipAddress, token);

            // If the URL matches the exclusion regex, return "allow"
            if (this.Exclusions != null)
            {
                Regex exclusions;

                try
                {
                    exclusions = new Regex(Exclusions);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException("Exclusions value is not a valid regular expression");
                }


                if (exclusions.IsMatch(url.PathAndQuery))
                {
                    return new ValidateResult()
                    {
                        Action = "allow",
                        bustCookie = checkoutBusted
                    };
                }
            }

            // Figure out the room from the URL if it's not provided
            if (room == null)
            {
                room = this.IsRoomMatch(url.Host, url.PathAndQuery);
            }

            // if we can't match against a room we can't validate anything, so we send you through
            if (room == null)
            {
                return new ValidateResult()
                {
                    Action = "allow",
                    bustCookie = checkoutBusted
                };
            }


            // Step 5: Validate the signatures

            //If the chIDSignature is not present, use the crowdhandlerCookieValue
            if (chIdSignature != "")
            {
                exactSignature = chIdSignature;
            }
            else if (cookieData != null && cookieData.tokens.Count > 0)
            {
                candidateSignatures = cookieData.tokens.Last().signatures;
            }

            ValidateSignatureResponse sigResponse = new ValidateSignatureResponse() { expired = false, success = false }; // treat the validation as failed by default

            if (exactSignature != "")
            {
                sigResponse = this.ValidateSignature(exactSignature, DateTime.Parse(chRequestedStr).ToUniversalTime(), token, room);

            }
            else if (candidateSignatures != null && candidateSignatures.Count > 0)
            {
                sigResponse = this.ValidateSignature(candidateSignatures, cookieData, token, room);
            }

            // Step 5a: Failure!
            if (sigResponse.success == false)
            {
                // Get a token from the API

                if (token == "")
                {
                    token = "notsupplied";
                }

                var api = this.GetApiClient();
                var newTokenResult = api.getToken(targetUrl, userAgent, language, ipAddress, token);

                if (newTokenResult.promoted == false)
                {
                    var redirectUrl = $"{this.WaitingRoomEndpoint}/{newTokenResult.slug}?url={Uri.EscapeDataString(targetUrl)}&ch-code={chCode}&ch-id={newTokenResult.token}&ch-public-key={this.PublicApiKey}";
                    return new ValidateResult()
                    {
                        Action = "redirect",
                        redirectUrl = redirectUrl,
                        targetUrl = targetUrl,
                        token = newTokenResult.token,
                        code = chCode,
                        expired = sigResponse.expired
                    };
                }

                token = newTokenResult.token;
                exactSignature = newTokenResult.hash;
                chRequestedStr = newTokenResult.requested?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

            }

            // Step 5b: Success! We're validated, set up a new cookie

            CookieData newCookie = new CookieData();
            newCookie.integration = "dotnet";

            // do we have existing cookie data? if so start with their tokens
            if (cookieData != null)
            {
                newCookie.tokens = new List<CookieToken>();
                newCookie.tokens.AddRange(cookieData.tokens);
            }

            bool isNewToken = false;

            if (newCookie.tokens == null || newCookie.tokens.Count == 0 || newCookie.tokens.Last().token != token)
            {
                isNewToken = true;
            }

            // if the signature in the query string is not one of the tokens we already have, we need to add it to the list
            bool createNewSignature = exactSignature != "" && newCookie.tokens != null && newCookie.tokens.Count > 0 && newCookie.tokens.Last().signatures.Any(s => s.sig.Equals(exactSignature)) == false;

            if (createNewSignature)
            {
                newCookie.tokens.Last().signatures.Add(new CookieSignature() { gen = DateTime.Parse(chRequestedStr).ToUniversalTime(), sig = exactSignature });
            }

            var requestStartUnixTime = Util.DateTimeToUnixTimeStamp(requestStartTime);
            String startTimeHash = Util.SHA256Hash($"{Util.SHA256Hash(this.PrivateApiKey)}{requestStartUnixTime}");
            if (isNewToken)
            {
                //Reset the token array
                newCookie.tokens = new List<CookieToken>();
                newCookie.tokens.Add(new CookieToken()
                {
                    token = token,
                    touched = requestStartUnixTime,
                    touchedSig = startTimeHash,
                    signatures = new List<CookieSignature> { new CookieSignature() { gen = DateTime.Parse(chRequestedStr).ToUniversalTime(), sig = exactSignature } },
                });
            }
            else
            {
                //Update the last token with updated values
                //tokenObjects[tokenObjects.length - 1].signatures = signatures;
                newCookie.tokens.Last().touched = requestStartUnixTime;
                newCookie.tokens.Last().touchedSig = startTimeHash;
            }

            var cookieStr = Newtonsoft.Json.JsonConvert.SerializeObject(newCookie);

            if (redirectToCleanUrl)
            {
                // At this point, the cookie should be set, so redirect to the same page with the ch params cleared
                return new ValidateResult()
                {
                    Action = "redirect",
                    bustCookie = "not-busted",
                    redirectUrl = cleanedUrl ?? targetUrl,
                    setCookie = true,
                    cookieValue = cookieStr
                };
            }

            return new ValidateResult()
            {
                Action = "allow",
                bustCookie = checkoutBusted,
                setCookie = true,
                cookieValue = cookieStr
            };
        }

        public struct ValidateSignatureResponse
        {
            public Boolean success;
            public Boolean expired;
        }

        // If we only have cookie data use this
        public virtual ValidateSignatureResponse ValidateSignature(List<CookieSignature> CandidateSignatures, CookieData cookie, String token, RoomConfig room)
        {
            String hashedPrivateKey = Util.SHA256Hash(this.PrivateApiKey);
            String roomActiveDateFormatted = room.queueActivatesOn.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            List<String> generatedHistory = CandidateSignatures.ConvertAll<String>(c => c.gen.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
            generatedHistory.Reverse();
            List<String> hashCandidates = generatedHistory.ConvertAll<String>(gen => $"{hashedPrivateKey}{room.Slug}{roomActiveDateFormatted}{token}{gen}");

            foreach (String candidate in hashCandidates)
            {
                String hash = Util.SHA256Hash(candidate);
                if (CandidateSignatures.Any(cs => cs.sig.Equals(hash)))
                {
                    CookieToken activeCookie = cookie.tokens.Last();

                    String hashedTouchedSig = Util.SHA256Hash($"{hashedPrivateKey}{activeCookie.touched}");
                    int minsSince = (int)(DateTime.UtcNow - Util.UnixTimeStampToDateTime(activeCookie.touched)).TotalMinutes;

                    if (minsSince < room.timeout && hashedTouchedSig.Equals(activeCookie.touchedSig))
                    {
                        // Success, everything matches and is not timed out
                        return new ValidateSignatureResponse() { success = true, expired = false };
                    }

                    //Expired or old sig doesn't match
                    return new ValidateSignatureResponse() { success = false, expired = true };
                }
            }

            // Doesn't match
            return new ValidateSignatureResponse() { success = false, expired = false };
        }
        // If we have url params, use this
        public virtual ValidateSignatureResponse ValidateSignature(String Signature, DateTime requested, String token, RoomConfig room)
        {
            String hashedPrivateKey = Util.SHA256Hash(this.PrivateApiKey);

            // Don't forget the universal time conversion or you'll probably spend 1 billion years debugging this
            String requestDateFormatted = requested.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            String roomActiveDateFormatted = room.queueActivatesOn.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            String hashCandidate = $"{hashedPrivateKey}{room.Slug}{roomActiveDateFormatted}{token}{requestDateFormatted}";

            String requiredHash = Util.SHA256Hash(hashCandidate);

            if (requiredHash.Equals(Signature))
            {
                int minsSince = (int)(DateTime.UtcNow - requested.ToUniversalTime()).TotalMinutes;
                if (minsSince < room.timeout)
                {
                    // Matched and in time!
                    return new ValidateSignatureResponse() { success = true, expired = false };
                }

                //matched but expired
                return new ValidateSignatureResponse() { success = false, expired = true };
            }

            // Doesn't match
            return new ValidateSignatureResponse() { success = false, expired = false };
        }

        /// <summary>
        /// Convert a JSON Object into a structured <see cref="CookieData"/> Object
        /// </summary>
        public virtual CookieData getCookieData(String JSONCookieData)
        {
            if (JSONCookieData == null || JSONCookieData == "")
            {
                return null;
            }
            return Newtonsoft.Json.JsonConvert.DeserializeObject<CookieData>(JSONCookieData);
        }

        /// <summary>
        /// Test the provided host and url path match any of the rooms in the provided Crowdhandler <see cref="Config">Room Configuration</see> 
        /// </summary>
        /// <param name="host">Hostname</param>
        /// <param name="path">URL Path and query string</param>
        /// <param name="config">Room Configuration</param>
        /// <returns>The first matched Room, or null if one could not be found</returns>
        public virtual RoomConfig MatchRoom(string host, string path, List<RoomConfig> rooms)
        {
            foreach (RoomConfig room in rooms)
            {
                if (room.domain != $"https://{host}")
                {
                    continue;
                }

                bool matched = false;

                switch (room.patternType)
                {
                    case "regex":
                        Regex reg = new Regex(room.urlPattern);
                        matched = reg.IsMatch(path);
                        break;
                    case "contains":
                        matched = path.Contains(room.urlPattern);
                        break;
                    case "all":
                        matched = true;
                        break;
                    default:
                        break;
                }

                if (matched)
                {
                    return room;
                }
            }
            return null;
        }

        /// <summary>
        /// Check if the provided host and URL path match the checkout URL pattern of any room in the provided Crowdhandler Room Configuration.
        /// </summary>
        /// <param name="host">Hostname</param>
        /// <param name="path">URL Path and query string</param>
        /// <returns>True if a checkout URL pattern is matched, otherwise false</returns>
        public virtual string IsCheckoutBuster(string host, string path, List<RoomConfig> rooms)
        {
            foreach (RoomConfig room in rooms)
            {
                if (room.domain != $"https://{host}")
                {
                    continue;
                }

                // Check if the path matches the checkout URL pattern
                try
                {
                    Regex checkoutRegex = new Regex(room.checkout);
                    if (checkoutRegex.IsMatch(path))
                    {
                        return "busted"; // Checkout pattern matched
                    }
                }
                catch (ArgumentException ex)
                {
                    // Handle or log invalid regex pattern here
                }
            }
            return "not-busted"; // No matching checkout pattern found
        }


        /// <summary>
        /// Test the provided host and url path match any of the rooms found via the Crowdhandler API. <see cref="Config">Room Configuration</see> 
        /// </summary>
        /// <param name="host">Hostname</param>
        /// <param name="path">URL Path and query string</param>
        /// <returns>The first matched Room, or null if one could not be found</returns>
        ///

        public virtual RoomConfig IsRoomMatch(string host, string path)
        {
            return MatchRoom(host, path, this.getRoomConfig());
        }


        public virtual string CheckoutBuster(string host, string path, string targetUrl, String userAgent, String language, String ipAddress, string token)
        {
            string result = IsCheckoutBuster(host, path, this.getRoomConfig());
            if (result == “busted”)
            {
                try
                {
                    // Fire an HTTP request
                    var apiClient = GetApiClient();
                    apiClient.getToken(targetUrl, userAgent, language, ipAddress, token);
                }
                catch (Exception ex)
                {
                    // Handle the exception (e.g., log the error, return a specific result, etc.)
                    System.Diagnostics.Debug.WriteLine(“Error communicating checkout bust to CrowdHandler API: ” + ex.ToString());
                }
            }
            return result;
        }

        /// <summary>
        /// Look up an application configuration value from Web.config or App.config.
        /// </summary>
        /// <param name="settingName">The config value name to look up</param>
        /// <returns>Config value</returns>
        /// 
        protected virtual String getConfigValue(String settingName, Boolean required)
        {
            String value = ConfigurationManager.AppSettings[settingName];

            if (value == null && required == true)
            {
                throw new MissingFieldException("Value not found in ConfigurationManager.AppSettings: " + settingName);
            }

            return value;
        }

        private ApiClient _crowdhandlerApi;
        private ApiClient GetApiClient()
        {
            if (this._crowdhandlerApi == null)
            {
                this._crowdhandlerApi = new ApiClient(this.ApiEndpoint, this.PublicApiKey, this.APIRequestTimeout, this.RoomCacheTTL);
            }
            return this._crowdhandlerApi;
        }

        public virtual List<RoomConfig> getRoomConfig()
        {
            return GetApiClient().getRoomConfig();
        }
    }
}
