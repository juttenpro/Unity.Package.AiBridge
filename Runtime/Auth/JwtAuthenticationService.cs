using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using SimulationCrew.AIBridge.WebSocket;
using SimulationCrew.AIBridge.Configuration;

namespace SimulationCrew.AIBridge.Auth
{
    /// <summary>
    /// JWT-based authentication service for API access with token caching
    /// </summary>
    public class JwtAuthenticationService : IAuthenticationService
    {
        private readonly string _baseUrl;
        private string _cachedToken;
        private DateTime _tokenExpiry;
        private readonly object _cacheLock = new object();
        
        /// <summary>
        /// Gets the cached JWT token if available and not expired
        /// </summary>
        public string CachedToken
        {
            get
            {
                lock (_cacheLock)
                {
                    if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
                    {
                        return _cachedToken;
                    }
                    return null;
                }
            }
        }
        
        public JwtAuthenticationService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? WebSocketClient.Instance?.ApiBaseUrl ?? "https://conversation-api.com";
        }
        
        public async Task<string> GetAuthTokenAsync(string userId, string role, string apiKey)
        {
            var startTime = DateTime.Now;
            
            // Check if we have a valid cached token
            lock (_cacheLock)
            {
                if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
                {
                    //Debug.Log($"[AuthenticationService] Using cached token for user: {userId} (0ms - from cache)");
                    return _cachedToken;
                }
            }
            
            //Debug.Log($"[AuthenticationService] Requesting new JWT token for user: {userId}");
            var fullUrl = $"{_baseUrl}{ApiEndpoints.AuthToken}";
            
            using var request = new UnityWebRequest(fullUrl, "POST");
            var tokenRequest = new { userId, role };
            var jsonBody = JsonConvert.SerializeObject(tokenRequest);
            
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader(ApiEndpoints.ContentTypeHeader, ApiEndpoints.ContentTypeJson);
            request.SetRequestHeader(ApiEndpoints.ApiKeyHeader, apiKey);
            
            var operation = request.SendWebRequest();
            
            while (!operation.isDone)
            {
                await Task.Yield();
            }
            
            if (request.result != UnityWebRequest.Result.Success)
            {
                var duration = (DateTime.Now - startTime).TotalMilliseconds;
                Debug.LogError($"[AuthenticationService] Failed to get auth token after {duration:F0}ms: {request.error}");
                return null;
            }
            
            try
            {
                var response = JsonConvert.DeserializeObject<TokenResponse>(request.downloadHandler.text);
                //var duration = (DateTime.Now - startTime).TotalMilliseconds;
                //Debug.Log($"[AuthenticationService] Successfully obtained auth token for user: {userId} ({duration:F0}ms)");
                
                // Cache the token for future use (tokens typically valid for 1 hour)
                if (!string.IsNullOrEmpty(response?.token))
                {
                    lock (_cacheLock)
                    {
                        _cachedToken = response.token;
                        _tokenExpiry = DateTime.UtcNow.AddMinutes(55); // Refresh 5 minutes before expiry
                    }
                }
                
                return response?.token;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AuthenticationService] Failed to parse auth response: {e.Message}");
                return null;
            }
        }
        
        [Serializable]
        private class TokenResponse
        {
            public string token;
        }
    }
}
