namespace Tsc.AIBridge.Configuration
{
    /// <summary>
    /// Central configuration for API endpoints
    /// </summary>
    public static class ApiEndpoints
    {
        public const string AuthToken = "/api/auth/token";
        
        // HTTP Headers
        public const string ContentTypeHeader = "Content-Type";
        public const string ApiKeyHeader = "X-API-Key";
        public const string ContentTypeJson = "application/json";
    }
}
