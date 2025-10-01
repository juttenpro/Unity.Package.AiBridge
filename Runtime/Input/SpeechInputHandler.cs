using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tsc.AIBridge.Audio.Capture;
using UnityEngine.InputSystem;
using Tsc.AIBridge.Audio.VAD;

namespace Tsc.AIBridge.Input
{
    /// <summary>
    /// Handles speech input through Push-To-Talk (PTT) or Voice Activation.
    /// Provides a clean interface for user speech events.
    /// Internal complexity (PTT/VAD/SmartOffset) is handled by RecordingController.
    /// </summary>
    public class SpeechInputHandler : MonoBehaviour
    {
        #region Events

        /// <summary>
        /// Fired when user starts speaking (regardless of PTT or VAD)
        /// </summary>
#pragma warning disable 0067 // Event is never used - Available for external scripts
        public event Action OnUserStartedSpeaking;
#pragma warning restore 0067

        /// <summary>
        /// Fired when user stops speaking (after all delays/offsets)
        /// </summary>
#pragma warning disable 0067 // Event is never used - Available for external scripts
        public event Action OnUserStoppedSpeaking;
#pragma warning restore 0067

        /// <summary>
        /// Fired when audio data is available
        /// </summary>
        public event Action<float[]> OnAudioDataReceived;

        /// <summary>
        /// Legacy event for PTT button press (for UI feedback)
        /// </summary>
        public static event Action OnPttButtonPressed;

        /// <summary>
        /// Legacy event for PTT button release (for UI feedback)
        /// </summary>
        public static event Action OnPttButtonReleased;

        /// <summary>
        /// Fired when STT has completed processing user speech.
        /// Perfect moment for VAD calibration before NPC responds.
        /// </summary>
        public static event Action OnSpeechProcessingCompleted;

        /// <summary>
        /// Event fired when recording starts
        /// </summary>
        public event Action OnRecordingStarted;

        /// <summary>
        /// Event fired when recording stops
        /// </summary>
        public event Action OnRecordingStopped;

        #endregion

        #region Settings

        [Header("Input Configuration")]
        [SerializeField] private InputActionReference[] talkButtons;

        [Header("Recording Settings")]
        [SerializeField]
        [Tooltip("Delay before stopping recording after PTT release")]
        private float stopRecordingDelay = 0.25f;

        [SerializeField]
        [Tooltip("Use Voice Activity Detection for smart mic offset")]
        private bool useSmartMicOffset = true;

        [SerializeField]
        [Range(0.1f, 1.0f)]
        [Tooltip("Max time to keep mic open after PTT to check if user stopped")]
        private float smartOffsetMaxDuration = 0.5f;

        [SerializeField]
        [Range(0.05f, 0.3f)]
        [Tooltip("Silence duration to confirm user stopped speaking")]
        private float silenceThreshold = 0.1f;

        [Header("VAD Settings")]
        [SerializeField]
        [Tooltip("Enable adaptive VAD that learns noise levels during silence")]
        private bool useAdaptiveVAD = true;

        [SerializeField]
        [Range(0.005f, 0.05f)]
        [Tooltip("Fixed threshold for speech detection when adaptive is off")]
        private float fixedVadThreshold = 0.02f;

        [SerializeField]
        [Range(0.005f, 0.03f)]
        [Tooltip("How much above noise floor to set threshold (adaptive mode)")]
        private float adaptiveMargin = 0.015f;

        [SerializeField]
        [Range(0.005f, 0.02f)]
        [Tooltip("Minimum threshold even in silent environments")]
        private float minimumThreshold = 0.01f;

        [Header("Voice Activation")]
        [SerializeField]
        [Tooltip("Enable voice activation without PTT (open mic mode)")]
        private bool useVoiceActivation;

        [SerializeField]
        [Range(0.2f, 2.0f)]
        [Tooltip("Pre-buffer duration for voice activation")]
        private float voiceActivationPreBufferTime = 0.5f;

        [SerializeField]
        [Range(0.05f, 0.5f)]
        [Tooltip("Speech duration required to trigger recording")]
        private float voiceActivationOnsetTime = 0.15f;

        [SerializeField]
        [Range(0.3f, 2.0f)]
        [Tooltip("Silence duration to stop recording")]
        private float voiceActivationSilenceTimeout = 0.8f;

        [Header("Custom Vocabulary")]
        [Tooltip("Domain-specific words for better STT recognition. One word per line or separated by commas.")]
        [TextArea(3, 5)]
        [SerializeField] private string customVocabularyText = "";

        [Header("Debug")]
        [SerializeField] private bool enableVerboseLogging;

        #endregion

        #region Private Fields

        private MicrophoneCapture _microphoneCapture;
        private VADManager _vadManager;
        private RecordingController _recordingController;
        private bool _isCapturing;

        // Voice activation pre-buffer
        private CircularAudioBuffer _preBuffer;

        // Audio encoding for upstream (player → backend)
        private Audio.Processing.AudioStreamProcessor _audioStreamProcessor;

        // Recording state
        private bool _isRecording;
        private bool _isPttPressed;
        private float _pttPressTime;
        private Coroutine _stopRecordingCoroutine;

        // Voice activation state
        private bool _isVoiceActivated;
        private float _voiceOnsetTimer;
        private float _lastVoiceTime;
        private float _silenceTimer;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the currently active microphone device name
        /// </summary>
        public string ActiveMicrophoneName => _microphoneCapture?.SelectedDevice;

        /// <summary>
        /// Gets whether recording is currently active
        /// </summary>
        public bool IsRecording => _isRecording;

        /// <summary>
        /// Check if Push-To-Talk is currently active
        /// </summary>
        public bool IsPushToTalkActive() => _isPttPressed;

        /// <summary>
        /// Get the duration of the current PTT press in seconds
        /// </summary>
        public float GetPushToTalkDuration() => _isPttPressed ? Time.time - _pttPressTime : 0f;

        /// <summary>
        /// Gets the static stop recording delay for external access
        /// </summary>
        public static float StopRecordingDelayStatic { get; private set; } = 0.25f;

        /// <summary>
        /// Gets whether the user is currently speaking according to VAD
        /// </summary>
        public bool IsUserSpeaking => _vadManager?.IsUserSpeaking ?? false;

        /// <summary>
        /// Get the VAD manager for advanced operations
        /// </summary>
        public VADManager VadManager => _vadManager;

        /// <summary>
        /// Gets the parsed custom vocabulary list
        /// </summary>
        public List<string> ParsedCustomVocabulary => ParseCustomVocabulary();

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Find or create MicrophoneCapture
            _microphoneCapture = GetComponent<MicrophoneCapture>();
            if (_microphoneCapture == null)
            {
                _microphoneCapture = gameObject.AddComponent<MicrophoneCapture>();
            }

            // Setup pre-buffer for voice activation
            if (useVoiceActivation)
            {
                var bufferSamples = Mathf.CeilToInt(voiceActivationPreBufferTime * MicrophoneCapture.Frequency);
                _preBuffer = new CircularAudioBuffer(bufferSamples);
            }

            // Initialize AudioStreamProcessor for encoding (NO audioPlayer = encoding-only mode)
            _audioStreamProcessor = new Audio.Processing.AudioStreamProcessor(
                audioPlayer: null, // encoding-only mode
                opusBitrate: MicrophoneCapture.UPSTREAM_OPUS_BITRATE,
                bufferDuration: 0f, // not used for encoding
                isVerboseLogging: enableVerboseLogging
            );

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] AudioStreamProcessor initialized for upstream encoding");

            StopRecordingDelayStatic = stopRecordingDelay;
        }

        private void Start()
        {
            // Subscribe to microphone events
            _microphoneCapture.OnAudioDataAvailable += HandleAudioData;
            _microphoneCapture.OnCaptureStarted += HandleCaptureStarted;
            _microphoneCapture.OnCaptureStopped += HandleCaptureStopped;

            // Setup input actions
            SetupInputActions();
        }

        private void OnDestroy()
        {
            // Cleanup
            if (_microphoneCapture != null)
            {
                _microphoneCapture.OnAudioDataAvailable -= HandleAudioData;
                _microphoneCapture.OnCaptureStarted -= HandleCaptureStarted;
                _microphoneCapture.OnCaptureStopped -= HandleCaptureStopped;
            }

            CleanupInputActions();

            if (_stopRecordingCoroutine != null)
            {
                StopCoroutine(_stopRecordingCoroutine);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Start recording manually
        /// </summary>
        public void StartRecording()
        {
            if (_isRecording)
                return;

            _isRecording = true;

            // Start microphone capture
            _microphoneCapture.StartCapture();

            // Start audio encoding - buffering is controlled by RequestOrchestrator
            if (_audioStreamProcessor == null)
            {
                Debug.LogError("[SpeechInputHandler] _audioStreamProcessor is NULL! Cannot start encoding!");
            }
            else
            {
                Debug.Log("[SpeechInputHandler] Calling StartEncoding() on AudioStreamProcessor");
                _audioStreamProcessor.StartEncoding();
            }

            OnRecordingStarted?.Invoke();

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] Recording and encoding started");
        }

        /// <summary>
        /// Stop recording manually
        /// </summary>
        public void StopRecording()
        {
            // CRITICAL: Never stop recording while PTT is pressed
            // VAD should only control recording stop when:
            // 1. useVoiceActivation is true (open mic mode), OR
            // 2. PTT is NOT pressed AND useSmartMicOffset is true
            if (_isPttPressed)
            {
                if (enableVerboseLogging)
                    Debug.LogWarning("[SpeechInputHandler] Attempted to stop recording while PTT pressed - ignoring!");
                return;
            }

            if (!_isRecording)
                return;

            _isRecording = false;
            _microphoneCapture.StopCapture();

            // Stop audio encoding
            _audioStreamProcessor?.StopEncoding();

            OnRecordingStopped?.Invoke();

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] Recording and encoding stopped");
        }

        /// <summary>
        /// Run post-PTT calibration for VAD
        /// </summary>
        public void RunPostPTTCalibration()
        {
            // Force VAD adaptation after PTT release
            _vadManager?.ForceAdaptation();

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] Post-PTT calibration completed");
        }

        /// <summary>
        /// Inform VAD that NPC is speaking (prevents threshold adaptation)
        /// </summary>
        public void SetNpcSpeaking(bool isSpeaking)
        {
            if (_vadManager != null)
            {
                _vadManager.SetNpcSpeaking(isSpeaking);
            }
        }

        /// <summary>
        /// Called when STT processing is complete.
        /// This triggers VAD calibration at the optimal moment between user speech and NPC response.
        /// </summary>
        public void NotifySTTCompleted()
        {
            // Trigger calibration at the perfect moment
            _vadManager?.CalibrateBetweenSTTAndNPC();

            // Fire event for other systems
            OnSpeechProcessingCompleted?.Invoke();

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] STT completed - VAD calibration triggered");
        }

        /// <summary>
        /// Called when interruption is approved
        /// </summary>
        public void OnInterruptionApproved()
        {
            // Stop any ongoing recording
            if (_stopRecordingCoroutine != null)
            {
                StopCoroutine(_stopRecordingCoroutine);
                _stopRecordingCoroutine = null;
            }

            StopRecording();

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] Interruption approved - recording stopped");
        }

        /// <summary>
        /// Initialize for testing purposes (compatibility method)
        /// </summary>
        public void InitializeForTesting()
        {
            // Ensure components are initialized
            if (_microphoneCapture == null)
            {
                _microphoneCapture = GetComponent<MicrophoneCapture>();
                if (_microphoneCapture == null)
                {
                    _microphoneCapture = gameObject.AddComponent<MicrophoneCapture>();
                }
            }

            if (_vadManager == null)
            {
                _vadManager = new VADManager(enableVerboseLogging);

                // Apply VAD settings from Inspector
                if (useAdaptiveVAD)
                {
                    _vadManager.SetAdaptiveSettings(adaptiveMargin, minimumThreshold);
                }
                else
                {
                    _vadManager.SetFixedThreshold(fixedVadThreshold);
                }
            }

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] Initialized for testing");
        }

        /// <summary>
        /// Get the AudioStreamProcessor for upstream encoding (used by RequestOrchestrator)
        /// </summary>
        public Audio.Processing.AudioStreamProcessor AudioStreamProcessor => _audioStreamProcessor;

        #endregion

        #region Input Handling

        private void SetupInputActions()
        {
            if (talkButtons == null || talkButtons.Length == 0)
                return;

            foreach (var actionRef in talkButtons)
            {
                if (actionRef?.action != null)
                {
                    actionRef.action.Enable();
                    actionRef.action.started += OnPttPressed;
                    actionRef.action.canceled += OnPttReleased;
                }
            }
        }

        private void CleanupInputActions()
        {
            if (talkButtons == null)
                return;

            foreach (var actionRef in talkButtons)
            {
                if (actionRef?.action != null)
                {
                    actionRef.action.started -= OnPttPressed;
                    actionRef.action.canceled -= OnPttReleased;
                }
            }
        }

        private void OnPttPressed(InputAction.CallbackContext context)
        {
            _isPttPressed = true;
            _pttPressTime = Time.time;

            OnPttButtonPressed?.Invoke();

            if (_stopRecordingCoroutine != null)
            {
                StopCoroutine(_stopRecordingCoroutine);
                _stopRecordingCoroutine = null;
            }

            StartRecording();

            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] PTT pressed");
        }

        private void OnPttReleased(InputAction.CallbackContext context)
        {
            _isPttPressed = false;

            OnPttButtonReleased?.Invoke();

            // CRITICAL FIX: Reset silence timer when PTT is released
            // This prevents immediate stop if user was silent during PTT
            _silenceTimer = 0;

            // Start delayed stop or smart offset
            if (useSmartMicOffset)
            {
                _stopRecordingCoroutine = StartCoroutine(SmartStopRecording());
            }
            else
            {
                _stopRecordingCoroutine = StartCoroutine(DelayedStopRecording());
            }

            if (enableVerboseLogging)
                Debug.Log($"[SpeechInputHandler] PTT released after {Time.time - _pttPressTime:F2}s");
        }

        #endregion

        #region Audio Processing

        private void HandleAudioData(float[] samples)
        {
            if (samples == null || samples.Length == 0)
            {
                Debug.LogWarning("[SpeechInputHandler] HandleAudioData received null or empty samples!");
                return;
            }

            Debug.Log($"[SpeechInputHandler] HandleAudioData received {samples.Length} samples, _isRecording={_isRecording}");

            // Update VAD
            var pttDuration = _isPttPressed ? Time.time - _pttPressTime : 0f;
            var isSpeaking = _vadManager?.ProcessAudioFrame(samples, pttDuration) ?? false;
            // IsUserSpeaking is now a property that reads from _vadManager

            // Handle voice activation
            if (useVoiceActivation && !_isPttPressed)
            {
                ProcessVoiceActivation(samples, isSpeaking);
            }

            // Forward audio if recording
            if (_isRecording)
            {
                Debug.Log($"[SpeechInputHandler] Forwarding {samples.Length} samples to AudioStreamProcessor");
                // Send to AudioStreamProcessor for encoding
                // Buffering is controlled by RequestOrchestrator via AudioStreamProcessor.StartBuffering()
                _audioStreamProcessor?.ProcessRecordingData(samples);

                // Also fire event for other listeners (e.g., InterruptionManager)
                OnAudioDataReceived?.Invoke(samples);
            }
            else
            {
                Debug.LogWarning($"[SpeechInputHandler] NOT forwarding audio - _isRecording is false");
            }

            // Track silence for smart offset
            // CRITICAL FIX: Only track silence AFTER PTT is released, not during PTT
            // This prevents the timer from accumulating while user holds PTT silently
            if (useSmartMicOffset && !_isPttPressed && _isRecording)
            {
                if (!isSpeaking)
                {
                    _silenceTimer += samples.Length / (float)MicrophoneCapture.Frequency;
                }
                else
                {
                    _silenceTimer = 0;
                }
            }
        }

        private void ProcessVoiceActivation(float[] samples, bool isSpeaking)
        {
            // Add to pre-buffer
            _preBuffer?.AddSamples(samples);

            if (isSpeaking)
            {
                _lastVoiceTime = Time.time;

                if (!_isVoiceActivated)
                {
                    _voiceOnsetTimer += samples.Length / (float)MicrophoneCapture.Frequency;

                    if (_voiceOnsetTimer >= voiceActivationOnsetTime)
                    {
                        // Activate recording
                        _isVoiceActivated = true;
                        StartRecording();

                        // Send pre-buffered audio
                        if (_preBuffer != null)
                        {
                            var preBufferedAudio = _preBuffer.GetAllSamples();
                            if (preBufferedAudio != null && preBufferedAudio.Length > 0)
                            {
                                OnAudioDataReceived?.Invoke(preBufferedAudio);
                            }
                        }

                        if (enableVerboseLogging)
                            Debug.Log("[SpeechInputHandler] Voice activation triggered");
                    }
                }
            }
            else
            {
                _voiceOnsetTimer = 0;

                if (_isVoiceActivated && Time.time - _lastVoiceTime > voiceActivationSilenceTimeout)
                {
                    // Deactivate recording
                    _isVoiceActivated = false;
                    StopRecording();

                    if (enableVerboseLogging)
                        Debug.Log("[SpeechInputHandler] Voice activation ended");
                }
            }
        }

        private void HandleCaptureStarted()
        {
            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] Microphone capture started");
        }

        private void HandleCaptureStopped()
        {
            if (enableVerboseLogging)
                Debug.Log("[SpeechInputHandler] Microphone capture stopped");
        }

        #endregion

        #region Recording Control

        private IEnumerator DelayedStopRecording()
        {
            yield return new WaitForSeconds(stopRecordingDelay);
            StopRecording();
        }

        private IEnumerator SmartStopRecording()
        {
            _silenceTimer = 0;
            float elapsedTime = 0;

            while (elapsedTime < smartOffsetMaxDuration)
            {
                if (_silenceTimer >= silenceThreshold)
                {
                    // User stopped speaking
                    break;
                }

                if (_isPttPressed)
                {
                    // PTT pressed again, abort
                    yield break;
                }

                yield return null;
                elapsedTime += Time.deltaTime;
            }

            StopRecording();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Parses the custom vocabulary text into a list of words
        /// </summary>
        private List<string> ParseCustomVocabulary()
        {
            if (string.IsNullOrWhiteSpace(customVocabularyText))
                return null;

            // Split by common delimiters: comma, semicolon, newline
            var words = customVocabularyText
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim())
                .Where(w => !string.IsNullOrEmpty(w))
                .Distinct() // Remove duplicates
                .ToList();

            return words.Count > 0 ? words : null;
        }

        #endregion
    }


    /// <summary>
    /// Circular buffer for audio pre-buffering
    /// </summary>
    public class CircularAudioBuffer
    {
        private readonly float[] _buffer;
        private int _writePosition;
        private bool _isFull;

        public CircularAudioBuffer(int capacity)
        {
            _buffer = new float[capacity];
        }

        public void AddSamples(float[] samples)
        {
            if (samples == null)
                return;

            for (var i = 0; i < samples.Length; i++)
            {
                _buffer[_writePosition] = samples[i];
                _writePosition = (_writePosition + 1) % _buffer.Length;

                if (_writePosition == 0)
                    _isFull = true;
            }
        }

        public float[] GetAllSamples()
        {
            var sampleCount = _isFull ? _buffer.Length : _writePosition;
            var result = new float[sampleCount];

            if (_isFull)
            {
                // Buffer has wrapped, read from write position to end, then beginning to write position
                var firstPartSize = _buffer.Length - _writePosition;
                Array.Copy(_buffer, _writePosition, result, 0, firstPartSize);
                Array.Copy(_buffer, 0, result, firstPartSize, _writePosition);
            }
            else
            {
                // Buffer hasn't wrapped yet
                Array.Copy(_buffer, 0, result, 0, _writePosition);
            }

            return result;
        }

        public void Clear()
        {
            _writePosition = 0;
            _isFull = false;
            Array.Clear(_buffer, 0, _buffer.Length);
        }
    }
}