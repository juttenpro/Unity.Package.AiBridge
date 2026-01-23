using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Tsc.AIBridge.Services
{
    /// <summary>
    /// Manager for context cache lifecycle.
    /// Caches are identified by key and stored for reuse across sessions.
    ///
    /// USAGE:
    /// 1. At course/lesson load time:
    ///    yield return ContextCacheManager.Instance.EnsureCache("course-v1-persona-mentor", systemPrompt, "gemini-2.5-flash");
    ///
    /// 2. When starting a conversation:
    ///    var cacheName = ContextCacheManager.Instance.GetCacheName("course-v1-persona-mentor");
    ///    context.contextCacheName = cacheName; // Pass to ConversationContext
    ///
    /// CACHE KEYS:
    /// Use a consistent key format that changes when content changes:
    /// - "course-{courseId}-persona-{personaId}-v{version}"
    /// - "lesson-{lessonId}-v{hash}"
    /// </summary>
    public class ContextCacheManager : MonoBehaviour
    {
        private static ContextCacheManager _instance;
        public static ContextCacheManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ContextCacheManager");
                    _instance = go.AddComponent<ContextCacheManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        /// <summary>
        /// Stored cache information
        /// </summary>
        public class CacheInfo
        {
            public string CacheName { get; set; }
            public DateTime ExpiresAt { get; set; }
            public bool WasCreated { get; set; }
        }

        // In-memory cache storage (key -> cache info)
        private readonly Dictionary<string, CacheInfo> _caches = new();

        /// <summary>
        /// Default TTL in minutes for new caches
        /// </summary>
        public int DefaultTtlMinutes { get; set; } = 60;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        /// <summary>
        /// Ensures a cache exists for the given key.
        /// Creates new cache if not exists, refreshes TTL if exists.
        /// </summary>
        /// <param name="cacheKey">Unique key for the cache</param>
        /// <param name="systemPrompt">System prompt to cache</param>
        /// <param name="model">Gemini model (e.g., "gemini-2.5-flash")</param>
        /// <param name="ttlMinutes">Cache TTL in minutes (default: DefaultTtlMinutes)</param>
        /// <param name="onComplete">Optional callback when complete (success or failure)</param>
        /// <returns>Coroutine for use with StartCoroutine</returns>
        public IEnumerator EnsureCache(
            string cacheKey,
            string systemPrompt,
            string model,
            int? ttlMinutes = null,
            Action<bool, string> onComplete = null)
        {
            var ttl = ttlMinutes ?? DefaultTtlMinutes;

            yield return ContextCacheService.EnsureCacheExists(
                cacheKey,
                systemPrompt,
                model,
                ttl,
                response =>
                {
                    // Store cache info
                    _caches[cacheKey] = new CacheInfo
                    {
                        CacheName = response.cacheName,
                        ExpiresAt = DateTime.TryParse(response.expiresAt, out var dt) ? dt : DateTime.UtcNow.AddMinutes(ttl),
                        WasCreated = response.wasCreated
                    };

                    Debug.Log($"[ContextCacheManager] Cache stored: key='{cacheKey}', name='{response.cacheName}'");
                    onComplete?.Invoke(true, response.cacheName);
                },
                error =>
                {
                    Debug.LogError($"[ContextCacheManager] Failed to ensure cache for '{cacheKey}': {error}");
                    onComplete?.Invoke(false, error);
                }
            );
        }

        /// <summary>
        /// Gets the cache name for a given key, if available.
        /// </summary>
        /// <param name="cacheKey">The cache key</param>
        /// <returns>Cache name if available, null otherwise</returns>
        public string GetCacheName(string cacheKey)
        {
            return _caches.TryGetValue(cacheKey, out var info) ? info.CacheName : null;
        }

        /// <summary>
        /// Gets the full cache info for a given key.
        /// </summary>
        /// <param name="cacheKey">The cache key</param>
        /// <returns>Cache info if available, null otherwise</returns>
        public CacheInfo GetCacheInfo(string cacheKey)
        {
            return _caches.TryGetValue(cacheKey, out var info) ? info : null;
        }

        /// <summary>
        /// Checks if a cache exists and is not expired.
        /// </summary>
        /// <param name="cacheKey">The cache key</param>
        /// <returns>True if cache exists and is valid</returns>
        public bool HasValidCache(string cacheKey)
        {
            if (!_caches.TryGetValue(cacheKey, out var info))
                return false;

            // Check if expired (with 5 minute buffer)
            return info.ExpiresAt > DateTime.UtcNow.AddMinutes(5);
        }

        /// <summary>
        /// Removes a cache from the local storage.
        /// Note: This does not delete the cache from Gemini, just removes local reference.
        /// </summary>
        /// <param name="cacheKey">The cache key</param>
        public void RemoveCache(string cacheKey)
        {
            _caches.Remove(cacheKey);
        }

        /// <summary>
        /// Clears all cached references.
        /// </summary>
        public void ClearAll()
        {
            _caches.Clear();
        }

        /// <summary>
        /// Gets all currently stored cache keys.
        /// </summary>
        public IEnumerable<string> GetAllCacheKeys()
        {
            return _caches.Keys;
        }
    }
}
