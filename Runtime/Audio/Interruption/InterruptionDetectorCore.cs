using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tsc.AIBridge.Audio.Interruption
{
    /// <summary>
    /// Core interruption detection logic - regular C# class for better performance.
    /// Detects real interruptions vs back-channeling using persistence-based classification.
    /// This is the non-MonoBehaviour version of InterruptionDetector.
    /// </summary>
    public class InterruptionDetectorCore
    {
        #region Configuration

        // Detection Settings
        private float _interruptThreshold;
        private readonly float _cooldownPeriod;
        
        // Urgency Settings
        private readonly float _emergencyThreshold;
        private readonly UrgencyLevel _currentUrgencyLevel;
        
        // User Adaptation
        private readonly bool _enableUserAdaptation;
        
        // Near-End Override Settings (Defaults)
        //private bool defaultUseTimeBasedNearEnd;
        private readonly float _defaultTimeBasedThresholdSeconds;
        
        // Debug Settings
        private readonly bool _enableVerboseLogging;

        #endregion

        #region State

        
        // Core state
        private bool _isInterruptable = true;
        private bool _isInterrupting;
        
        /// <summary>
        /// Public property to check if interruption is currently being processed
        /// </summary>
        public bool IsInterrupting => _isInterrupting;
        private float _overlapTimer;
        private float _lastInterruptTime = -999f; // Initialize to very negative to prevent initial cooldown
        
        private bool _isInCooldown;
        
        // NPC pause tolerance
        private float _npcSilenceTimer;
        private const float NPCPauseTolerance = 0.3f; // Allow 300ms pauses without reset
        
        // Pause detection state
        private bool _wasNPCSpeaking;
        
        // NPC response state
        private string _currentNpcResponse = string.Empty;
        private float _estimatedResponseDuration;
        private float _responseElapsedTime;
        private float _responseProgress;
        private bool _npcNearEnd;
        private bool _responseComplete; // Track if AudioStreamEnd received
        
        // Current turn overrides
        private bool _useTimeBasedNearEnd;
        private float _timeBasedThresholdSeconds;
        
        // Session management
        private readonly Dictionary<string, SessionInfo> _activeSessions = new();
        private string _currentSessionId = string.Empty;
        
        // User profiling
        private readonly UserInterruptionProfile _userProfile = new();
        private int _consecutiveInterruptions;
        
        // Performance tracking
        private int _totalChecks;
        private float _totalProcessingTime;
        private float _lastCleanupTime;

        #endregion

        #region Events

        /// <summary>
        /// Fired when a valid interruption is detected
        /// </summary>
        public event Action OnInterruptionDetected;

        /// <summary>
        /// Fired when a potential interruption is rejected
        /// </summary>
        public event Action<string> OnInterruptionRejected;

        #endregion

        #region Constructor

        /// <summary>
        /// Create a new InterruptionDetectorCore with configuration
        /// </summary>
        public InterruptionDetectorCore(
            float interruptThreshold = 1.5f,
            float cooldownPeriod = 2.0f,
            float emergencyThreshold = 1.0f,
            UrgencyLevel urgencyLevel = UrgencyLevel.Normal,
            bool enableUserAdaptation = true,
            float defaultNearEndThreshold = 0.85f, // DEPRECATED - kept for compatibility
            bool defaultUseTimeBasedNearEnd = false,
            float defaultTimeBasedThresholdSeconds = 2.0f,
            bool enableDebugLogging = false)
        {
            _interruptThreshold = interruptThreshold;
            _cooldownPeriod = cooldownPeriod;
            _emergencyThreshold = emergencyThreshold;
            _currentUrgencyLevel = urgencyLevel;
            _enableUserAdaptation = enableUserAdaptation;
            // defaultNearEndThreshold is deprecated - percentage-based near-end removed
            _defaultTimeBasedThresholdSeconds = defaultTimeBasedThresholdSeconds;
            _enableVerboseLogging = enableDebugLogging;
            
            // Initialize defaults
            // Percentage-based near-end no longer used
            _useTimeBasedNearEnd = defaultUseTimeBasedNearEnd;
            _timeBasedThresholdSeconds = defaultTimeBasedThresholdSeconds;
            
            if (enableDebugLogging)
            {
                Debug.Log($"[Interruption] Detector initialized - Threshold: {interruptThreshold}s, " +
                         $"Cooldown: {cooldownPeriod}s, UserAdaptation: {enableUserAdaptation}");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Check for interruption based on overlapping speech
        /// </summary>
        public bool CheckForInterruption(bool playerSpeaking, bool npcSpeaking, float deltaTime)
        {
            var startTime = Time.realtimeSinceStartup;
            _totalChecks++;

            try
            {
                // Special case: if deltaTime is large enough to instantly trigger, skip cooldown
                // This is for test scenarios and cases where we know there's been a long overlap
                var instantTrigger = deltaTime >= _interruptThreshold && playerSpeaking && npcSpeaking;
                
                // Handle cooldown period (skip for instant trigger)
                if (!instantTrigger && _isInCooldown && Time.time - _lastInterruptTime < _cooldownPeriod)
                {
                    return false;
                }
                else if (_isInCooldown)
                {
                    _isInCooldown = false;
                    if (_enableVerboseLogging)
                    {
                        Debug.Log("[Interruption] Cooldown period ended");
                    }
                }

                // Update NPC speaking state and silence timer FIRST
                // This ensures npcSilenceTimer is accurate for pause-fill detection
                if (npcSpeaking)
                {
                    _wasNPCSpeaking = true;
                    _npcSilenceTimer = 0f; // Reset timer when NPC is speaking
                }
                else if (_wasNPCSpeaking)
                {
                    // Track how long NPC has been silent (must be done BEFORE pause-fill check)
                    _npcSilenceTimer += deltaTime;
                }
                
                // CRITICAL: Detect pause-fill scenario with tolerance (AFTER updating npcSilenceTimer)
                // If NPC paused (VAD detected silence) and player starts talking,
                // this COULD be an interruption, but we need to respect pause tolerance
                // Only trigger immediate interruption for LONG pauses (> 300ms)
                if (!npcSpeaking && playerSpeaking && !_responseComplete && _wasNPCSpeaking)
                {
                    // Only trigger immediate interruption if pause is LONG (> 300ms)
                    // Short pauses should be handled by normal interruption logic
                    if (_npcSilenceTimer > NPCPauseTolerance)
                    {
                        // Track for debugging
                        Debug.Log($"[Interruption] 🔇 PAUSE-FILL INTERRUPTION - User fills NPC pause (silence: {_npcSilenceTimer:F2}s)");
                        Debug.Log($"[Interruption] Type: Natural pause interruption");
                        
                        // Trigger immediate interruption (user filling NPC pause after long silence)
                        _isInterrupting = true;
                        _lastInterruptTime = Time.time;
                        _isInCooldown = true;
                        
                        OnInterruptionDetected?.Invoke();
                        
                        // Reset state (but keep isInterrupting true!)
                        _overlapTimer = 0f; // Reset timer but NOT isInterrupting flag
                        _wasNPCSpeaking = false;
                        
                        return true;
                    }
                    // For short pauses, let the normal interruption logic handle it
                }
                
                // If response IS complete, user can speak freely
                if (_responseComplete && playerSpeaking)
                {
                    Debug.Log("[Interruption] User speaking after NPC complete response - normal conversation flow");
                    // This is normal conversation flow, not an interruption
                    return false;
                }

                // Reset if neither party is speaking
                // BUT: Add a small grace period to handle VAD flicker
                if (!playerSpeaking && !npcSpeaking)
                {
                    // Only reset if we've been silent for a bit
                    if (_overlapTimer > 0 && _overlapTimer < 0.2f)
                    {
                        // Keep the timer going for a brief moment (VAD flicker tolerance)
                        Debug.Log($"[Interruption] Brief silence detected but keeping timer ({_overlapTimer:F2}s)");
                    }
                    else
                    {
                        ResetOverlapState();
                    }
                    return false;
                }

                // Check for various rejection scenarios
                var rejectionReason = GetRejectionReason();
                if (!string.IsNullOrEmpty(rejectionReason))
                {
                    // DON'T reset overlap state for network delay - we want to keep counting!
                    // Only reset for real rejection reasons like "not interruptable"
                    var shouldResetTimer = !rejectionReason.Contains("Network delay");
                    
                    if (shouldResetTimer)
                    {
                        ResetOverlapState();
                    }
                    
                    OnInterruptionRejected?.Invoke(rejectionReason);
                    
                    // Only log when timer is reset (important rejections)
                    // Don't spam logs for rejections that don't reset timer
                    if (_enableVerboseLogging && shouldResetTimer)
                    {
                        Debug.Log($"[Interruption] Rejected and timer reset: {rejectionReason}");
                    }
                    
                    return false;
                }

                // Core interruption detection logic
                if (playerSpeaking && npcSpeaking)
                {
                    // Both speaking - reset silence timer
                    _npcSilenceTimer = 0f;
                    
                    // For instant trigger (deltaTime >= threshold), immediately trigger
                    if (instantTrigger)
                    {
                        _overlapTimer = deltaTime; // Set timer to deltaTime
                        Debug.Log($"[Interruption] Instant trigger: {deltaTime}s >= {_interruptThreshold}s threshold");
                        return TriggerInterruption();
                    }
                    
                    return ProcessOverlappingSpeech(deltaTime);
                }
                else if (playerSpeaking)
                {
                    // Player speaking but NPC silent - might be a brief pause
                    
                    // If this is the first time player speaks alone (not a pause), start fresh
                    if (!_wasNPCSpeaking)
                    {
                        // Player starting to speak while NPC not speaking - start counting
                        _overlapTimer += deltaTime;
                        return false;
                    }
                    
                    // If silence is brief, continue counting as potential interruption
                    // Even if overlapTimer was 0, we start counting from when player speaks
                    if (_npcSilenceTimer < NPCPauseTolerance)
                    {
                        // Continue or start counting overlap during brief NPC pause
                        _overlapTimer += deltaTime;
                        
                        if (_enableVerboseLogging && Time.frameCount % 180 == 0)
                        {
                            Debug.Log($"[Interruption] Counting during NPC pause - Timer: {_overlapTimer:F2}s, Pause: {_npcSilenceTimer:F2}s");
                        }
                        
                        // Don't trigger during pause - wait for NPC to resume
                        // The timer keeps counting but we don't interrupt during silence
                        return false;
                    }
                    else
                    {
                        // Silence too long - reset
                        ResetOverlapState();
                        return false;
                    }
                }
                else
                {
                    // Neither speaking or only NPC speaking - reset
                    ResetOverlapState();
                    _npcSilenceTimer = 0f;
                    return false;
                }
            }
            finally
            {
                // Track performance
                var processingTime = Time.realtimeSinceStartup - startTime;
                _totalProcessingTime += processingTime;
            }
        }


        /// <summary>
        /// Set whether interruptions are allowed
        /// </summary>
        public void SetInterruptable(bool interruptable)
        {
            _isInterruptable = interruptable;
            
            if (_enableVerboseLogging)
            {
                Debug.Log($"[Interruption] Interruptable set to: {interruptable}");
            }
        }

        /// <summary>
        /// Set NPC near end of response
        /// </summary>
        public void SetNpcNearEnd(bool nearEnd)
        {
            _npcNearEnd = nearEnd;
        }

        /// <summary>
        /// Set NPC response progress
        /// </summary>
        public void SetNpcResponseProgress(float progress)
        {
            _responseProgress = Mathf.Clamp01(progress);
            
            // Auto-detect near end based on progress
            // Near-end is now purely time-based, calculated in CheckForInterruption
            // Remove percentage-based near-end detection
        }

        /// <summary>
        /// Set the NPC response for tracking partial heard text
        /// </summary>
        public void SetNpcResponse(string response, float estimatedDurationMs, 
            float? customNearEndThreshold = null, float? customTimeThresholdSeconds = null)
        {
            _currentNpcResponse = response ?? string.Empty;
            _estimatedResponseDuration = estimatedDurationMs / 1000f; // Convert to seconds
            _responseElapsedTime = 0f;
            _responseProgress = 0f;
            // _npcNearEnd no longer used - time-based detection only
            _responseComplete = false; // Reset on new response
            
            // Apply per-turn overrides if provided
            // customNearEndThreshold is deprecated - percentage-based near-end removed

            if (customTimeThresholdSeconds.HasValue)
            {
                _timeBasedThresholdSeconds = customTimeThresholdSeconds.Value;
            }
            else
            {
                _timeBasedThresholdSeconds = _defaultTimeBasedThresholdSeconds;
            }

            if (_enableVerboseLogging)
            {
                Debug.Log($"[Interruption] NPC response set - Length: {response?.Length ?? 0} chars, " +
                         $"Duration: {estimatedDurationMs}ms");
            }
        }

        /// <summary>
        /// Mark the response as complete (AudioStreamEnd received)
        /// </summary>
        public void SetResponseComplete()
        {
            _responseComplete = true;
            
            if (_enableVerboseLogging)
            {
                Debug.Log("[Interruption] NPC response marked as complete (AudioStreamEnd received)");
            }
        }
        
        /// <summary>
        /// Check if the current response is complete
        /// </summary>
        public bool IsResponseComplete => _responseComplete;
        
        /// <summary>
        /// Set response elapsed time for time-based near-end detection
        /// </summary>
        public void SetResponseElapsedTime(float elapsedSeconds)
        {
            _responseElapsedTime = elapsedSeconds;
            
            // Auto-detect near end based on time if enabled
            if (_useTimeBasedNearEnd && _estimatedResponseDuration > 0)
            {
                var timeRemaining = _estimatedResponseDuration - _responseElapsedTime;
                var wasNearEnd = _npcNearEnd;
                _npcNearEnd = timeRemaining <= _timeBasedThresholdSeconds;
                
                if (_enableVerboseLogging && !wasNearEnd && _npcNearEnd)
                {
                    Debug.Log($"[Interruption] NPC near end detected - {timeRemaining:F1}s remaining");
                }
            }
        }

        // SetNearEndThreshold removed - percentage-based near-end no longer used

        /// <summary>
        /// Set turn-specific thresholds
        /// </summary>
        /// <param name="persistenceTime">Time in seconds of overlap required for interruption (from PersonaSO)</param>
        /// <param name="useTimeBased">Whether to use time-based near-end detection</param>
        public void SetTurnSpecificThresholds(float persistenceTime, bool useTimeBased)
        {
            _interruptThreshold = persistenceTime; // Use PersonaSO's persistenceTime as interrupt threshold
            _timeBasedThresholdSeconds = 1.0f; // Default time-based near-end threshold
            _useTimeBasedNearEnd = useTimeBased;
            // Keep the default _nearEndThreshold from constructor

            if (_enableVerboseLogging)
            {
                Debug.Log($"[Interruption] Turn thresholds set - " +
                         $"Persistence: {persistenceTime}s, UseTimeBased: {useTimeBased}");
            }
        }

        /// <summary>
        /// Check if PTT should be allowed to start
        /// </summary>
        public bool ShouldAllowPTTStart()
        {
            // Always allow if interruptions are enabled
            if (_isInterruptable)
            {
                return true;
            }
            
            // When interruptable=false, check if we're near the end
            var nearEndOverride = IsNearEnd();
            
            if (_enableVerboseLogging && nearEndOverride)
            {
                Debug.Log($"[Interruption] PTT allowed due to near-end override (progress: {_responseProgress:P0})");
            }
            
            return nearEndOverride;
        }

        /// <summary>
        /// Check if NPC is near the end of response (time-based only)
        /// </summary>
        public bool IsNearEnd()
        {
            // Check time-based threshold if enabled
            if (_useTimeBasedNearEnd && _estimatedResponseDuration > 0)
            {
                var timeRemaining = _estimatedResponseDuration - _responseElapsedTime;
                if (timeRemaining <= _timeBasedThresholdSeconds)
                {
                    if (_enableVerboseLogging)
                    {
                        Debug.Log($"[Interruption] NPC near end detected - {timeRemaining:F1}s remaining");
                    }
                    return true;
                }
            }

            // Also check if response is complete (AudioStreamEnd received)
            if (_responseComplete)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Start a new session
        /// </summary>
        public void StartSession(string sessionId)
        {
            _currentSessionId = sessionId;
            
            if (!_activeSessions.ContainsKey(sessionId))
            {
                _activeSessions[sessionId] = new SessionInfo
                {
                    StartTime = Time.time,
                    InterruptionCount = 0
                };
            }
            
            ResetOverlapState();
            
            if (_enableVerboseLogging)
            {
                Debug.Log($"[Interruption] Session started: {sessionId}");
            }
        }

        /// <summary>
        /// End current session
        /// </summary>
        public void EndSession(string sessionId)
        {
            if (_activeSessions.ContainsKey(sessionId))
            {
                var session = _activeSessions[sessionId];
                
                if (_enableVerboseLogging)
                {
                    var duration = Time.time - session.StartTime;
                    Debug.Log($"[Interruption] Session ended: {sessionId} - " +
                             $"Duration: {duration:F1}s, Interruptions: {session.InterruptionCount}");
                }
                
                _activeSessions.Remove(sessionId);
            }
            
            if (_currentSessionId == sessionId)
            {
                _currentSessionId = string.Empty;
                ResetOverlapState();
            }
        }

        /// <summary>
        /// Get last interruption metadata
        /// </summary>
        public InterruptionMetadata GetLastInterruptionMetadata()
        {
            var partialResponse = GetPartialResponseHeard();
            
            return new InterruptionMetadata
            {
                OverlapDuration = _overlapTimer,
                InterruptionTimestamp = Time.time,
                WasPlayerSpeaking = true,
                WasNpcSpeaking = true,
                PartialResponseHeard = partialResponse,
                FullResponseGenerated = _currentNpcResponse,
                ResponseCompletionPercentage = _responseProgress,
                RequestId = _currentSessionId,
                UrgencyLevel = _currentUrgencyLevel
            };
        }

        /// <summary>
        /// Clean up old sessions (should be called periodically)
        /// </summary>
        public void CleanupOldSessions()
        {
            var currentTime = Time.time;
            
            // Only cleanup every 5 seconds
            if (currentTime - _lastCleanupTime < 5f)
                return;
            
            _lastCleanupTime = currentTime;
            
            var sessionsToRemove = new List<string>();

            foreach (var kvp in _activeSessions)
            {
                if (currentTime - kvp.Value.StartTime > 300f) // 5 minutes old
                {
                    sessionsToRemove.Add(kvp.Key);
                }
            }

            foreach (var sessionId in sessionsToRemove)
            {
                _activeSessions.Remove(sessionId);
            }
            
            if (_enableVerboseLogging && sessionsToRemove.Count > 0)
            {
                Debug.Log($"[Interruption] Cleaned up {sessionsToRemove.Count} old sessions");
            }
        }

        /// <summary>
        /// Get current overlap duration for debugging
        /// </summary>
        public float GetOverlapDuration()
        {
            return _overlapTimer;
        }
        
        /// <summary>
        /// Get performance statistics
        /// </summary>
        public string GetStatistics()
        {
            var avgProcessingTime = _totalChecks > 0 ? (_totalProcessingTime / _totalChecks) * 1000f : 0f;
            var profileStats = _enableUserAdaptation ? 
                $", FalseRate: {_userProfile.FalseInterruptionRate:P0}" : "";
            
            return $"Checks: {_totalChecks}, AvgTime: {avgProcessingTime:F3}ms{profileStats}";
        }

        /// <summary>
        /// Dispose and cleanup
        /// </summary>
        public void Dispose()
        {
            if (_enableVerboseLogging)
            {
                Debug.Log($"[Interruption] Detector disposed - {GetStatistics()}");
            }
        }

        #endregion

        #region Private Methods

        private void ResetOverlapState()
        {
            _overlapTimer = 0f;
            _isInterrupting = false;
            _npcSilenceTimer = 0f;
        }
        
        /// <summary>
        /// Resets the interruption state after it has been handled.
        /// Call this when the interrupted response has been successfully handled.
        /// </summary>
        public void ResetInterruptionState()
        {
            ResetOverlapState();
            if (_enableVerboseLogging)
            {
                Debug.Log("[Interruption] Interruption state reset");
            }
        }

        private string GetRejectionReason()
        {
            // Reject if interruptions not allowed (unless near end)
            if (!_isInterruptable && !IsNearEnd())
            {
                return "Interruptions disabled for this NPC";
            }

            // Reject if too soon after last interruption (cooldown)
            // Only check cooldown if we've had at least one interruption
            if (_lastInterruptTime > 0 && Time.time - _lastInterruptTime < _cooldownPeriod)
            {
                return "Cooldown period active";
            }

            // Network delay compensation - don't reject, just keep counting
            // This allows the timer to build up past the network delay buffer
            // Comment out to let timer continue counting:
            // if (overlapTimer < networkDelayBuffer)
            // {
            //     return $"Network delay buffer ({networkDelayBuffer:F1}s)";
            // }

            // Don't reject for rapid PTT - let the timer build up
            // This was preventing interruption detection from working
            // var rapidPttThreshold = 0.2f;
            // if (overlapTimer < rapidPttThreshold)
            // {
            //     return "Rapid PTT press detected";
            // }

            // Audio feedback detection disabled - was preventing interruption
            // if (vadProcessor != null && overlapTimer < 0.5f)
            // {
            //     // Simple heuristic - could be improved with spectral analysis
            //     var threshold = vadProcessor.CurrentThreshold;
            //     if (threshold > 0.04f) // High threshold might indicate feedback
            //     {
            //         return "Possible audio feedback detected";
            //     }
            // }

            // Don't reject for simultaneous start either - let interruption happen
            // var simultaneousStart = overlapTimer < 0.3f && playerSpeaking && npcSpeaking;
            // if (simultaneousStart)
            // {
            //     return "Simultaneous start detected";
            // }

            // Reject if user has high false interruption rate and this is too short
            if (_enableUserAdaptation && _userProfile.FalseInterruptionRate > 0.5f)
            {
                var requiredDuration = GetAdaptiveThreshold();
                if (_overlapTimer < requiredDuration)
                {
                    return $"User profile requires longer confirmation ({requiredDuration:F1}s)";
                }
            }

            // Reject if too many consecutive interruptions
            if (_consecutiveInterruptions >= 3)
            {
                return "Too many consecutive interruptions";
            }

            return null; // No rejection
        }

        private bool ProcessOverlappingSpeech(float deltaTime)
        {
            _overlapTimer += deltaTime;

            var threshold = GetCurrentThreshold();
            
            // CRITICAL FIX: Prevent excessive logging that can cause Unity to appear frozen
            // Only log once per second (60 frames) AND limit to prevent log spam
            // Also add a safety check to prevent infinite logging loops
            if (!_isInterrupting && _overlapTimer > threshold * 0.8f && Time.frameCount % 180 == 0)
            {
                // Additional safety: Don't log if timer is stuck (not incrementing properly)
                // This can happen with very small deltaTime values or precision issues
                if (_enableVerboseLogging && _overlapTimer < threshold * 10f) // Sanity check - timer shouldn't be 10x threshold
                {
                    Debug.Log($"[Interruption] Overlap progress: {_overlapTimer:F2}s / {threshold:F2}s (need {threshold - _overlapTimer:F2}s more)");
                }
            }
            
            if (!_isInterrupting && _overlapTimer > threshold)
            {
                // Threshold reached - determine type before triggering
                return TriggerInterruption();
            }

            return false;
        }

        private float GetCurrentThreshold()
        {
            var baseThreshold = _currentUrgencyLevel == UrgencyLevel.Emergency 
                ? _emergencyThreshold 
                : _interruptThreshold;

            // Apply user adaptation if enabled
            if (_enableUserAdaptation)
            {
                return GetAdaptiveThreshold();
            }

            return baseThreshold;
        }

        private float GetAdaptiveThreshold()
        {
            var baseThreshold = _currentUrgencyLevel == UrgencyLevel.Emergency 
                ? _emergencyThreshold 
                : _interruptThreshold;

            // Increase threshold for users with high false interrupt rates
            if (_userProfile.FalseInterruptionRate > 0.3f)
            {
                var multiplier = 1f + (_userProfile.FalseInterruptionRate * 0.5f);
                return baseThreshold * multiplier;
            }

            return baseThreshold;
        }

        private string DetermineInterruptionType()
        {
            // Check if this is a near-end interruption (NPC has been speaking for a while)
            // Time-based near-end detection
            if (IsNearEnd())
            {
                return $"🔚 NEAR-END INTERRUPTION - User taking turn after {_responseElapsedTime:F1}s (overlap: {_overlapTimer:F2}s)";
            }
            
            // Otherwise it's a mid-speech interruption (breaking into conversation)
            // interruptThreshold comes from PersonaSO.persistenceTime (e.g., 1.5s)
            return $"🗣️ MID-SPEECH INTERRUPTION - User breaking into NPC speech (persistence: {_overlapTimer:F2}s > PersonaSO threshold: {_interruptThreshold:F2}s)";
        }
        
        private bool TriggerInterruption()
        {
            // Determine interruption type for logging
            var interruptionType = DetermineInterruptionType();
            
            _isInterrupting = true;
            _lastInterruptTime = Time.time;
            _isInCooldown = true;
            _consecutiveInterruptions++;
            
            // Log the interruption type
            Debug.Log($"[Interruption] {interruptionType}");
            
            // Reset overlap timer to prevent continuous logging
            _overlapTimer = 0f;

            // Update session info
            if (_activeSessions.ContainsKey(_currentSessionId))
            {
                _activeSessions[_currentSessionId].InterruptionCount++;
            }

            // Update user profile
            if (_enableUserAdaptation)
            {
                _userProfile.TotalInterruptions++;
                _userProfile.LastInterruptionTime = Time.time;
            }

            if (_enableVerboseLogging)
            {
                Debug.Log($"[Interruption] TRIGGERED - Overlap: {_overlapTimer:F2}s, " +
                         $"Progress: {_responseProgress:P0}, Consecutive: {_consecutiveInterruptions}");
            }

            OnInterruptionDetected?.Invoke();
            return true;
        }

        private string GetPartialResponseHeard()
        {
            if (string.IsNullOrEmpty(_currentNpcResponse))
                return string.Empty;

            // Calculate approximate position based on response progress
            var approximatePosition = (int)(_currentNpcResponse.Length * _responseProgress);
            
            // Find the last complete sentence up to that position
            var lastPeriod = _currentNpcResponse.LastIndexOf('.', approximatePosition);
            if (lastPeriod > 0)
            {
                return _currentNpcResponse.Substring(0, lastPeriod + 1);
            }
            
            // If no complete sentence, return first half
            return _currentNpcResponse.Substring(0, approximatePosition);
        }

        //private void ResetToDefaultThresholds()
        //{
        //    nearEndThreshold = defaultNearEndThreshold;
        //    useTimeBasedNearEnd = defaultUseTimeBasedNearEnd;
        //    timeBasedThresholdSeconds = defaultTimeBasedThresholdSeconds;
        //}

        #endregion

        #region Helper Classes

        private class SessionInfo
        {
            public float StartTime { get; set; }
            public int InterruptionCount { get; set; }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Urgency level for adaptive thresholds
    /// </summary>
    public enum UrgencyLevel
    {
        Normal,
        Emergency
    }

    /// <summary>
    /// Metadata about an interruption event
    /// </summary>
    public class InterruptionMetadata
    {
        public float OverlapDuration { get; set; }
        public float InterruptionTimestamp { get; set; }
        public bool WasPlayerSpeaking { get; set; }
        public bool WasNpcSpeaking { get; set; }
        public string PartialResponseHeard { get; set; }
        public string FullResponseGenerated { get; set; }
        public float ResponseCompletionPercentage { get; set; }
        public string RequestId { get; set; }
        public UrgencyLevel UrgencyLevel { get; set; }
    }

    /// <summary>
    /// User interruption profile for adaptation
    /// </summary>
    public class UserInterruptionProfile
    {
        public int TotalInterruptions { get; set; }
        public int FalseInterruptions { get; set; }
        public float LastInterruptionTime { get; set; }
        public float AverageInterruptionDuration { get; set; }
        
        public float FalseInterruptionRate => 
            TotalInterruptions > 0 ? (float)FalseInterruptions / TotalInterruptions : 0f;
    }

    #endregion
}