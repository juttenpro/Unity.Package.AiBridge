using System;
using System.Collections;
using UnityEngine;

namespace Tsc.AIBridge.Audio.Capture
{
    /// <summary>
    /// Microphone audio capture for Push-To-Talk (PTT) recording.
    /// Captures audio from the microphone and provides it as float arrays for processing.
    /// </summary>
    public class MicrophoneCapture : MonoBehaviour, IAudioCaptureProvider
    {
        #region Inspector Settings

        [Header("Audio Settings")]
        [SerializeField]
        [Tooltip("Enable echo cancellation/prevention (mutes AudioSource to prevent feedback)")]
        private bool enableEchoCancellation = true;

        [Header("Debug")]
        [SerializeField]
        [Tooltip("Enable volume calculation for debugging (slight performance cost)")]
        private bool calculateVolume = true;

        [SerializeField]
        [Tooltip("Enable verbose logging for debugging")]
        private bool enableVerboseLogging;

        #endregion
        #region Static Events

        /// <summary>
        /// Static event for global recording started notification (RecorderBase compatibility)
        /// </summary>
        public static event Action RecordingStarted;

        /// <summary>
        /// Static event for global recording stopped notification (RecorderBase compatibility)
        /// </summary>
        public static event Action RecordingStopped;

        /// <summary>
        /// Static event for global audio data received notification (RecorderBase compatibility)
        /// </summary>
        public static event Action<float[]> RecordingDataReceived;

        #endregion

        #region Events

        public event Action OnCaptureStarted;
        public event Action OnCaptureStopped;
        public event Action<float[]> OnAudioDataAvailable;
        public event Action<string> OnError;

        #endregion

        #region Configuration

        // IMPORTANT: Audio Sample Rate Architecture
        // =========================================
        // UPSTREAM (Microphone → Backend): 16 kHz
        //   - Used for Speech-to-Text (STT) processing
        //   - 16 kHz is sufficient for voice recognition
        //   - Lower bandwidth usage for upload
        //   - Industry standard for STT services (Google, Azure, etc.)
        //
        // DOWNSTREAM (Backend → Unity): 48 kHz
        //   - Used for Text-to-Speech (TTS) playback
        //   - Higher quality for NPC voice output
        //   - Better audio fidelity for realistic speech
        //   - Set in ConnectionParameters for backend communication
        //
        // DO NOT CONFUSE THESE TWO DIFFERENT STREAMS!
        private const int SAMPLE_RATE = 16000;  // UPSTREAM: Microphone capture for STT

        // Opus encoding bitrate for upstream audio (microphone → backend)
        // 64kbps provides good quality for voice recognition
        public const int UPSTREAM_OPUS_BITRATE = 64000;

        private const int RECORDING_LENGTH = 10; // 10 seconds circular buffer like RecorderBase
        private const float MUTE_CHECK_TIMEOUT = 0.5f; // Check if mic is muted after 0.5s

        #endregion

        #region Properties

        /// <summary>
        /// Static access to the standard sample rate used for audio capture
        /// </summary>
        public static int Frequency => SAMPLE_RATE;

        public bool IsCapturing { get; private set; }
        public int SampleRate => SAMPLE_RATE;
        public int Channels { get; private set; } = 1; // Will be set from actual recording clip
        public float CurrentVolume { get; private set; }
        public string SelectedDevice { get; private set; }

        /// <summary>
        /// Enable volume calculation (slight performance cost)
        /// </summary>
        public bool CalculateVolume
        {
            get => calculateVolume;
            set => calculateVolume = value;
        }

        /// <summary>
        /// Current position in the recording buffer
        /// </summary>
        public int CurrentPosition
        {
            get
            {
                if (recordingClip != null && Microphone.IsRecording(SelectedDevice))
                {
                    return Microphone.GetPosition(SelectedDevice);
                }
                return 0;
            }
        }

        /// <summary>
        /// Check if microphone is available
        /// </summary>
        public bool IsMicrophoneAvailable => Microphone.devices != null && Microphone.devices.Length > 0;

        #endregion

        #region Private Fields

        private AudioClip recordingClip;
        private int lastReadPosition = 0;
        private Coroutine captureCoroutine;
        private AudioSource audioSource;

        // Optimization: Buffer pooling to prevent GC allocations
        private float[] _reusableBuffer;
        private const int MAX_BUFFER_SIZE = 16000; // 1 second at 16kHz mono

        // Optimization: Cached microphone position
        private int _cachedPosition;
        private float _lastPositionCheckTime;
        private const float POSITION_CHECK_INTERVAL = 0.016f; // ~60Hz, enough for audio

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Select default microphone if available
            var devices = Microphone.devices;
            if (devices != null && devices.Length > 0)
            {
                SelectedDevice = devices[0];
            }

            // Set up audio source for echo prevention
            SetupEchoPrevention();

            // iOS specific audio setup
#if UNITY_IOS
            FixAudioOniOS();
#endif

            // Check microphone permission on mobile platforms
#if UNITY_IOS || UNITY_ANDROID
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                StartCoroutine(RequestMicrophonePermission());
            }
#endif
        }

        private IEnumerator Start()
        {
            // CRITICAL: Start microphone immediately at scene start (like old RecorderBase behavior)
            // This prevents hardware delays when PTT is pressed:
            // - Bluetooth headsets need time to switch from playback to headset mode (~100-300ms)
            // - Active Noise Cancellation (ANC) needs time to adjust (~50-200ms)
            // - Microphone gain adjustment takes time (~50-150ms)
            // Starting early ensures hardware is "warm" and ready for immediate recording

#if UNITY_IOS || UNITY_ANDROID
            // Wait for microphone permission on mobile platforms
            float timeout = 5f;
            float elapsed = 0f;
            while (!Application.HasUserAuthorization(UserAuthorization.Microphone) && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                Debug.LogError("[MicrophoneCapture] Microphone permission not granted after timeout!");
                yield break;
            }
#else
            yield return null; // Wait one frame for Awake to complete
#endif

            if (enableVerboseLogging)
                Debug.Log("[MicrophoneCapture] Starting microphone at scene start (prevents hardware switching delays)");
            StartCapture();
        }

        private void OnDestroy()
        {
            if (IsCapturing)
            {
                StopCapture();
            }

            if (recordingClip != null)
            {
                Destroy(recordingClip);
                recordingClip = null;
            }

            // Clean up pooled buffer
            _reusableBuffer = null;
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                // Headset removed / app paused - stop capture
                if (IsCapturing)
                {
                    StopCapture();
                    if (enableVerboseLogging)
                        Debug.Log("[MicrophoneCapture] Stopped capture due to application pause (headset removed)");
                }
            }
            else
            {
                // Headset put back on / app resumed - restart capture
                // CRITICAL: Without this, microphone stays stopped after headset is removed and put back on
                // This causes PTT to fail silently - button press works but no audio is sent
                if (!IsCapturing && IsMicrophoneAvailable)
                {
                    if (enableVerboseLogging)
                        Debug.Log("[MicrophoneCapture] Restarting capture after application resume (headset put back on)");
                    StartCapture();
                }
            }
        }

        #endregion

        #region Public Methods

        public void StartCapture()
        {
            if (IsCapturing)
            {
                Debug.LogWarning("[MicrophoneCapture] Already capturing");
                return;
            }

            if (!IsMicrophoneAvailable)
            {
                RaiseError("No microphone available");
                return;
            }

            if (string.IsNullOrEmpty(SelectedDevice))
            {
                var devices = Microphone.devices;
                if (devices == null || devices.Length == 0)
                {
                    RaiseError("No microphone devices found");
                    return;
                }

                // Use default device (first available)
                SelectedDevice = devices[0];
                if (enableVerboseLogging)
                    Debug.Log($"[MicrophoneCapture] Using default device: {SelectedDevice}");
            }

            // CRITICAL: Always use SAMPLE_RATE (16kHz), let Unity handle resampling
            // This matches RecorderBase behavior which works reliably across all devices
            // Unity will automatically resample if the device doesn't natively support 16kHz
            // Attempting to "smartly" adapt to device capabilities breaks Opus encoding
            // because Frequency property would return 16kHz while actual recording is 48kHz

            // Stop AudioSource first (like RecorderBase)
            audioSource.Stop();

            // Start recording with 10 second circular buffer (like RecorderBase)
            // ALWAYS use SAMPLE_RATE (16kHz) - Unity will resample if needed
            recordingClip = Microphone.Start(SelectedDevice, true, RECORDING_LENGTH, SAMPLE_RATE);

            if (recordingClip == null)
            {
                RaiseError("Failed to start microphone recording");
                return;
            }

            // Get actual channels from recording clip
            Channels = recordingClip.channels;

            // Configure AudioSource for echo prevention
            audioSource.clip = recordingClip;

            // Start waiting for microphone to be ready (like RecorderBase)
            StartCoroutine(WaitForMicrophoneReady());
        }

        public void StopCapture()
        {
            if (!IsCapturing)
            {
                return;
            }

            IsCapturing = false;

            // Stop coroutine
            if (captureCoroutine != null)
            {
                StopCoroutine(captureCoroutine);
                captureCoroutine = null;
            }

            // Stop microphone
            if (Microphone.IsRecording(SelectedDevice))
            {
                // Read any remaining data
                ReadRemainingData();
                Microphone.End(SelectedDevice);
            }

            // Stop AudioSource playback
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = null;
            }

            // Clean up
            if (recordingClip != null)
            {
                Destroy(recordingClip);
                recordingClip = null;
            }

            lastReadPosition = 0;
            CurrentVolume = 0f;

            OnCaptureStopped?.Invoke();
            RecordingStopped?.Invoke(); // Fire static event for RecorderBase compatibility
            if (enableVerboseLogging)
                Debug.Log("[MicrophoneCapture] Stopped capture");
        }

        public bool SelectDevice(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return false;

            var devices = Microphone.devices;
            if (devices == null)
                return false;

            foreach (var device in devices)
            {
                if (device == deviceName)
                {
                    SelectedDevice = deviceName;
                    return true;
                }
            }

            return false;
        }

        public string[] GetAvailableDevices()
        {
            return Microphone.devices;
        }


        #endregion

        #region Private Methods

        private void SetupEchoPrevention()
        {
            if (!enableEchoCancellation)
            {
                if (enableVerboseLogging)
                    Debug.Log("[MicrophoneCapture] Echo cancellation disabled");
                return;
            }

            // Get or add AudioSource for echo prevention
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
            }

            // CRITICAL: Always mute to prevent echo/feedback
            // This prevents the microphone audio from playing through speakers
            audioSource.mute = true;
            audioSource.loop = true;
            audioSource.ignoreListenerPause = true; // Keep in sync even when paused
        }

#if UNITY_IOS
        private void FixAudioOniOS()
        {
            // Use UnitySpeakerFix to force iOS audio to speakers
            // This is needed for iOS to properly handle audio routing
            // Based on RecorderBase implementation
            try
            {
                // Note: iPhoneSpeaker class would need to be imported or implemented
                // For now we'll use Unity's built-in iOS audio configuration
                UnityEngine.iOS.Device.SetNoBackupFlag(Application.persistentDataPath);

                // Force audio to speaker on iOS
                if (Microphone.devices.Length > 0)
                {
                    // Start and stop mic once to force proper audio routing
                    var tempClip = Microphone.Start(null, true, 1, SAMPLE_RATE);
                    if (tempClip != null)
                    {
                        Microphone.End(null);
                        Destroy(tempClip);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MicrophoneCapture] iOS audio fix failed: {e.Message}");
            }
        }
#endif

        private IEnumerator WaitForMicrophoneReady()
        {
            float muteTimer = MUTE_CHECK_TIMEOUT;

            // Wait for microphone to start recording (position > 0)
            while (Microphone.GetPosition(SelectedDevice) <= 0)
            {
                if (muteTimer > 0)
                {
                    muteTimer -= Time.deltaTime;
                    if (muteTimer <= 0)
                    {
                        if (!Microphone.IsRecording(SelectedDevice))
                        {
                            RaiseError("Microphone is muted or not available");
                            yield break;
                        }
                    }
                }
                yield return null;
            }

            // Now configure AudioSource playback (like RecorderBase)
            audioSource.loop = true;
            audioSource.ignoreListenerPause = true;
            audioSource.Play();

            lastReadPosition = 0;
            IsCapturing = true;

            // Start capture coroutine
            captureCoroutine = StartCoroutine(CaptureRoutine());

            OnCaptureStarted?.Invoke();
            RecordingStarted?.Invoke(); // Fire static event for RecorderBase compatibility
            if (enableVerboseLogging)
                Debug.Log($"[MicrophoneCapture] Started capture - Device: {SelectedDevice}, SampleRate: {SAMPLE_RATE}Hz, Channels: {Channels}");
        }

        private IEnumerator CaptureRoutine()
        {
            // This coroutine now just keeps running while capturing
            // Actual data reading happens in ProcessAudioData called from Update
            while (IsCapturing)
            {
                yield return null;
            }
        }

        private void Update()
        {
            // Process audio data like RecorderBase does in its Update loop
            if (IsCapturing && recordingClip != null)
            {
                ProcessAudioData();
            }
        }

        private void ProcessAudioData()
        {
            if (!IsCapturing || recordingClip == null)
                return;

            // Optimization: Cache position checks to reduce native calls
            if (Time.time - _lastPositionCheckTime >= POSITION_CHECK_INTERVAL)
            {
                _cachedPosition = Microphone.GetPosition(SelectedDevice);
                _lastPositionCheckTime = Time.time;
            }

            if (_cachedPosition <= 0)
                return;

            // Handle wraparound (circular buffer)
            if (lastReadPosition > _cachedPosition)
                lastReadPosition = 0;

            // Check if there's new data to read
            int samplesToRead = _cachedPosition - lastReadPosition;
            if (samplesToRead > 0)
            {
                // Optimization: Reuse buffer to prevent GC allocations
                int requiredSize = samplesToRead * Channels;

                // Only allocate if buffer doesn't exist or is too small
                if (_reusableBuffer == null || _reusableBuffer.Length < requiredSize)
                {
                    // Allocate with some headroom to reduce future allocations
                    int newSize = Mathf.Min(requiredSize * 2, MAX_BUFFER_SIZE);
                    _reusableBuffer = new float[newSize];
                }

                // Clamp requiredSize to actual buffer size to prevent Array.Copy overflow
                // This handles edge cases where timing glitches cause abnormally large sample counts
                int actualSize = Mathf.Min(requiredSize, _reusableBuffer.Length);

                // Read data into reusable buffer
                recordingClip.GetData(_reusableBuffer, lastReadPosition);

                // Calculate current volume only if needed (optimization)
                if (CalculateVolume)
                {
                    CurrentVolume = CalculateRMS(_reusableBuffer, actualSize);
                }

                // Create array segment to pass only the actual data
                // Note: We must create a new array here because listeners expect ownership
                // But this is now the ONLY allocation, and it's necessary
                float[] audioData = new float[actualSize];
                System.Array.Copy(_reusableBuffer, 0, audioData, 0, actualSize);

                OnAudioDataAvailable?.Invoke(audioData);
                RecordingDataReceived?.Invoke(audioData); // Fire static event for RecorderBase compatibility

                lastReadPosition = _cachedPosition;
            }
        }

        private void ReadRemainingData()
        {
            if (recordingClip == null)
                return;

            int currentPosition = Microphone.GetPosition(SelectedDevice);
            if (currentPosition > lastReadPosition)
            {
                int samplesToRead = currentPosition - lastReadPosition;
                int requiredSize = samplesToRead * Channels;

                // Clamp to maximum buffer size to prevent overflow from timing glitches
                int actualSize = Mathf.Min(requiredSize, MAX_BUFFER_SIZE);

                // Reuse buffer if possible for final read
                if (_reusableBuffer != null && _reusableBuffer.Length >= actualSize)
                {
                    recordingClip.GetData(_reusableBuffer, lastReadPosition);

                    // Must create new array for final data as ownership transfers
                    float[] finalData = new float[actualSize];
                    System.Array.Copy(_reusableBuffer, 0, finalData, 0, actualSize);
                    OnAudioDataAvailable?.Invoke(finalData);
                    RecordingDataReceived?.Invoke(finalData); // Fire static event
                }
                else
                {
                    // Fallback if buffer not available
                    float[] finalData = new float[actualSize];
                    recordingClip.GetData(finalData, lastReadPosition);
                    OnAudioDataAvailable?.Invoke(finalData);
                    RecordingDataReceived?.Invoke(finalData); // Fire static event
                }
            }
        }


        private float CalculateRMS(float[] samples, int length)
        {
            if (samples == null || length == 0)
                return 0f;

            float sum = 0f;
            // Only process the actual data length, not the entire buffer
            for (int i = 0; i < length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return Mathf.Sqrt(sum / length);
        }

        // Overload for compatibility
        private float CalculateRMS(float[] samples)
        {
            return CalculateRMS(samples, samples?.Length ?? 0);
        }

        private void RaiseError(string error)
        {
            Debug.LogError($"[MicrophoneCapture] {error}");
            OnError?.Invoke(error);
        }

#if UNITY_IOS || UNITY_ANDROID
        private IEnumerator RequestMicrophonePermission()
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);

            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                RaiseError("Microphone permission denied");
            }
        }
#endif

        #endregion
    }
}