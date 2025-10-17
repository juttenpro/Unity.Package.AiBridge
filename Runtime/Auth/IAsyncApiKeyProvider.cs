using System;
using System.Collections;

namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Interface for providing API key authentication asynchronously via coroutines.
    /// Use this interface when API key retrieval requires async operations (network calls, decryption, etc).
    /// For simple synchronous providers (environment variables, hardcoded values), use IApiKeyProvider instead.
    /// </summary>
    public interface IAsyncApiKeyProvider
    {
        /// <summary>
        /// Gets the orchestrator API key for backend authentication asynchronously.
        /// </summary>
        /// <param name="callback">Callback invoked with result status and API key.
        /// First parameter: Success status (true = success, false = failure).
        /// Second parameter: API key string on success, or error message on failure.</param>
        /// <returns>Coroutine for Unity to execute</returns>
        IEnumerator GetOrchestratorApiKeyAsync(Action<bool, string> callback);
    }
}
