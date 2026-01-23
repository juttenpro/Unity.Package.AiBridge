namespace Tsc.AIBridge.Configuration
{
    /// <summary>
    /// Central configuration for API endpoints
    /// </summary>
    public static class ApiEndpoints
    {
        public const string AuthToken = "/api/auth/token";

        /// <summary>
        /// Context cache ensure endpoint for Gemini cost optimization.
        /// Creates cache if not exists, refreshes TTL if exists.
        /// </summary>
        public const string CacheEnsure = "/api/cache/ensure";

        // HTTP Headers
        public const string ContentTypeHeader = "Content-Type";
        public const string ApiKeyHeader = "X-API-Key";
        public const string ContentTypeJson = "application/json";
    }
}
