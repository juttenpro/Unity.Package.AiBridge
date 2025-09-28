using System;
using System.Collections;
using UnityEngine;
using Tsc.AIBridge.Audio.VAD;

namespace Tsc.AIBridge.Input
{
    /// <summary>
    /// Internal controller that manages all recording logic.
    /// Handles PTT, Voice Activation, and Smart Offset internally.
    /// Provides a simple interface: user is speaking or not.
    /// </summary>
    internal class RecordingController
    {
        #region Events

        /// <summary>
        /// Fired when user starts speaking (regardless of PTT or VAD)
        /// </summary>
#pragma warning disable 0067 // Event is never used - Available for external scripts
        public event Action OnUserStartedSpeaking;
#pragma warning restore 0067

        /// <summary>
        /// Fired when user stops speaking (after all offsets/delays)
        /// </summary>
#pragma warning disable 0067 // Event is never used - Available for external scripts
        public event Action OnUserStoppedSpeaking;
#pragma warning restore 0067

        #endregion

        #region Configuration

        private readonly bool _useVoiceActivation;
        private readonly bool _useSmartMicOffset;
        private readonly float _stopRecordingDelay;
        private readonly float _smartOffsetMaxDuration;
        private readonly float _silenceThreshold;
        private readonly float _voiceOnsetTime;
        private readonly float _voiceSilenceTime;
        private readonly bool _enableVerboseLogging;

        #endregion

        #region State

        private bool _isPttPressed;
        private bool _isUserSpeaking;
        private bool _isVoiceActivated;
        private float _silenceTimer;
        private float _voiceOnsetTimer;
        private Coroutine _stopDelayCoroutine;
        private MonoBehaviour _coroutineRunner;
        private VADManager _vadManager;

        #endregion

        #region Properties

        /// <summary>
        /// Is the user currently speaking (from external perspective)
        /// </summary>
        public bool IsUserSpeaking => _isUserSpeaking;

        /// <summary>
        /// Is PTT currently pressed
        /// </summary>
        public bool IsPttPressed => _isPttPressed;

        #endregion

        #region Constructor

        public RecordingController(
            MonoBehaviour coroutineRunner,
            VADManager vadManager,
            bool useVoiceActivation,
            bool useSmartMicOffset,
            float stopRecordingDelay,
            float smartOffsetMaxDuration,
            float silenceThreshold,
            float voiceOnsetTime,
            float voiceSilenceTime,
            bool enableVerboseLogging)
        {
            _coroutineRunner = coroutineRunner;
            _vadManager = vadManager;
            _useVoiceActivation = useVoiceActivation;
            _useSmartMicOffset = useSmartMicOffset;
            _stopRecordingDelay = stopRecordingDelay;
            _smartOffsetMaxDuration = smartOffsetMaxDuration;
            _silenceThreshold = silenceThreshold;
            _voiceOnsetTime = voiceOnsetTime;
            _voiceSilenceTime = voiceSilenceTime;
            _enableVerboseLogging = enableVerboseLogging;
        }

        #endregion

        #region PTT Control

        /// <summary>
        /// Called when PTT is pressed
        /// </summary>
        public void OnPttPressed()
        {
            _isPttPressed = true;

            // Cancel any pending stop
            CancelStopDelay();

            // Reset silence timer
            _silenceTimer = 0;

            // If voice activation triggered this, mark it
            if (_isVoiceActivated)
            {
                // Voice activation simulated a PTT press
            }
            else
            {
                // Real PTT press - start speaking immediately
                StartUserSpeaking("PTT pressed");
            }
        }

        /// <summary>
        /// Called when PTT is released
        /// </summary>
        public void OnPttReleased()
        {
            _isPttPressed = false;

            // Reset silence timer for smart offset
            _silenceTimer = 0;

            // Start appropriate delay/offset
            if (_useVoiceActivation && _isVoiceActivated)
            {
                // Let voice activation handle it
                return;
            }

            if (_useSmartMicOffset && _vadManager != null)
            {
                // Use smart offset based on VAD
                _stopDelayCoroutine = _coroutineRunner.StartCoroutine(SmartOffsetCoroutine());
            }
            else
            {
                // Use fixed delay
                _stopDelayCoroutine = _coroutineRunner.StartCoroutine(FixedDelayCoroutine());
            }
        }

        #endregion

        #region Voice Activation Control

        /// <summary>
        /// Process audio frame for voice activation
        /// </summary>
        public void ProcessAudioForVoiceActivation(float[] samples)
        {
            if (!_useVoiceActivation)
                return;

            bool vadDetectsSpeech = _vadManager?.IsUserSpeaking ?? false;

            if (!_isUserSpeaking)
            {
                // Check for voice onset
                if (vadDetectsSpeech)
                {
                    _voiceOnsetTimer += samples.Length / 48000f; // Assuming 48kHz

                    if (_voiceOnsetTimer >= _voiceOnsetTime)
                    {
                        // Voice activation triggered
                        _isVoiceActivated = true;
                        StartUserSpeaking("Voice activation triggered");
                    }
                }
                else
                {
                    _voiceOnsetTimer = 0;
                }
            }
            else if (_isVoiceActivated && !_isPttPressed)
            {
                // Check for voice offset (only if not PTT)
                if (!vadDetectsSpeech)
                {
                    _silenceTimer += samples.Length / 48000f;

                    if (_silenceTimer >= _voiceSilenceTime)
                    {
                        // Voice deactivated
                        _isVoiceActivated = false;
                        StopUserSpeaking("Voice activation stopped");
                    }
                }
                else
                {
                    _silenceTimer = 0;
                }
            }
        }

        #endregion

        #region Recording State Control

        /// <summary>
        /// Process audio data during recording
        /// </summary>
        public void ProcessRecordingAudio(float[] samples, bool vadDetectsSpeech)
        {
            // Update silence timer for smart offset (only when NOT PTT pressed)
            if (_useSmartMicOffset && !_isPttPressed && _isUserSpeaking)
            {
                if (!vadDetectsSpeech)
                {
                    _silenceTimer += samples.Length / 48000f;
                }
                else
                {
                    _silenceTimer = 0;
                }
            }
        }

        private void StartUserSpeaking(string reason)
        {
            if (_isUserSpeaking)
                return;

            _isUserSpeaking = true;

            if (_enableVerboseLogging)
                Debug.Log($"[RecordingController] User started speaking: {reason}");

            OnUserStartedSpeaking?.Invoke();
        }

        private void StopUserSpeaking(string reason)
        {
            // CRITICAL: Never stop if PTT is pressed
            if (_isPttPressed)
            {
                if (_enableVerboseLogging)
                    Debug.LogWarning("[RecordingController] Attempted to stop while PTT pressed - ignoring!");
                return;
            }

            if (!_isUserSpeaking)
                return;

            _isUserSpeaking = false;
            _voiceOnsetTimer = 0;
            _silenceTimer = 0;

            if (_enableVerboseLogging)
                Debug.Log($"[RecordingController] User stopped speaking: {reason}");

            OnUserStoppedSpeaking?.Invoke();
        }

        private void CancelStopDelay()
        {
            if (_stopDelayCoroutine != null)
            {
                _coroutineRunner.StopCoroutine(_stopDelayCoroutine);
                _stopDelayCoroutine = null;
            }
        }

        #endregion

        #region Coroutines

        private IEnumerator SmartOffsetCoroutine()
        {
            float elapsedTime = 0;

            while (elapsedTime < _smartOffsetMaxDuration)
            {
                // Check if PTT pressed again
                if (_isPttPressed)
                {
                    yield break;
                }

                // Check if silence threshold reached
                if (_silenceTimer >= _silenceThreshold)
                {
                    StopUserSpeaking($"Smart offset: {_silenceTimer:F2}s silence");
                    yield break;
                }

                yield return new WaitForSeconds(0.02f);
                elapsedTime += 0.02f;
            }

            StopUserSpeaking($"Smart offset: max duration {_smartOffsetMaxDuration}s");
        }

        private IEnumerator FixedDelayCoroutine()
        {
            yield return new WaitForSeconds(_stopRecordingDelay);

            // Check if PTT pressed again
            if (_isPttPressed)
            {
                yield break;
            }

            StopUserSpeaking($"Fixed delay: {_stopRecordingDelay}s");
        }

        #endregion

        #region Cleanup

        public void Cleanup()
        {
            CancelStopDelay();
            _isUserSpeaking = false;
            _isPttPressed = false;
            _isVoiceActivated = false;
        }

        #endregion
    }
}