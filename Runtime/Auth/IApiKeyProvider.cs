namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Interface for providing API key authentication to the conversation backend.
    /// Implementations should retrieve the API key from appropriate configuration sources.
    /// </summary>
    public interface IApiKeyProvider
    {
        /// <summary>
        /// Gets the orchestrator API key for backend authentication.
        /// </summary>
        /// <returns>The API key string used for authenticating with the backend services.</returns>
        string GetOrchestratorApiKey();
    }
}