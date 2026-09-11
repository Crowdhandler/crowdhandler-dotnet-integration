using System;
using System.Net;

namespace Crowdhandler.NETsdk
{
    /// <summary>
    /// Raised when a call to the CrowdHandler API fails. <see cref="StatusCode"/> is populated when the API answered with an
    /// HTTP error, and null for transport failures (timeouts, DNS, connection reset) and malformed responses.
    /// </summary>
    public class CrowdhandlerApiException : Exception
    {
        /// <summary>HTTP status code returned by the API, or null if no response was received.</summary>
        public int? StatusCode { get; }

        /// <summary>
        /// True for a definitive rejection by the API (4xx other than 429). These are not retried and are never
        /// treated as "trust on fail": the request was understood and refused, typically because of an invalid API key,
        /// an unknown token, or bad parameters.
        /// </summary>
        public bool IsClientError => StatusCode.HasValue && StatusCode.Value >= 400 && StatusCode.Value < 500 && StatusCode.Value != 429;

        /// <summary>
        /// True when the failure is transient or on the infrastructure side (5xx, 429, timeout, network error, throttled).
        /// These are the failures that <c>FailTrust</c> applies to.
        /// </summary>
        public bool IsTransient => !IsClientError;

        public CrowdhandlerApiException(string message, int? statusCode = null, Exception innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        public CrowdhandlerApiException(string message, HttpStatusCode statusCode)
            : this(message, (int)statusCode)
        {
        }
    }
}
