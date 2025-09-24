using System;
using UnityEngine;

namespace SimulationCrew.AIBridge.Auth
{
    /// <summary>
    /// Provides API key authentication by reading from environment variables.
    /// This is a generic implementation that doesn't depend on project-specific code.
    /// Projects can implement their own IApiKeyProvider for different authentication methods.
    /// </summary>
    public class EnvironmentApiKeyProvider : IApiKeyProvider
    {
        private readonly string _apiKey;
        private readonly string _environmentVariableName;

        /// <summary>
        /// Initializes a new instance of the EnvironmentApiKeyProvider.
        /// </summary>
        /// <param name="environmentVariableName">Name of the environment variable containing the API key. Defaults to "ORCHESTRATOR_API_KEY"</param>
        public EnvironmentApiKeyProvider(string environmentVariableName = "ORCHESTRATOR_API_KEY")
        {
            _environmentVariableName = environmentVariableName;

            try
            {
                _apiKey = Environment.GetEnvironmentVariable(_environmentVariableName);

                if (string.IsNullOrEmpty(_apiKey))
                {
                    Debug.LogWarning($"[EnvironmentApiKeyProvider] Environment variable '{_environmentVariableName}' is not set or empty.");
                }
                else
                {
                    Debug.Log($"[EnvironmentApiKeyProvider] API key loaded from environment variable '{_environmentVariableName}'");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[EnvironmentApiKeyProvider] Error retrieving API Key from environment: {ex.Message}");
                _apiKey = string.Empty;
            }
        }

        /// <summary>
        /// Gets the orchestrator API key for backend authentication.
        /// </summary>
        /// <returns>The API key string, or empty string if not configured.</returns>
        public string GetOrchestratorApiKey() => _apiKey;
    }
}