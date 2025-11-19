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

        // The streaming player that will provide audio data
        private StreamingAudioPlayer _streamingPlayer;
        private AudioSource _audioSource;
        private bool _isInitialized;
        private bool _isPaused;
        private bool _isPlaybackActive = true;


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
            if (_isInitialized)
            {
                return;
            }

            _isInitialized = true;

            // Configure AudioSource for streaming
            if (_audioSource)
            {
                _audioSource.Stop();

                // Create a dummy clip - Unity requires this for OnAudioFilterRead to work
                // We'll be generating the actual audio in OnAudioFilterRead
                var sampleRate = 48000; // Default to Opus rate
                var clipLength = sampleRate * 10; // 10 seconds, will loop if needed
                var channels = 1; // Mono for VAD processing and VR

                var streamingClip = AudioClip.Create("StreamingAudio_Relay", clipLength, channels, sampleRate, true);
                _audioSource.clip = streamingClip;
                _audioSource.loop = true;
                _audioSource.Play();

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
            }

            //Debug.Log($"[AudioFilterRelay] Initialized on {gameObject.name}");
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
            if (_audioSource)
            {
                _audioSource.mute = false; // Unmute when starting
                if (!_audioSource.isPlaying)
                {
                    _audioSource.Play();
                }
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
                var clipLength = sampleRate * 10; // 10 seconds
                var channels = 1; // Mono

                // Destroy old clip if it exists - MUST use DestroyImmediate for instant cleanup
                // Unity's Destroy() is async and schedules destroy for end of frame
                // This can cause audio bleeding as DSP keeps old clip samples
                if (_audioSource.clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(_audioSource.clip);
                }

                // Create fresh clip for next stream
                _audioSource.clip = AudioClip.Create("StreamingAudio_Relay", clipLength, channels, sampleRate, true);
                _audioSource.loop = true;

                // CRITICAL: Reset AudioSource internal buffers to prevent audio bleeding
                // Unity's DSP pipeline can retain samples in internal buffers (~100-200ms)
                // Setting time=0 forces Unity to clear these buffers
                // IMPORTANT: Must be called AFTER clip is created, otherwise Unity throws warning
                _audioSource.time = 0f;
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
            bool hasRealClip = _audioSource != null &&
                               _audioSource.clip != null &&
                               !_audioSource.clip.name.StartsWith("StreamingAudio");

            if (hasRealClip && !isStreamingActive)
            {
                // Passthrough: Let Unity's AudioSource play the clip normally
                // The data array already contains the clip samples from Unity's audio engine
                // We just need to forward it to listeners (OVRLipSync, VAD) without modification
                OnAudioProcessed?.Invoke(data, channels);
                return;
            }

            // STREAMING MODE: Original streaming audio logic
            if (!_isInitialized || _streamingPlayer == null || _isPaused || !_isPlaybackActive)
            {
                // Fill with silence if not ready or paused
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = 0f;
                }
                return;
            }

            // Let the streaming player fill the audio buffer
            _streamingPlayer.FillAudioBuffer(data, channels);

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