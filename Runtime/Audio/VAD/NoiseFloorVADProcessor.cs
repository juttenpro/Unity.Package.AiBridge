using System;
using System.Collections.Generic;

namespace Tsc.AIBridge.Audio.VAD
{
    /// <summary>
    /// A simple adaptive VAD: threshold = max(minimum, room noise x factor), where the room noise is the 10th percentile of
    /// the frame RMS over the last 5 seconds. The quietest frames of a few seconds are the room, so speech pauses, the
    /// patient's own voice and the NPC's echo do not lift it; a steady noise (a TV, a fan) does.
    ///
    /// Why next to DynamicRangeVADProcessor: on a phone microphone a normal voice is RMS 0.010-0.017 in a room at
    /// 0.0003-0.0005. DynamicRange's threshold stayed at 0.013-0.014 (a noise floor of at least 0.003, plus 35% of the way
    /// to earlier loud speech) and rose to ~0.02 while the patient spoke, because his speech pauses counted as noise
    /// (Assistant Platform, 2026-11-04). Opt-in, so push-to-talk headsets keep the tuned DynamicRange behaviour.
    /// </summary>
    public sealed class NoiseFloorVADProcessor : VADProcessorBase
    {
        public const float DefaultMinimumThreshold = 0.006f;
        public const float DefaultNoiseFactor = 3f;

        private const float WindowSeconds = 5f;
        private const float NoisePercentile = 0.10f;
        private const float SustainedSpeechTime = 0.05f; // as DynamicRange: 50 ms above the threshold
        private const int SmoothingWindow = 5;
        private const int SampleRate = 16000; // MicrophoneCapture records mono at 16 kHz

        private readonly float _minimumThreshold;
        private readonly float _noiseFactor;
        private readonly Queue<(float Rms, float Seconds)> _window = new();
        private readonly List<float> _sorted = new();
        private readonly Queue<float> _recentVolumes = new();
        private float _windowSeconds;
        private float _aboveThresholdTime;
        private bool _isNpcSpeaking;

        /// <summary>The room noise: the 10th percentile of the last 5 s. 0 until the first frame.</summary>
        public float NoiseFloor { get; private set; }

        public override float CurrentThreshold => Math.Max(_minimumThreshold, NoiseFloor * _noiseFactor);

        public NoiseFloorVADProcessor(string name, float minimumThreshold = DefaultMinimumThreshold,
            float noiseFactor = DefaultNoiseFactor, bool enableVerboseLogging = false)
            : base(name, enableVerboseLogging)
        {
            if (!(minimumThreshold > 0f))
                throw new ArgumentOutOfRangeException(nameof(minimumThreshold), minimumThreshold, "The minimum threshold must be above 0.");
            if (!(noiseFactor >= 1f))
                throw new ArgumentOutOfRangeException(nameof(noiseFactor), noiseFactor, "The noise factor must be at least 1.");
            _minimumThreshold = minimumThreshold;
            _noiseFactor = noiseFactor;
        }

        /// <summary>While the NPC speaks its voice through the speaker is not room noise: those frames stay out of the window.</summary>
        public void SetNpcSpeaking(bool isSpeaking) => _isNpcSpeaking = isSpeaking;

        public override bool ProcessAudioFrame(float[] audioData, float additionalContext = 0f)
        {
            if (audioData == null || audioData.Length == 0)
                return CurrentlySpeaking;

            LastAudioFrame = audioData;
            var seconds = audioData.Length / (float)SampleRate;
            var rms = CalculateRMS(audioData);
            if (!_isNpcSpeaking)
                AddToWindow(rms, seconds);

            _recentVolumes.Enqueue(rms);
            if (_recentVolumes.Count > SmoothingWindow)
                _recentVolumes.Dequeue();
            var smoothed = 0f;
            foreach (var volume in _recentVolumes) smoothed += volume;
            smoothed /= _recentVolumes.Count;

            _aboveThresholdTime = smoothed > CurrentThreshold ? _aboveThresholdTime + seconds : 0f;
            return ProcessSpeechDetection(_aboveThresholdTime >= SustainedSpeechTime, seconds);
        }

        private void AddToWindow(float rms, float seconds)
        {
            _window.Enqueue((rms, seconds));
            _windowSeconds += seconds;
            while (_windowSeconds > WindowSeconds && _window.Count > 1)
                _windowSeconds -= _window.Dequeue().Seconds;

            _sorted.Clear();
            foreach (var frame in _window) _sorted.Add(frame.Rms);
            _sorted.Sort();
            NoiseFloor = _sorted[(int)(_sorted.Count * NoisePercentile)];
        }

        public override void Reset()
        {
            base.Reset();
            // The room is still the room: the noise window stays, only the running detection starts over.
            _recentVolumes.Clear();
            _aboveThresholdTime = 0f;
        }
    }
}
