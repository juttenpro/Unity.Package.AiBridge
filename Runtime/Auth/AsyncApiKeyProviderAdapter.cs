using System;
using System.Collections;
using UnityEngine;

namespace Tsc.AIBridge.Auth
{
    /// <summary>
    /// Adapter that wraps IAsyncApiKeyProvider to expose it as synchronous IApiKeyProvider.
    /// Caches the API key after first retrieval to avoid repeated async calls.
    /// IMPORTANT: First call will block briefly while waiting for coroutine to complete.
    /// </summary>
    public class AsyncApiKeyProviderAdapter : IApiKeyProvider
    {
        private readonly MonoBehaviour _coroutineHost;
        private readonly IAsyncApiKeyProvider _asyncProvider;
        private string _cachedApiKey;
        private bool _isCached;
        private bool _isFetching;
        private readonly object _lock = new object();

        /// <summary>
        /// Creates an adapter for an async API key provider.
        /// </summary>
        /// <param name="coroutineHost">MonoBehaviour to run coroutines on (typically WebSocketClient)</param>
        /// <param name="asyncProvider">The async provider to wrap</param>
        public AsyncApiKeyProviderAdapter(MonoBehaviour coroutineHost, IAsyncApiKeyProvider asyncProvider)
        {
            _coroutineHost = coroutineHost ?? throw new ArgumentNullException(nameof(coroutineHost));
            _asyncProvider = asyncProvider ?? throw new ArgumentNullException(nameof(asyncProvider));
        }

        /// <summary>
        /// Gets the orchestrator API key synchronously by waiting for async operation to complete.
        /// Uses cached value after first retrieval for performance.
        /// </summary>
        /// <returns>The API key string</returns>
        /// <exception cref="InvalidOperationException">When API key retrieval fails</exception>
        public string GetOrchestratorApiKey()
        {
            // Return cached value if available
            lock (_lock)
            {
                if (_isCached)
                    return _cachedApiKey;
            }

            // Prevent concurrent fetches
            lock (_lock)
            {
                if (_isFetching)
                {
                    // Wait for existing fetch to complete
                    while (_isFetching)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                    return _cachedApiKey;
                }

                _isFetching = true;
            }

            // Fetch API key using coroutine
            string resultKey = null;
            bool success = false;
            bool completed = false;

            _coroutineHost.StartCoroutine(FetchApiKey((isSuccess, key) =>
            {
                success = isSuccess;
                resultKey = key;
                completed = true;
            }));

            // Wait for coroutine to complete (with timeout)
            var timeout = DateTime.Now.AddSeconds(10);
            while (!completed && DateTime.Now < timeout)
            {
                System.Threading.Thread.Sleep(50);
            }

            lock (_lock)
            {
                _isFetching = false;
            }

            if (!completed)
            {
                throw new InvalidOperationException("API key fetch timed out after 10 seconds");
            }

            if (!success)
            {
                throw new InvalidOperationException($"Failed to retrieve API key: {resultKey}");
            }

            // Cache the result
            lock (_lock)
            {
                _cachedApiKey = resultKey;
                _isCached = true;
            }

            return _cachedApiKey;
        }

        /// <summary>
        /// Internal coroutine to fetch API key from async provider
        /// </summary>
        private IEnumerator FetchApiKey(Action<bool, string> callback)
        {
            var coroutine = _asyncProvider.GetOrchestratorApiKeyAsync(callback);
            yield return _coroutineHost.StartCoroutine(coroutine);
        }
    }
}
