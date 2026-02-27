using UnityEngine;

namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// API key provider that takes the key directly from the Inspector.
    /// Convenient for quick testing without environment variable setup.
    ///
    /// WARNING: Only for local development and testing!
    /// Never commit scenes with real API keys to version control.
    /// For production, use EnvironmentApiKeyProvider or a custom provider.
    /// </summary>
    public class InspectorApiKeyProvider : MonoBehaviour, IApiKeyProvider
    {
        [SerializeField]
        [Tooltip("Paste your API key here. WARNING: Do not commit to version control!")]
        private string apiKey;

        /// <inheritdoc/>
        public string GetOrchestratorApiKey()
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new System.InvalidOperationException(
                    "[InspectorApiKeyProvider] API key is empty! Paste your key in the Inspector.");
            }

            return apiKey;
        }

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(apiKey))
                Debug.LogWarning("[InspectorApiKeyProvider] API key is empty! Paste your key in the Inspector.");
        }
    }
}
