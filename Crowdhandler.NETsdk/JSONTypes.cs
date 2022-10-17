using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Crowdhandler.NETsdk.JSONTypes
{
    public class RoomConfig
    {
        [JsonProperty("slug")]
        public string Slug { get; set; }
        [JsonProperty("urlPattern")]
        public string urlPattern { get; set; }
        [JsonProperty("PatternType")]
        public string patternType { get; set; }
        [JsonProperty("queueActivatesOn")]
        public DateTime queueActivatesOn { get; set; }
        [JsonProperty("domain")]
        public string domain { get; set; }
        [JsonProperty("safetyMode")]
        public bool safetyMode { get; set; }
        [JsonProperty("timeout")]
        public int timeout { get; set; }
        [JsonProperty("ttl")]
        public int ttl { get; set; }
    }

    public class CookieSignature
    {
        [JsonProperty("gen")]
        public DateTime gen { get; set; }
        [JsonProperty("sig")]
        public String sig { get; set; }
    }
    public class CookieToken
    {
        [JsonProperty("token")]
        public String token { get; set; }
        [JsonProperty("touched")]
        public UInt64 touched { get; set; }
        [JsonProperty("touchedSig")]
        public String touchedSig { get; set; }
        [JsonProperty("signatures")]
        public List<CookieSignature> signatures { get; set; }
    }
    public class CookieData
    {
        [JsonProperty("integration")]
        public String integration { get; set; }
        [JsonProperty("tokens")]
        public List<CookieToken> tokens { get; set; }
    }
    public class TokenResponse
    {
        [JsonProperty("status")]
        public int status { get; set; }
        [JsonProperty("token")]
        public string token { get; set; }
        //[JsonProperty("title")]
        //public string title { get; set; }
        //[JsonProperty("position")]
        //public int position { get; set; }
        [JsonProperty("promoted")]
        public bool promoted { get; set; }
        [JsonProperty("urlRedirect")]
        public string urlRedirect { get; set; }
        //[JsonProperty("onsale")]
        //public DateTime onsale { get; set; }
        //[JsonProperty("message")]
        //public string message { get; set; }
        [JsonProperty("slug")]
        public string slug { get; set; }
        //[JsonProperty("priority")]
        //public int priority { get; set; }
        //[JsonProperty("priorityAvailable")]
        //public string priorityAvailable { get; set; }
        //[JsonProperty("logo")]
        //public string logo { get; set; }
        //[JsonProperty("stock")]
        //public string stock { get; set; }
        [JsonProperty("responseID")]
        public string responseID { get; set; }
        //[JsonProperty("captchaRequired")]
        //public int captchaRequired { get; set; }
        //[JsonProperty("rate")]
        //public int rate { get; set; }
        //[JsonProperty("sessionsExpire")]
        //public int sessionsExpire { get; set; }
        //[JsonProperty("sessionsTimeout")]
        //public int sessionsTimeout { get; set; }
        [JsonProperty("requested")]
        public DateTime? requested { get; set; }
        [JsonProperty("hash")]
        public string hash { get; set; }
        //[JsonProperty("ttl")]
        //public int ttl { get; set; }
    }
}
