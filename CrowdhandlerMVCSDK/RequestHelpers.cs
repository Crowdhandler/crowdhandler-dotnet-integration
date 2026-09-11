using System;
using System.Net;

namespace Crowdhandler.MVCSDK
{
    /// <summary>Request-parsing helpers shared by the MVC 5 filter, the ASP.NET Core filter and the middleware.</summary>
    internal static class RequestHelpers
    {
        /// <summary>
        /// Pick the visitor's IP: the first non-empty entry of the forwarded header if present, else the socket address.
        /// Ports and IPv4-mapped IPv6 prefixes are stripped so the API receives a plain address.
        /// Returns "" when nothing usable is available (the API then detects the address itself).
        /// </summary>
        public static string ExtractClientIp(string forwardedHeaderValue, string remoteAddress)
        {
            if (!string.IsNullOrWhiteSpace(forwardedHeaderValue))
            {
                foreach (var part in forwardedHeaderValue.Split(','))
                {
                    var candidate = NormaliseIp(part);
                    if (candidate.Length > 0)
                    {
                        return candidate;
                    }
                }
            }
            return NormaliseIp(remoteAddress);
        }

        /// <summary>Trim, drop a port suffix and brackets, and unwrap IPv4-mapped IPv6 addresses. Returns "" for anything that is not an IP.</summary>
        public static string NormaliseIp(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            string v = value.Trim().Trim('"');
            if (v.Length == 0 || v.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            // [::1]:8080 or 1.2.3.4:8080
            if (v.StartsWith("["))
            {
                int close = v.IndexOf(']');
                if (close > 0)
                {
                    v = v.Substring(1, close - 1);
                }
            }
            else
            {
                int colon = v.IndexOf(':');
                if (colon > 0 && v.IndexOf(':', colon + 1) < 0)
                {
                    v = v.Substring(0, colon); // exactly one colon: IPv4 with port
                }
            }

            if (!IPAddress.TryParse(v, out IPAddress parsed))
            {
                return "";
            }
            if (parsed.IsIPv4MappedToIPv6)
            {
                parsed = parsed.MapToIPv4();
            }
            if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && parsed.ScopeId != 0)
            {
                parsed.ScopeId = 0; // fe80::1%eth0 -> fe80::1
            }
            return parsed.ToString();
        }

        /// <summary>
        /// Cookie values may arrive raw (<c>{"integration":...</c>) or percent-encoded (<c>%7B...</c>) depending on who wrote them.
        /// Decode once if needed so the gatekeeper always sees JSON or a bare token.
        /// </summary>
        public static string NormaliseCookieValue(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "";
            }
            string v = raw.Trim();
            if (v.StartsWith("{") || v.StartsWith("tok"))
            {
                return v;
            }
            try
            {
                return Uri.UnescapeDataString(v);
            }
            catch (Exception)
            {
                return v;
            }
        }
    }
}
