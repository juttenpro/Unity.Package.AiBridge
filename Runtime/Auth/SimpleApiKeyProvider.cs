namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Simple implementation of IApiKeyProvider that returns a hardcoded API key.
    ///
    /// ⚠️ WARNING: ONLY FOR UNIT TESTING AND DEVELOPMENT!
    /// DO NOT use this in production code - use EnvironmentApiKeyProvider or custom provider instead.
    /// Never hardcode production API keys in source code or version control!
    /// </summary>
    public class SimpleApiKeyProvider : IApiKeyProvider
    {
        private readonly string _apiKey;

        /// <summary>
        /// Initializes a new instance of the SimpleApiKeyProvider with the specified API key.
        /// WARNING: Only use this for unit tests or local development!
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