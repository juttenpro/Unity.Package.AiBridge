using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Tsc.AIBridge.Data;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Tracks and reports comprehensive latency metrics for AI conversation pipeline.
    /// Measures individual component latencies (STT, LLM, TTS) and overall perceived latency.
    /// Implements deferred reporting to ensure accurate timing data is captured.
    /// Key metrics tracked:
    /// - PTT duration (how long user held the button)
    /// - STT latency (speech recognition time)
    /// - LLM latency (AI response generation time)
    /// - Perceived latency (user stops speaking → AI starts speaking)
    /// </summary>
    public class LatencyTracker
    {
        private readonly Stopwatch _stopwatch = new();
        private readonly List<long> _latencyHistory = new();
        private readonly string _personaName;
        
        // Current measurement
        private long _sttLatency;
        private long _llmLatency;
        private long _pttDuration; // Time user held PTT button
        private readonly Stopwatch _pttStopwatch = new();
        private readonly Stopwatch _sttWaitStopwatch = new(); // Measures time from PTT release to STT complete
        private long _ttsLatency; // TTS latency from buffer hints
        private string _ttsLatencyLevel = ""; // TTS latency level from buffer hints
        private string _lastSttTranscript = ""; // Store last STT transcript for UI display
        private string _microphoneName = ""; // Store active microphone name
        
        // Deferred reporting
        private long _pendingPerceivedLatency; // Store latency when playback starts
        private bool _hasPlaybackStarted; // Track if playback has started
        private bool _hasLlmCompleted; // Track if LLM response received
        private bool _hasTtsTimingReceived; // Track if TTS timing has been received
        
        // Events
        public event Action<LatencyStats> OnLatencyStatsUpdated;
        
        // Static event for backwards compatibility
        public static event Action<LatencyStats> OnLatencyStatsUpdatedStatic;
        
        public LatencyTracker(string personaName)
        {
            _personaName = personaName;
        }

        /// <summary>
        /// Sets the active microphone name for display in latency logs
        /// </summary>
        /// <param name="microphoneName">The name of the active microphone device</param>
        public void SetMicrophoneName(string microphoneName)
        {
            _microphoneName = microphoneName;
        }
        
        /// <summary>
        /// Starts measuring latency when the user stops speaking (releases PTT).
        /// Records PTT duration and begins tracking time to first AI response.
        /// Resets all measurement states for a new conversation turn.
        /// </summary>
        public void StartMeasurement()
        {
            // Stop PTT measurement and store duration
            if (_pttStopwatch.IsRunning)
            {
                _pttDuration = _pttStopwatch.ElapsedMilliseconds;
                _pttStopwatch.Stop();
                //UnityEngine.Debug.Log($"[{_personaName}] PTT duration: {_pttDuration}ms");
            }
            else
            {
                _pttDuration = 0;
                //UnityEngine.Debug.LogWarning($"[{_personaName}] PTT stopwatch not running - PTT duration = 0");
            }
            
            // Start measuring how long we wait for STT result
            _sttWaitStopwatch.Restart();
            
            _stopwatch.Restart();
            _sttLatency = 0;
            // Don't reset LLM latency - keep the last known value for display
            // _llmLatency = 0;
            
            // Reset deferred reporting state
            _pendingPerceivedLatency = 0;
            _hasPlaybackStarted = false;
            _hasLlmCompleted = false;
            _hasTtsTimingReceived = false;
            
            //UnityEngine.Debug.Log($"[{_personaName}] Started latency measurement (perceived latency starts now)");
        }
        
        /// <summary>
        /// Marks the start of audio recording when PTT button is pressed.
        /// Starts tracking PTT duration and resets LLM timing from previous conversation.
        /// </summary>
        public void MarkRecordingStart()
        {
            // Start PTT duration measurement
            _pttStopwatch.Restart();
            
            // Reset LLM timing to prevent showing previous conversation's timing
            _llmLatency = 0;
            
            //UnityEngine.Debug.Log($"[{_personaName}] PTT button pressed - starting duration measurement");
        }
        
        /// <summary>
        /// Marks when the first audio chunk is sent to the server.
        /// Currently not used in the implementation.
        /// </summary>
        public void MarkFirstChunkSent()
        {
            // Not used in current implementation
        }
        
        /// <summary>
        /// Marks when transcription is received from STT service.
        /// Delegates to MarkSttComplete for processing.
        /// </summary>
        /// <param name="transcription">The transcribed text from speech recognition</param>
        public void MarkTranscriptionReceived(string transcription)
        {
            MarkSttComplete(transcription);
        }
        
        /// <summary>
        /// Marks when AI response is received from LLM service.
        /// Delegates to MarkLlmComplete for processing.
        /// </summary>
        /// <param name="response">The AI-generated response text</param>
        public void MarkAIResponseReceived(string response)
        {
            MarkLlmComplete(response);
        }
        
        /// <summary>
        /// Marks when TTS audio streaming begins from the backend.
        /// Logs the time from measurement start to audio stream start.
        /// </summary>
        public void MarkAudioStreamStart()
        {
            // Audio streaming has started (TTS response beginning)
            if (!_stopwatch.IsRunning) return;
            
            // Calculate time from measurement start to TTS stream start
            var streamStartLatency = _stopwatch.ElapsedMilliseconds;
            //UnityEngine.Debug.Log($"[{_personaName}] Audio stream started ({streamStartLatency}ms from start)");
        }
        
        /// <summary>
        /// Resets all latency measurements and clears history.
        /// Should be called when starting a new conversation session.
        /// </summary>
        public void Reset()
        {
            _stopwatch.Reset();
            _pttStopwatch.Reset();
            _sttLatency = 0;
            _llmLatency = 0;
            _pttDuration = 0;
            _ttsLatency = 0;
            _ttsLatencyLevel = "";
            _latencyHistory.Clear();
            _hasPlaybackStarted = false;
            _hasLlmCompleted = false;
            _hasTtsTimingReceived = false;
            _pendingPerceivedLatency = 0;
        }
        
        /// <summary>
        /// Marks STT completion and records speech-to-text latency.
        /// Tracks both Unity-side wait time and backend processing time.
        /// </summary>
        /// <param name="transcript">The transcribed text from speech recognition</param>
        /// <param name="timing">Optional backend timing metrics for detailed analysis</param>
        public void MarkSttComplete(string transcript, SttTiming timing = null)
        {
            if (!_stopwatch.IsRunning) return;

            // Store the transcript for UI display
            _lastSttTranscript = transcript;

            // Measure actual wait time from PTT release to STT complete
            long sttWaitTime = 0;
            if (_sttWaitStopwatch.IsRunning)
            {
                sttWaitTime = _sttWaitStopwatch.ElapsedMilliseconds;
                _sttWaitStopwatch.Stop();
            }

            // Store backend STT timing for informational purposes
            if (timing != null && timing.TotalProcessingMs > 0)
            {
                _sttLatency = (long)timing.TotalProcessingMs;
                UnityEngine.Debug.Log($"[{_personaName}] STT: \"{transcript}\" ({_sttLatency}ms from backend, actual wait: {sttWaitTime}ms)");
            }
            else
            {
                _sttLatency = 0; // No backend timing available
                UnityEngine.Debug.Log($"[{_personaName}] STT: \"{transcript}\" (no backend timing, actual wait: {sttWaitTime}ms)");
            }

            // Store the actual wait time as this is what affects perceived latency
            _sttLatency = sttWaitTime;

        }
        
        /// <summary>
        /// Mark LLM completion
        /// </summary>
        public void MarkLlmComplete(string response, LlmTiming timing = null)
        {
            // Always update LLM timing when we receive it, regardless of stopwatch state
            // This ensures we have the timing data available when playback starts
            if (timing != null)
            {
                // Prefer LlmWaitMs (user-perceived wait time) over FirstResponseMs
                if (timing.LlmWaitMs.HasValue && timing.LlmWaitMs.Value > 0)
                {
                    _llmLatency = (long)timing.LlmWaitMs.Value;
                    //UnityEngine.Debug.Log($"[{_personaName}] LLM: \"{response}\" (LLM wait: {_llmLatency}ms from backend, {timing.ChunkCount} chunks)");
                }
                else if (timing.FirstResponseMs > 0)
                {
                    // Fallback to FirstResponseMs if LlmWaitMs not available
                    _llmLatency = (long)timing.FirstResponseMs;
                    //UnityEngine.Debug.Log($"[{_personaName}] LLM: \"{response}\" ({_llmLatency}ms from backend, {timing.ChunkCount} chunks)");
                }
            }
            //else
            //{
            //    // Don't reset to 0 if we don't have new timing - keep the last known value
            //    UnityEngine.Debug.Log($"[{_personaName}] LLM: \"{response}\" (no backend timing - keeping previous: {_llmLatency}ms)");
            //}
            
            // Mark LLM as completed
            _hasLlmCompleted = true;
            //UnityEngine.Debug.Log($"[{_personaName}] ✓ LLM complete - latency: {_llmLatency}ms");

            // SIMPLE APPROACH: Try to report immediately (in case TTS timing already arrived)
            TryReportMetricsNow();
        }
        
        /// <summary>
        /// Mark TTS start (first audio chunk received)
        /// </summary>
        public void MarkTtsStart()
        {
            if (!_stopwatch.IsRunning) return;
            
            //UnityEngine.Debug.Log($"[{_personaName}] TTS started - expecting audio stream");
        }
        
        /// <summary>
        /// Mark TTS completion - timing tracking removed
        /// </summary>
        public void MarkTtsComplete()
        {
            // TTS timing tracking removed - not useful
            if (!_stopwatch.IsRunning) return;
            
            //UnityEngine.Debug.Log($"[{_personaName}] Audio stream started");
        }
        
        /// <summary>
        /// Update TTS latency from buffer hint message
        /// </summary>
        public void UpdateTtsLatency(double ttsLatencyMs, string latencyLevel)
        {
            _ttsLatency = (long)ttsLatencyMs;
            _ttsLatencyLevel = latencyLevel;
            _hasTtsTimingReceived = true;
            //UnityEngine.Debug.Log($"[{_personaName}] ✓ TTS timing received: {ttsLatencyMs}ms ({latencyLevel})");

            // SIMPLE APPROACH: Report metrics immediately when TTS timing arrives
            // This is more reliable than waiting for playback start (race conditions!)
            TryReportMetricsNow();
        }

        /// <summary>
        /// Try to report metrics immediately based on backend timing data.
        /// DISABLED: Backend timings (STT + LLM + TTS) are NOT accurate for perceived latency due to streaming/parallelism.
        /// Real perceived latency MUST be measured with stopwatch from PTT release to AudioSource.Play()
        /// This is now handled by MarkPlaybackStart() which provides accurate user-perceived latency.
        /// This method only logs component timings and triggers CheckAndReportIfComplete().
        /// </summary>
        public void TryReportMetricsNow()
        {
            // Check if we have the essential backend timings
            if (!_hasLlmCompleted || !_hasTtsTimingReceived)
            {
                //UnityEngine.Debug.Log($"[{_personaName}] Cannot report yet - LLM: {_hasLlmCompleted}, TTS: {_hasTtsTimingReceived}");
                return;
            }

            // Log component timings for debugging (but don't use as perceived latency)
            //UnityEngine.Debug.Log($"[{_personaName}] Backend timings received - STT:{_sttLatency}ms, LLM:{_llmLatency}ms, TTS:{_ttsLatency}ms (sum: {_sttLatency + _llmLatency + _ttsLatency}ms, but NOT perceived latency due to streaming)");

            // Check if playback has already started and report if we have everything
            CheckAndReportIfComplete();
        }
        
        /// <summary>
        /// Mark playback buffer ready (buffer filled, about to play)
        /// Does NOT stop the stopwatch - waits for actual audio output
        /// </summary>
        public void MarkPlaybackStart(float bufferDurationSeconds = 0)
        {
            // Latency tracking is optional - not a core functionality issue if stopwatch not running
            // This is normal for NPC-initiated conversations where there's no PTT button press
            if (!_stopwatch.IsRunning)
            {
                // Debug log only - this is informational, not an error
                // For NPC-initiated: could add separate LLM→TTS latency tracking in the future
                //UnityEngine.Debug.Log($"[{_personaName}] MarkPlaybackStart called without active latency measurement (normal for NPC-initiated conversations)");
                return;
            }

            // Mark playback as started but DON'T stop stopwatch yet
            // We'll stop it when first audio sample is actually output
            _hasPlaybackStarted = true;

            //UnityEngine.Debug.Log($"[{_personaName}] Playback buffer ready at {_stopwatch.ElapsedMilliseconds}ms - waiting for actual audio output");
        }

        /// <summary>
        /// Mark actual audio output (first sample played through speakers)
        /// This is the TRUE perceived latency - when user actually hears audio
        /// </summary>
        public void MarkActualAudioOutput()
        {
            if (!_stopwatch.IsRunning)
            {
                //UnityEngine.Debug.LogWarning($"[{_personaName}] MarkActualAudioOutput called but stopwatch not running");
                return;
            }

            // The ONLY measurement that matters: PTT release (or request start) → First audio sample output
            var totalPerceivedLatency = _stopwatch.ElapsedMilliseconds;

            _stopwatch.Stop();

            // Store for history
            _latencyHistory.Add(totalPerceivedLatency);

            _pendingPerceivedLatency = totalPerceivedLatency;

            //UnityEngine.Debug.Log($"[{_personaName}] ✅ Actual audio output at {totalPerceivedLatency}ms - true perceived latency");

            // Check if we can report immediately (if TTS timing already arrived)
            // Otherwise, wait for CheckAndReportIfComplete() when TTS timing arrives
            CheckAndReportIfComplete();
        }
        
        /// <summary>
        /// Check if we have all required timing data and report if complete
        /// </summary>
        private void CheckAndReportIfComplete()
        {
            // We need all three conditions to be true:
            // 1. Playback has started (we have perceived latency)
            // 2. LLM timing has been received
            // 3. TTS timing has been received
            if (_hasPlaybackStarted && _hasLlmCompleted && _hasTtsTimingReceived && _pendingPerceivedLatency > 0)
            {
                //UnityEngine.Debug.Log($"[{_personaName}] All timing data received, reporting complete latency stats");
                ReportLatencyStats(_pendingPerceivedLatency);
            }
            else
            {
                // Log what we're still waiting for (but not during Unity shutdown)
                if (UnityEngine.Application.isPlaying)
                {
                    var waiting = new List<string>();
                    if (!_hasPlaybackStarted) waiting.Add("playback start");
                    if (!_hasLlmCompleted) waiting.Add("LLM timing");
                    if (!_hasTtsTimingReceived) waiting.Add("TTS timing");

                    // Log what we're still waiting for to help debug (only once per request)
                    if (waiting.Count > 0 && _pendingPerceivedLatency > 0)
                    {
                        UnityEngine.Debug.LogWarning($"[{_personaName}] ⏳ Metrics blocked - waiting for: {string.Join(", ", waiting)} | " +
                            $"Playback: {_hasPlaybackStarted}, LLM: {_hasLlmCompleted}, TTS: {_hasTtsTimingReceived}, Latency: {_pendingPerceivedLatency}ms");
                    }
                }
            }
        }
        
        /// <summary>
        /// Report latency statistics with complete timing information
        /// </summary>
        private void ReportLatencyStats(long totalPerceivedLatency)
        {
            // Categorize perceived latency - adjusted thresholds for user experience
            var category = totalPerceivedLatency < 1500 ? "EXCELLENT" :
                          totalPerceivedLatency < 2000 ? "GOOD" :
                          totalPerceivedLatency < 2500 ? "ACCEPTABLE" : 
                          totalPerceivedLatency < 3000 ? "FAIR" : "POOR";
            
            // Build informational breakdown - these are backend timings, not part of perceived latency
            var pttTiming = _pttDuration > 0 ? $"PTT:{_pttDuration}ms, " : "";
            // Now _sttLatency is the actual wait time from PTT release to STT complete
            var ttsTiming = _ttsLatency > 0 ? $", TTS wait:{_ttsLatency}ms ({_ttsLatencyLevel})" : "";
            var infoBreakdown = $"(Backend info: {pttTiming}STT wait:{_sttLatency}ms, LLM wait:{_llmLatency}ms{ttsTiming})";

            // Include microphone info and STT transcript in the log
            var micInfo = !string.IsNullOrEmpty(_microphoneName) ? $" [Mic: {_microphoneName}]" : "";
            var sttInfo = !string.IsNullOrEmpty(_lastSttTranscript) ? $" | User said: \"{_lastSttTranscript}\"" : "";

            UnityEngine.Debug.Log($"[{_personaName}] {category} PERCEIVED LATENCY: {totalPerceivedLatency}ms {infoBreakdown}{micInfo}{sttInfo}");
            
            // Update statistics with perceived latency
            var stats = new LatencyStats
            {
                LastEndToEndLatency = totalPerceivedLatency,
                LastBootLatency = 0, // Not relevant for perceived latency
                LastSttLatency = _sttLatency, // Informational
                LastLlmLatency = _llmLatency, // Informational  
                LastPttDuration = _pttDuration, // Time user held PTT button
                LastTtsLatency = _ttsLatency, // TTS timing
                TtsLatencyLevel = _ttsLatencyLevel, // TTS latency level
                AverageLatency = _latencyHistory.Count > 0 ? _latencyHistory.Average() : 0,
                MinLatency = _latencyHistory.Count > 0 ? _latencyHistory.Min() : 0,
                MaxLatency = _latencyHistory.Count > 0 ? _latencyHistory.Max() : 0,
                SampleCount = _latencyHistory.Count,
                PersonaName = _personaName,
                LastUpdateTime = DateTime.Now
            };
            
            OnLatencyStatsUpdated?.Invoke(stats);

            // Debug: Log static event invocation
            if (OnLatencyStatsUpdatedStatic != null)
            {
                //UnityEngine.Debug.Log($"[{_personaName}] Invoking static event with {OnLatencyStatsUpdatedStatic.GetInvocationList().Length} subscribers");
                OnLatencyStatsUpdatedStatic.Invoke(stats);
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[{_personaName}] No subscribers to OnLatencyStatsUpdatedStatic event!");
            }
            
            // Clear the pending latency since it's been reported
            _pendingPerceivedLatency = 0;
            
            // Reset the tracking flags to prevent duplicate reporting
            _hasPlaybackStarted = false;
            _hasLlmCompleted = false;
            _hasTtsTimingReceived = false;
        }
        
    }
}