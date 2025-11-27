using UnityEngine;

namespace Tsc.AIBridge.Audio.Playback
{
    /// <summary>
    /// Relay component that must be placed on the same GameObject as the AudioSource.
    /// Captures OnAudioFilterRead callbacks and forwards them to the StreamingAudioPlayer.
    /// This is necessary when the AudioSource is on a different GameObject (e.g., NPC's head for lip sync).
    ///
    /// IMPORTANT: For OVR LipSync compatibility, this component must execute BEFORE OVRLipSyncContext.
    /// This is enforced via Script Execution Order.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class AudioFilterRelay : MonoBehaviour
    {
        /// <summary>
        /// Event fired when audio data has been processed
        /// Used for speech segment detection and other analysis
        /// </summary>
        public event System.Action<float[], int> OnAudioProcessed;

        /// <summary>
        /// Get the underlying AudioSource component
        /// </summary>
        public AudioSource AudioSource => _audioSource;

        // Name of the streaming dummy clip - used to identify clips we created vs real AudioClips
        private const string StreamingClipName = "StreamingAudio_Relay";

        // The streaming player that will provide audio data
        private StreamingAudioPlayer _streamingPlayer;
        private AudioSource _audioSource;
        private bool _isInitialized;
        private bool _isPaused;
        private bool _isPlaybackActive = true;

        // Cached clip name for thread-safe access in OnAudioFilterRead
        // Updated on main thread (Update), read on audio thread (OnAudioFilterRead)
        private string _cachedClipName;

        // Reusable buffer for storing spatial weights during OnAudioFilterRead
        // Pre-allocated to avoid GC pressure on audio thread
        private float[] _spatialWeightsBuffer;

        // Tiny value used in dummy clip for spatial weight calculation
        // Must be small enough to be inaudible if leaked, but large enough to avoid floating point issues
        // NOTE: 1e-6 was too small - Unity's audio pipeline may clip/denormalize very small values
        // causing distance attenuation to not work correctly. 1e-4 (-80dB) is still inaudible
        // but large enough for proper spatial weight calculation including distance rolloff.
        private const float SpatialDummyValue = 1e-4f;


        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();

            if (!_audioSource)
            {
                var error = $"[AudioFilterRelay] CONFIGURATION ERROR: No AudioSource found on {gameObject.name}! " +
                           "AudioFilterRelay requires an AudioSource component on the same GameObject.";
                Debug.LogError(error);
                enabled = false;
                throw new MissingComponentException(error);
            }
        }

        /// <summary>
        /// Set the streaming player for this relay
        /// </summary>
        public void SetStreamingPlayer(StreamingAudioPlayer streamingPlayer)
        {
            _streamingPlayer = streamingPlayer;

            if (!_isInitialized)
            {
                Initialize();
            }
        }

        /// <summary>
        /// Initialize the relay
        /// </summary>
        private void Initialize()
        {
            Debug.Log($"[AudioFilterRelay] Initialize() called on {gameObject.name} - _isInitialized={_isInitialized}, _audioSource={((_audioSource != null) ? "NOT NULL" : "NULL")}");

            if (_isInitialized)
            {
                Debug.Log($"[AudioFilterRelay] Already initialized on {gameObject.name}, skipping");
                return;
            }

            _isInitialized = true;

            // CRITICAL: Initialize() may be called before Awake() due to Unity's script execution order
            // If _audioSource is null, try to get it now
            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
                Debug.Log($"[AudioFilterRelay] _audioSource was null, retrieved via GetComponent: {(_audioSource != null ? "SUCCESS" : "FAILED")}");
            }

            // Configure AudioSource for streaming with spatial audio support
            if (_audioSource)
            {
                _audioSource.Stop();

                // SPATIAL AUDIO TRICK: Create a dummy clip filled with 1.0 values
                // When Unity plays this clip with spatialBlend > 0, it calculates spatial weights
                // (distance attenuation, stereo panning, HRTF, reverb) and applies them to the samples.
                // Since our samples are 1.0, the resulting 'data' array in OnAudioFilterRead
                // contains the pure spatial weights. We multiply our streaming audio by these weights
                // to get proper 3D spatial audio for procedurally generated sound.
                // See: https://stackoverflow.com/questions/38843408/realtime-3d-audio-streaming-and-playback
                var sampleRate = 48000; // Default to Opus rate
                var channels = 1; // Mono for VAD processing and VR

                // Create short looping clip with tiny non-zero samples for spatial weight calculation
                var clipLength = sampleRate; // 1 second is enough for looping
                var streamingClip = AudioClip.Create(StreamingClipName, clipLength, channels, sampleRate, false);

                // Fill with tiny values so Unity's spatial calculations produce usable weights
                // Using a tiny value (1e-6) ensures any leakage during transitions is inaudible
                // We normalize the weights in OnAudioFilterRead by dividing by this value
                var dummyBuffer = new float[clipLength];
                for (int i = 0; i < clipLength; i++)
                {
                    dummyBuffer[i] = SpatialDummyValue;
                }
                streamingClip.SetData(dummyBuffer, 0);

                _audioSource.clip = streamingClip;
                _audioSource.loop = true;
                _audioSource.Play();

                // CRITICAL: Update cached clip name immediately for audio thread
                // Don't wait for Update() - OnAudioFilterRead needs this NOW for spatial audio
                _cachedClipName = streamingClip.name;

                // Check if Unity's output sample rate matches our TTS audio clip sample rate
                var systemSampleRate = AudioSettings.outputSampleRate;
                if (systemSampleRate != sampleRate)
                {
                    Debug.LogWarning(
                        $"[AIBridge] AudioSettings.outputSampleRate is {systemSampleRate}Hz but TTS audio requires {sampleRate}Hz.\n" +
                        $"This will cause incorrect playback speed (audio will play {(float)sampleRate / systemSampleRate:F2}x too fast/slow).\n" +
                        $"SOLUTION: Set Project Settings > Audio > System Sample Rate to {sampleRate}Hz, or configure at runtime:\n" +
                        $"  var config = AudioSettings.GetConfiguration();\n" +
                        $"  config.sampleRate = {sampleRate};\n" +
                        $"  AudioSettings.Reset(config);\n" +
                        $"See README.md for details."
                    );
                }

                Debug.Log($"[AudioFilterRelay] Initialized on {gameObject.name} - clip={(_audioSource.clip != null ? _audioSource.clip.name : "null")}, loop={_audioSource.loop}, isPlaying={_audioSource.isPlaying}");
            }
            else
            {
                Debug.LogError($"[AudioFilterRelay] Initialize() called but _audioSource is NULL on {gameObject.name}! Dummy clip was NOT created!");
            }
        }

        /// <summary>
        /// Clean up when destroyed
        /// </summary>
        public void Cleanup()
        {
            _streamingPlayer = null;
            _isInitialized = false;

            if (_audioSource && _audioSource.isPlaying)
            {
                _audioSource.Stop();
            }
        }

        /// <summary>
        /// Pause audio playback
        /// </summary>
        public void Pause()
        {
            _isPaused = true;
            if (_audioSource)
            {
                _audioSource.Pause();
            }
        }

        /// <summary>
        /// Resume audio playback
        /// </summary>
        public void Resume()
        {
            _isPaused = false;
            if (_audioSource)
            {
                _audioSource.UnPause();
            }
        }

        /// <summary>
        /// Set playback active state
        /// </summary>
        public void SetPlaybackActive(bool active)
        {
            _isPlaybackActive = active;
        }

        /// <summary>
        /// Start playback
        /// </summary>
        public void StartPlayback()
        {
            _isPlaybackActive = true;

            // DEFENSIVE PROGRAMMING: Ensure AudioSource exists
            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
                Debug.LogWarning($"[AudioFilterRelay] StartPlayback: _audioSource was null, retrieved via GetComponent: {(_audioSource != null ? "SUCCESS" : "FAILED")}");
            }

            if (_audioSource)
            {
                // CRITICAL: If the current clip is not our streaming dummy clip, we need to replace it
                // This handles the case where streaming starts while scripted audio is playing:
                // - NpcAudioPlayer plays scripted audio (e.g., N6.wav)
                // - Streaming audio arrives from backend while scripted audio is playing
                // - AudioSource.clip is still "N6", not "StreamingAudio_Relay"
                // - hasStreamingDummyClip=false → FALLBACK path → no spatial audio!
                //
                // We MUST replace ANY non-streaming clip with our dummy clip for spatial audio to work
                bool needsDummyClip = _audioSource.clip == null || _audioSource.clip.name != StreamingClipName;

                if (needsDummyClip)
                {
                    var reason = _audioSource.clip == null ? "clip is null" : $"clip is '{_audioSource.clip.name}' (not streaming dummy)";
                    Debug.LogWarning($"[AudioFilterRelay] StartPlayback: {reason}, recreating dummy clip for spatial audio");

                    var sampleRate = 48000;
                    var channels = 1;
                    var clipLength = sampleRate;
                    var streamingClip = AudioClip.Create(StreamingClipName, clipLength, channels, sampleRate, false);

                    var dummyBuffer = new float[clipLength];
                    for (int i = 0; i < clipLength; i++)
                    {
                        dummyBuffer[i] = SpatialDummyValue;
                    }
                    streamingClip.SetData(dummyBuffer, 0);

                    _audioSource.clip = streamingClip;
                    _audioSource.loop = true;

                    // CRITICAL: Update cached clip name immediately for audio thread
                    _cachedClipName = streamingClip.name;

                    Debug.Log($"[AudioFilterRelay] Recreated dummy clip: {streamingClip.name}");
                }

                // DEBUG: Log AudioSource state BEFORE unmute
                Debug.Log($"[AudioFilterRelay] StartPlayback on {gameObject.name} - BEFORE: mute={_audioSource.mute}, volume={_audioSource.volume}, enabled={_audioSource.enabled}, isPlaying={_audioSource.isPlaying}, clip={(_audioSource.clip != null ? _audioSource.clip.name : "null")}");

                _audioSource.mute = false; // Unmute when starting
                if (!_audioSource.isPlaying)
                {
                    _audioSource.Play();
                }

                // DEBUG: Log AudioSource state AFTER unmute
                Debug.Log($"[AudioFilterRelay] StartPlayback on {gameObject.name} - AFTER: mute={_audioSource.mute}, volume={_audioSource.volume}, enabled={_audioSource.enabled}, isPlaying={_audioSource.isPlaying}, clip={(_audioSource.clip != null ? _audioSource.clip.name : "null")}");
            }
            else
            {
                Debug.LogError($"[AudioFilterRelay] StartPlayback called but AudioSource is NULL on {gameObject.name}!");
            }
        }

        /// <summary>
        /// Stop playback and clear all audio buffers
        /// </summary>
        public void StopPlayback()
        {
            _isPlaybackActive = false;
            if (_audioSource)
            {
                _audioSource.mute = true; // Mute immediately for instant audio stop
                if (_audioSource.isPlaying)
                {
                    _audioSource.Stop();
                }

                // Extra safety: Recreate the streaming clip to ensure completely fresh state
                // This prevents any residual samples in the AudioClip itself
                var sampleRate = 48000; // TTS output frequency
                var channels = 1; // Mono

                // Destroy old clip if it exists - MUST use DestroyImmediate for instant cleanup
                // Unity's Destroy() is async and schedules destroy for end of frame
                // This can cause audio bleeding as DSP keeps old clip samples
                if (_audioSource.clip != null)
                {
                    // CRITICAL: Only destroy OUR streaming clips, not real AudioClips from Addressables!
                    // Destroying Addressable assets with allowDestroyingAssets:true corrupts Unity's asset database
                    // Real clips (from Addressables) should just be dereferenced, not destroyed
                    if (_audioSource.clip.name == StreamingClipName)
                    {
                        // This is our streaming dummy clip - safe to destroy
                        // allowDestroyingAssets: true is required for runtime-created AudioClips
                        UnityEngine.Object.DestroyImmediate(_audioSource.clip, true);
                    }
                    else
                    {
                        // This is a real AudioClip (e.g., from Addressables) - just dereference, don't destroy
                        // The clip will be properly released by Addressables when no longer needed
                        _audioSource.clip = null;
                    }
                }

                // Create fresh clip for next stream - filled with tiny values for spatial audio weights
                var clipLength = sampleRate; // 1 second is enough for looping
                var newClip = AudioClip.Create(StreamingClipName, clipLength, channels, sampleRate, false);
                var dummyBuffer = new float[clipLength];
                for (int i = 0; i < clipLength; i++)
                {
                    dummyBuffer[i] = SpatialDummyValue;
                }
                newClip.SetData(dummyBuffer, 0);

                _audioSource.clip = newClip;
                _audioSource.loop = true;

                // CRITICAL: Update cached clip name immediately for audio thread
                // This is the most important place - streaming audio will start right after StopPlayback
                // and OnAudioFilterRead needs to see the streaming dummy clip name for spatial audio to work
                _cachedClipName = newClip.name;

                Debug.Log($"[AudioFilterRelay] StopPlayback recreated dummy clip on {gameObject.name} - clip={(_audioSource.clip != null ? _audioSource.clip.name : "null")}");

                // CRITICAL: Reset AudioSource internal buffers to prevent audio bleeding
                // Unity's DSP pipeline can retain samples in internal buffers (~100-200ms)
                // Setting time=0 forces Unity to clear these buffers
                // IMPORTANT: Must be called AFTER clip is created, otherwise Unity throws warning
                _audioSource.time = 0f;

                // Unmute now that buffers are cleared - AudioSource is ready for next audio
                // We only muted temporarily to prevent audio bleeding during the transition
                _audioSource.mute = false;
            }
        }

        /// <summary>
        /// Update cached clip name on main thread for thread-safe access in OnAudioFilterRead.
        /// OnAudioFilterRead runs on audio thread and cannot access Unity properties like AudioSource.clip.
        /// </summary>
        private void Update()
        {
            // Cache clip name on main thread for audio thread access
            // This prevents threading exceptions when checking clip type in OnAudioFilterRead
            if (_audioSource != null && _audioSource.clip != null)
            {
                _cachedClipName = _audioSource.clip.name;
            }
            else
            {
                _cachedClipName = null;
            }
        }

        /// <summary>
        /// Unity callback for audio processing.
        /// This is called by Unity's audio thread when the AudioSource needs audio data.
        /// Supports two modes:
        /// 1. Passthrough mode: When a real AudioClip is assigned (e.g., pre-recorded MP3),
        ///    passes the audio through unchanged for OVRLipSync and normal playback.
        /// 2. Streaming mode: When using the streaming dummy clip, forwards to StreamingAudioPlayer.
        /// </summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            // Check if streaming is actively playing
            // Streaming has priority over passthrough mode
            bool isStreamingActive = _isPlaybackActive &&
                                     _streamingPlayer != null &&
                                     _streamingPlayer.IsPlaybackActive;

            // PASSTHROUGH MODE: Check if AudioSource has a real clip (not the streaming dummy)
            // AND streaming is not active
            // This allows VoiceLinePlayer to play pre-recorded audio (MP3/WAV) through the same
            // AudioSource while maintaining OVRLipSync compatibility
            // NOTE: Use cached clip name to avoid threading exceptions (OnAudioFilterRead runs on audio thread)
            bool hasStreamingDummyClip = !string.IsNullOrEmpty(_cachedClipName) &&
                                         _cachedClipName.StartsWith("StreamingAudio");
            bool hasRealClip = !string.IsNullOrEmpty(_cachedClipName) && !hasStreamingDummyClip;

            if (hasRealClip && !isStreamingActive)
            {
                // Passthrough: Let Unity's AudioSource play the clip normally
                // The data array already contains the clip samples from Unity's audio engine
                // We just need to forward it to listeners (OVRLipSync, VAD) without modification
                OnAudioProcessed?.Invoke(data, channels);
                return;
            }

            // STREAMING MODE: Procedural audio with spatial audio support
            if (!_isInitialized || _streamingPlayer == null || _isPaused || !_isPlaybackActive)
            {
                // Fill with silence if not ready or paused
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = 0f;
                }
                return;
            }

            // SPATIAL AUDIO: The 'data' array currently contains spatial weights from Unity
            // (because we're playing a clip filled with SpatialDummyValue, and Unity applied spatial processing)
            // The weights are scaled by SpatialDummyValue, so we normalize by dividing
            // We need to: 1) Store & normalize weights, 2) Fill with our samples, 3) Multiply by weights
            //
            // CRITICAL FIX: Only calculate spatial weights if we have the streaming dummy clip!
            // If a real clip is still loaded (transition from scripted to streaming audio),
            // the data array contains real audio samples (e.g., 0.5) instead of SpatialDummyValue (1e-6).
            // Multiplying by invDummyValue (1,000,000) would result in MASSIVE gain causing distortion!
            // In this case, skip spatial processing and use weights of 1.0 (no spatial attenuation).

            // Ensure buffer is large enough (lazy allocation, only grows)
            if (_spatialWeightsBuffer == null || _spatialWeightsBuffer.Length < data.Length)
            {
                _spatialWeightsBuffer = new float[data.Length];
            }

            if (hasStreamingDummyClip)
            {
                // NORMAL PATH: Streaming dummy clip is loaded, calculate spatial weights correctly
                // Unity calculated: output = SpatialDummyValue * spatialWeight
                // So: spatialWeight = output / SpatialDummyValue
                var invDummyValue = 1f / SpatialDummyValue;

                for (int i = 0; i < data.Length; i++)
                {
                    _spatialWeightsBuffer[i] = data[i] * invDummyValue;
                }
            }
            else
            {
                // FALLBACK PATH: Real clip still loaded during streaming transition
                // Skip spatial weight calculation - use unity gain (1.0) to prevent distortion
                // This briefly disables spatial audio but prevents severe oversaturation/clipping
                for (int i = 0; i < data.Length; i++)
                {
                    _spatialWeightsBuffer[i] = 1.0f;
                }
            }

            // Fill buffer with streaming audio samples (this overwrites data)
            _streamingPlayer.FillAudioBuffer(data, channels);

            // Apply spatial weights to get proper 3D positioning
            for (int i = 0; i < data.Length; i++)
            {
                data[i] *= _spatialWeightsBuffer[i];
            }

            // Fire event for any listeners (e.g., VAD processors)
            OnAudioProcessed?.Invoke(data, channels);
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        /// <summary>
        /// Get the streaming audio player this relay is connected to
        /// </summary>
        public StreamingAudioPlayer GetStreamingPlayer()
        {
            return _streamingPlayer;
        }

        /// <summary>
        /// Check if this relay is initialized and ready
        /// </summary>
        public bool IsInitialized => _isInitialized && _streamingPlayer != null;
    }
}