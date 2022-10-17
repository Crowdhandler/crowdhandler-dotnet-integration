using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Crowdhandler.NETsdk
{
    internal class Util
    {
        public static Dictionary<string, string> parseUrlQueries(String queryString)
        {
            if (queryString == "")
            {
                return new Dictionary<string, string>();
            }

            string query = queryString.StartsWith("?") ? queryString.Substring(1) : queryString;

            Dictionary<string, string> dict = new Dictionary<string, string>();

            foreach (String q in query.Split('&'))
            {
                String[] parts = q.Split('=');
                dict[parts[0]] = parts[1];
            }

            return dict;
        }

        public static String SHA256Hash(String value)
        {
            StringBuilder Sb = new StringBuilder();

            using (SHA256 hash = SHA256Managed.Create())
            {
                Encoding enc = Encoding.UTF8;
                Byte[] result = hash.ComputeHash(enc.GetBytes(value));

                foreach (Byte b in result)
                    Sb.Append(b.ToString("x2"));
            }

            return Sb.ToString();
        }
        public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dateTime = dateTime.AddSeconds(unixTimeStamp);
            return dateTime.ToUniversalTime();
        }

        public static UInt64 DateTimeToUnixTimeStamp(DateTime dt)
        {
            return (UInt64)dt.ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
        }
    }
}
