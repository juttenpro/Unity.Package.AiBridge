using UnityEngine;

namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Example API key provider that retrieves the API key from an environment variable.
    /// This is useful for development, CI/CD pipelines, and production deployments.
    ///
    /// USAGE FOR 3RD PARTY DEVELOPERS:
    /// 1. Add this component to a GameObject (e.g., WebSocketClient GameObject)
    /// 2. Configure the environment variable name in the Inspector
    /// 3. Assign this component to WebSocketClient's apiKeyProviderComponent field
    /// 4. Set the environment variable before running Unity (e.g., ORCHESTRATOR_API_KEY=your_key_here)
    ///
    /// SECURITY NOTE:
    /// - Environment variables are more secure than hardcoded keys
    /// - Ideal for server deployments and CI/CD systems
    /// - Can be set per-deployment without changing code
    /// </summary>
    public class EnvironmentApiKeyProvider : MonoBehaviour, IApiKeyProvider
    {
        [Header("Configuration")]
        [SerializeField]
        [Tooltip("Name of the environment variable containing the API key")]
        private string environmentVariableName = "ORCHESTRATOR_API_KEY";

        [Header("Debug")]
        [SerializeField] private bool enableLogging = false;

        /// <summary>
        /// Gets the orchestrator API key from the configured environment variable.
        /// </summary>
        /// <returns>The API key string</returns>
        /// <exception cref="System.InvalidOperationException">When environment variable is not set or empty</exception>
        public string GetOrchestratorApiKey()
        {
            if (string.IsNullOrEmpty(environmentVariableName))
            {
                throw new System.InvalidOperationException(
                    "[EnvironmentApiKeyProvider] environmentVariableName is not configured! " +
                    "Set the variable name in the Inspector.");
            }

            var apiKey = System.Environment.GetEnvironmentVariable(environmentVariableName);

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new System.InvalidOperationException(
                    $"[EnvironmentApiKeyProvider] Environment variable '{environmentVariableName}' is not set or empty! " +
                    $"Set it before running Unity (e.g., export {environmentVariableName}=your_key_here)");
            }

            if (enableLogging)
                Debug.Log($"[EnvironmentApiKeyProvider] Retrieved API key from environment variable '{environmentVariableName}' (length: {apiKey.Length})");

            return apiKey;
        }

        // Editor validation helper
        private void OnValidate()
        {
            if (string.IsNullOrEmpty(environmentVariableName))
            {
                Debug.LogWarning("[EnvironmentApiKeyProvider] environmentVariableName is empty! Configure it in the Inspector.");
            }
        }
    }
}
