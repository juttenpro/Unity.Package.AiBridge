using System.Collections;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Utils
{
    /// <summary>
    /// Utility class for finding StreamingAudioPlayer components in the scene.
    /// Extracted from BaseApiClient to reduce complexity and provide reusable functionality.
    /// </summary>
    public static class AudioPlayerLocator
    {
        /// <summary>
        /// Attempts to find a StreamingAudioPlayer component with retries.
        /// Searches in the GameObject, its children, and parent hierarchy.
        /// </summary>
        /// <param name="owner">The MonoBehaviour that needs the audio player</param>
        /// <param name="maxAttempts">Maximum number of attempts (default: 5)</param>
        /// <param name="callback">Callback with the found audio player (or null if not found)</param>
        /// <returns>Coroutine that performs the search</returns>
        public static IEnumerator FindAudioPlayerWithRetry(MonoBehaviour owner, int maxAttempts = 5, System.Action<StreamingAudioPlayer> callback = null)
        {
            StreamingAudioPlayer audioPlayer = null;
            
            // Try to find the audio player up to maxAttempts times
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    // Progressive delay between attempts
                    yield return new WaitForSeconds(0.1f * attempt);
                }
                
                // Try on the GameObject itself
                audioPlayer = owner.GetComponent<StreamingAudioPlayer>();
                
                if (!audioPlayer)
                {
                    // Try to find it in children
                    audioPlayer = owner.GetComponentInChildren<StreamingAudioPlayer>();
                }
                
                if (!audioPlayer && owner.transform.parent != null)
                {
                    // Try parent hierarchy
                    audioPlayer = owner.transform.parent.GetComponentInChildren<StreamingAudioPlayer>();
                }
                
                if (audioPlayer)
                {
                    Debug.Log($"[AudioPlayerLocator] Found StreamingAudioPlayer on attempt {attempt + 1} for {owner.name}");
                    callback?.Invoke(audioPlayer);
                    yield break;
                }
            }
            
            // Not found after all attempts
            Debug.LogError($"[AudioPlayerLocator] StreamingAudioPlayer not found after {maxAttempts} attempts for {owner.name}");
            callback?.Invoke(null);
        }
        
        /// <summary>
        /// Synchronously attempts to find a StreamingAudioPlayer component.
        /// Searches in the GameObject, its children, and parent hierarchy.
        /// </summary>
        /// <param name="owner">The MonoBehaviour that needs the audio player</param>
        /// <returns>The found StreamingAudioPlayer or null</returns>
        public static StreamingAudioPlayer FindAudioPlayer(MonoBehaviour owner)
        {
            // Try on the GameObject itself
            var audioPlayer = owner.GetComponent<StreamingAudioPlayer>();
            
            if (!audioPlayer)
            {
                // Try to find it in children
                audioPlayer = owner.GetComponentInChildren<StreamingAudioPlayer>();
            }
            
            if (!audioPlayer && owner.transform.parent != null)
            {
                // Try parent hierarchy
                audioPlayer = owner.transform.parent.GetComponentInChildren<StreamingAudioPlayer>();
            }
            
            if (audioPlayer)
            {
                Debug.Log($"[AudioPlayerLocator] Found StreamingAudioPlayer for {owner.name}");
            }
            else
            {
                Debug.LogError($"[AudioPlayerLocator] StreamingAudioPlayer not found for {owner.name}");
            }
            
            return audioPlayer;
        }
    }
}
