// Models/ApiLogEntry.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LLMProxy.Models
{
    public class ApiLogEntry
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id
        {
            get; set;
        }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [MaxLength(2048)]
        public string RequestPath { get; set; } = string.Empty; // e.g., /v1/chat/completions

        [MaxLength(10)]
        public string RequestMethod { get; set; } = string.Empty; // POST, GET

        // Client Request
        public string? ClientRequestBody
        {
            get; set;
        } // Can be large

        // Upstream (Backend) Request
        [MaxLength(256)]
        public string? UpstreamBackendName
        {
            get; set;
        } // Name of the backend used

        [MaxLength(2048)]
        public string? UpstreamUrl
        {
            get; set;
        }

        public string? UpstreamRequestBody
        {
            get; set;
        } // Potentially modified body

        // Upstream (Backend) Response
        public int? UpstreamStatusCode
        {
            get; set;
        } // Null if network error before response

        public string? UpstreamResponseBody
        {
            get; set;
        } // Can be large, especially errors or full responses

        // Final Proxy Response to Client
        public int ProxyResponseStatusCode
        {
            get; set;
        }

        public bool WasSuccess
        {
            get; set;
        } // Overall success of the proxy operation for the client

        [MaxLength(256)]
        public string? ErrorMessage
        {
            get; set;
        } // If overall operation failed, or specific backend error

        [MaxLength(128)]
        public string? RequestedModel
        {
            get; set;
        } // General model requested by client
    }
}