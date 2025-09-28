namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Simple implementation of IApiKeyProvider that returns a hardcoded API key.
    /// Useful for testing or when the API key is provided directly in code.
    /// WARNING: Never hardcode production API keys in source code!
    /// </summary>
    public class SimpleApiKeyProvider : IApiKeyProvider
    {
        private readonly string _apiKey;

        /// <summary>
        /// Initializes a new instance of the SimpleApiKeyProvider with the specified API key.
        /// </summary>
        /// <param name="apiKey">The API key to use for authentication</param>
        public SimpleApiKeyProvider(string apiKey)
        {
            _apiKey = apiKey ?? string.Empty;
        }

        /// <summary>
        /// Gets the orchestrator API key for backend authentication.
        /// </summary>
        /// <returns>The API key string</returns>
        public string GetOrchestratorApiKey() => _apiKey;
    }
}