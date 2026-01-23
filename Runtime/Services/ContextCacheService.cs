using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Tsc.AIBridge.Configuration;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.Networking;

namespace Tsc.AIBridge.Services
{
    /// <summary>
    /// Service for managing Gemini context caches via the API orchestrator.
    /// Provides cost optimization through cached content reuse (75% discount on cached tokens).
    ///
    /// Usage:
    /// 1. Call EnsureCacheExists() at course/lesson load time
    /// 2. Store the returned cacheName
    /// 3. Pass cacheName to ConversationContext.contextCacheName in sessions
    /// </summary>
    public static class ContextCacheService
    {
        private const string LogTag = "[ContextCacheService]";

        /// <summary>
        /// Request model for cache ensure endpoint
        /// </summary>
        [Serializable]
        public class EnsureCacheRequest
        {
            /// <summary>
            /// Unique identifier for this cache configuration.
            /// Should reflect the content being cached (e.g., "course-sollicitatie-persona-mentor-v1").
            /// Change this key when cached content changes to invalidate the cache.
            /// </summary>
            [JsonProperty("cacheKey")]
            public string cacheKey;

            /// <summary>
            /// System prompt / instructions to cache.
            /// This is the large, static content that benefits from caching.
            /// Only used when creating a new cache; ignored when refreshing existing cache.
            /// </summary>
            [JsonProperty("systemPrompt")]
            public string systemPrompt;

            /// <summary>
            /// Gemini model identifier (e.g., "gemini-2.5-flash").
            /// Cache is model-specific in Gemini.
            /// </summary>
            [JsonProperty("model")]
            public string model;

            /// <summary>
            /// Time-to-live for the cache in minutes.
            /// Default: 60 minutes.
            /// </summary>
            [JsonProperty("ttlMinutes")]
            public int ttlMinutes = 60;
        }

        /// <summary>
        /// Response model from cache ensure endpoint
        /// </summary>
        [Serializable]
        public class EnsureCacheResponse
        {
            /// <summary>
            /// Full Gemini cached content resource name.
            /// Format: "projects/{project}/locations/{location}/cachedContents/{id}"
            /// Pass this to ConversationContext.contextCacheName.
            /// </summary>
            [JsonProperty("cacheName")]
            public string cacheName;

            /// <summary>
            /// Cache expiration timestamp (ISO 8601).
            /// Refresh cache before this time by calling EnsureCacheExists again.
            /// </summary>
            [JsonProperty("expiresAt")]
            public string expiresAt;

            /// <summary>
            /// True if a new cache was created, false if existing cache was refreshed.
            /// </summary>
            [JsonProperty("wasCreated")]
            public bool wasCreated;
        }

        /// <summary>
        /// Ensures a context cache exists, creating it if necessary or refreshing TTL if it exists.
        /// Call this at course/lesson load time, not during conversation.
        /// </summary>
        /// <param name="cacheKey">Unique key for the cache (e.g., "course-v1-persona-mentor")</param>
        /// <param name="systemPrompt">System prompt to cache (only used for new caches)</param>
        /// <param name="model">Gemini model (e.g., "gemini-2.5-flash")</param>
        /// <param name="ttlMinutes">Cache time-to-live in minutes (default 60)</param>
        /// <param name="onSuccess">Callback with cache name and response on success</param>
        /// <param name="onError">Callback with error message on failure</param>
        /// <returns>Coroutine for use with StartCoroutine</returns>
        public static IEnumerator EnsureCacheExists(
            string cacheKey,
            string systemPrompt,
            string model,
            int ttlMinutes,
            Action<EnsureCacheResponse> onSuccess,
            Action<string> onError)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                onError?.Invoke("CacheKey is required");
                yield break;
            }

            // Get base URL from WebSocketClient
            var baseUrl = WebSocketClient.Instance?.ApiBaseUrl;
            if (string.IsNullOrEmpty(baseUrl))
            {
                onError?.Invoke("API base URL not configured. Ensure WebSocketClient is initialized.");
                yield break;
            }

            var url = baseUrl.TrimEnd('/') + ApiEndpoints.CacheEnsure;

            var request = new EnsureCacheRequest
            {
                cacheKey = cacheKey,
                systemPrompt = systemPrompt,
                model = model,
                ttlMinutes = ttlMinutes
            };

            var jsonBody = JsonConvert.SerializeObject(request);
            var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

            Debug.Log($"{LogTag} Ensuring cache exists: key='{cacheKey}', model='{model}', ttl={ttlMinutes}min");

            using var webRequest = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(bodyBytes),
                downloadHandler = new DownloadHandlerBuffer()
            };

            webRequest.SetRequestHeader(ApiEndpoints.ContentTypeHeader, ApiEndpoints.ContentTypeJson);

            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                var errorMsg = $"Cache ensure failed: {webRequest.error}";
                if (!string.IsNullOrEmpty(webRequest.downloadHandler?.text))
                {
                    errorMsg += $" - {webRequest.downloadHandler.text}";
                }
                Debug.LogError($"{LogTag} {errorMsg}");
                onError?.Invoke(errorMsg);
                yield break;
            }

            try
            {
                var responseText = webRequest.downloadHandler.text;
                var response = JsonConvert.DeserializeObject<EnsureCacheResponse>(responseText);

                if (response == null || string.IsNullOrEmpty(response.cacheName))
                {
                    onError?.Invoke("Invalid response: missing cacheName");
                    yield break;
                }

                Debug.Log($"{LogTag} Cache ensured: name='{response.cacheName}', wasCreated={response.wasCreated}, expiresAt={response.expiresAt}");
                onSuccess?.Invoke(response);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to parse response: {ex.Message}";
                Debug.LogError($"{LogTag} {errorMsg}");
                onError?.Invoke(errorMsg);
            }
        }

        /// <summary>
        /// Convenience overload with default TTL of 60 minutes.
        /// </summary>
        public static IEnumerator EnsureCacheExists(
            string cacheKey,
            string systemPrompt,
            string model,
            Action<EnsureCacheResponse> onSuccess,
            Action<string> onError)
        {
            return EnsureCacheExists(cacheKey, systemPrompt, model, 60, onSuccess, onError);
        }
    }
}
