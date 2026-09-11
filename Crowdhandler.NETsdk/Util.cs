using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Crowdhandler.NETsdk
{
    /// <summary>
    /// Internal helpers shared by the GateKeeper and ApiClient. Everything here is pure and side-effect free.
    /// </summary>
    internal static class Util
    {
        /// <summary>
        /// The timestamp format CrowdHandler uses for <c>ch-requested</c>, <c>queueActivatesOn</c> and cookie <c>gen</c> values.
        /// Signatures are computed over this exact string form, so it must never change.
        /// </summary>
        public const string CrowdhandlerDateFormat = "yyyy-MM-ddTHH:mm:ssZ";

        /// <summary>
        /// Values of <c>touched</c> at or above this are treated as milliseconds since the Unix epoch;
        /// below it, seconds. 1e11 seconds is the year 5138, 1e11 ms is 1973 — no real timestamp is ambiguous.
        /// </summary>
        private const ulong TouchedMillisecondThreshold = 100_000_000_000UL;

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Regex TokenPattern = new Regex(@"^tok[A-Za-z0-9_\-]{1,128}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="value"/>.
        /// Uses a FIPS-compliant implementation so it works on hosts with the Windows FIPS policy enforced.
        /// </summary>
        public static String SHA256Hash(String value)
        {
            var sb = new StringBuilder(64);
#if NETFRAMEWORK
            using (SHA256 hash = new SHA256CryptoServiceProvider()) // FIPS-compliant on hosts with the Windows FIPS policy enforced
#else
            using (SHA256 hash = SHA256.Create())
#endif
            {
                byte[] result = hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                foreach (byte b in result)
                {
                    sb.Append(b.ToString("x2"));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Constant-time string comparison for signatures, so timing does not leak how many leading characters matched.
        /// </summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            if (a.Length != b.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        /// <summary>
        /// Format a DateTime in CrowdHandler's canonical UTC form. Unspecified kinds are treated as UTC, never as local time.
        /// </summary>
        public static string FormatUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
            {
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            return dt.ToUniversalTime().ToString(CrowdhandlerDateFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parse an ISO-8601 timestamp into a UTC DateTime. Culture-invariant, never throws.
        /// Returns false for null, empty, or unparseable input.
        /// </summary>
        public static bool TryParseUtc(string value, out DateTime result)
        {
            result = default(DateTime);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
        }

        /// <summary>
        /// Seconds since the Unix epoch.
        /// </summary>
        public static UInt64 DateTimeToUnixTimeStamp(DateTime dt)
        {
            return (UInt64)dt.ToUniversalTime().Subtract(UnixEpoch).TotalSeconds;
        }

        /// <summary>
        /// Milliseconds since the Unix epoch. This is the unit the other CrowdHandler integrations write into the cookie's <c>touched</c> field.
        /// </summary>
        public static UInt64 DateTimeToUnixTimeStampMs(DateTime dt)
        {
            return (UInt64)dt.ToUniversalTime().Subtract(UnixEpoch).TotalMilliseconds;
        }

        /// <summary>
        /// Convert a cookie <c>touched</c> value to a UTC DateTime, accepting both the millisecond form written by this SDK (1.1+)
        /// and the JS/edge integrations, and the second form written by earlier versions of this SDK.
        /// Returns false if the value is out of any sane range.
        /// </summary>
        public static bool TryTouchedToDateTime(ulong touched, out DateTime result)
        {
            result = default(DateTime);
            try
            {
                double ms = touched >= TouchedMillisecondThreshold ? (double)touched : (double)touched * 1000d;
                if (ms > 4102444800000d) // 2100-01-01
                {
                    return false;
                }
                result = UnixEpoch.AddMilliseconds(ms);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a string looks like a CrowdHandler session token. Anything else from a URL or cookie is ignored rather than trusted.
        /// </summary>
        public static bool IsValidToken(string token)
        {
            return !string.IsNullOrEmpty(token) && TokenPattern.IsMatch(token);
        }

        /// <summary>
        /// Build a query string from key/value pairs, percent-encoding every value.
        /// </summary>
        public static string BuildQuery(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            return string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value ?? "")));
        }
    }
}
