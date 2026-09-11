using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Crowdhandler.NETsdk.JSONTypes
{
    /// <summary>
    /// Reads and writes timestamps in CrowdHandler's canonical form (<c>yyyy-MM-ddTHH:mm:ssZ</c>, UTC).
    /// Signatures are computed over this exact string, so values must round-trip byte-for-byte and never be
    /// interpreted in the server's local time zone.
    /// </summary>
    public class CrowdhandlerDateConverter : IsoDateTimeConverter
    {
        public CrowdhandlerDateConverter()
        {
            DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
            DateTimeStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            Culture = CultureInfo.InvariantCulture;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is DateTime dt && dt.Kind == DateTimeKind.Unspecified)
            {
                value = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            base.WriteJson(writer, value, serializer);
        }

        /// <summary>Reads any ISO-8601 form (with or without fractional seconds or an offset) as UTC; the base class would insist on the exact write format.</summary>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            bool nullable = Nullable.GetUnderlyingType(objectType) != null;
            if (reader.TokenType == JsonToken.Null)
            {
                if (nullable) return null;
                throw new JsonSerializationException("Cannot convert null value to DateTime.");
            }
            if (reader.TokenType == JsonToken.Date && reader.Value is DateTime already)
            {
                return already.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(already, DateTimeKind.Utc) : already.ToUniversalTime();
            }
            if (reader.TokenType == JsonToken.String)
            {
                string text = (string)reader.Value;
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                {
                    return parsed;
                }
                throw new JsonSerializationException("Unexpected date format: " + text);
            }
            throw new JsonSerializationException("Unexpected token parsing date: " + reader.TokenType);
        }
    }

    /// <summary>One entry of the <c>/v1/rooms</c> feed: a waiting room and the domain it protects.</summary>
    public class RoomConfig
    {
        [JsonProperty("id")]
        public string id { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }

        [JsonProperty("urlPattern")]
        public string urlPattern { get; set; }

        /// <summary>One of: regex, contains, regex-not, contains-not, all.</summary>
        [JsonProperty("patternType")]
        public string patternType { get; set; }

        [JsonProperty("queueActivatesOn")]
        [JsonConverter(typeof(CrowdhandlerDateConverter))]
        public DateTime queueActivatesOn { get; set; }

        /// <summary>Scheme plus host, e.g. <c>https://www.example.com</c>. May contain a <c>*</c> wildcard, e.g. <c>https://*.example.com</c>.</summary>
        [JsonProperty("domain")]
        public string domain { get; set; }

        [JsonProperty("safetyMode")]
        public bool safetyMode { get; set; }

        /// <summary>Session timeout in minutes. A signature older than this must be re-validated with the API.</summary>
        [JsonProperty("timeout")]
        public int timeout { get; set; }

        /// <summary>Regex (matched against the path and query) identifying the checkout-complete page, used for checkout busting.</summary>
        [JsonProperty("checkout")]
        public string checkout { get; set; }

        [JsonProperty("stock")]
        public int? stock { get; set; }

        [JsonProperty("ttl")]
        public int ttl { get; set; }
    }

    public class CookieSignature
    {
        [JsonProperty("gen")]
        [JsonConverter(typeof(CrowdhandlerDateConverter))]
        public DateTime gen { get; set; }

        [JsonProperty("sig")]
        public String sig { get; set; }
    }

    public class CookieToken
    {
        [JsonProperty("token")]
        public String token { get; set; }

        /// <summary>
        /// When this token was last seen, as seconds since the Unix epoch. Milliseconds (as the JS SDK writes) are also read.
        /// </summary>
        [JsonProperty("touched")]
        public UInt64 touched { get; set; }

        [JsonProperty("touchedSig")]
        public String touchedSig { get; set; }

        [JsonProperty("signatures")]
        public List<CookieSignature> signatures { get; set; }
    }

    /// <summary>The JSON stored in the <c>crowdhandler</c> cookie. Shared shape with the JS SDK and edge integrations.</summary>
    public class CookieData
    {
        [JsonProperty("integration")]
        public String integration { get; set; }

        [JsonProperty("tokens")]
        public List<CookieToken> tokens { get; set; }

        [JsonProperty("deployment", NullValueHandling = NullValueHandling.Ignore)]
        public String deployment { get; set; }
    }

    /// <summary>The <c>result</c> object of <c>/v1/requests</c>. Fields are optional on the wire; absent ones are null/default.</summary>
    public class TokenResponse
    {
        /// <summary>0 no room matched, 1 live, 3 blocked, 4 countdown (not yet on sale), 5 room full, 6 throttled.</summary>
        [JsonProperty("status")]
        public int status { get; set; }

        [JsonProperty("token")]
        public string token { get; set; }

        [JsonProperty("title")]
        public string title { get; set; }

        [JsonProperty("position")]
        public int? position { get; set; }

        [JsonProperty("promoted")]
        public bool promoted { get; set; }

        [JsonProperty("urlRedirect")]
        public string urlRedirect { get; set; }

        [JsonProperty("message")]
        public string message { get; set; }

        [JsonProperty("slug")]
        public string slug { get; set; }

        [JsonProperty("responseID")]
        public string responseID { get; set; }

        [JsonProperty("captchaRequired")]
        public bool? captchaRequired { get; set; }

        [JsonProperty("sessionsTimeout")]
        public int? sessionsTimeout { get; set; }

        [JsonProperty("deployment")]
        public string deployment { get; set; }

        [JsonProperty("domain")]
        public string domain { get; set; }

        [JsonProperty("requested")]
        [JsonConverter(typeof(CrowdhandlerDateConverter))]
        public DateTime? requested { get; set; }

        [JsonProperty("hash")]
        public string hash { get; set; }

        [JsonProperty("ttl")]
        public int? ttl { get; set; }
    }
}
