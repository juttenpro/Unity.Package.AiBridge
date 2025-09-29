using System;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Adapter class that wraps a ConversationRequest to implement INpcConfiguration.
    /// Allows ConversationRequest objects from the RuleSystem to be used where INpcConfiguration is expected.
    /// </summary>
    internal class ConversationRequestAdapter : INpcConfiguration
    {
        private readonly ConversationRequest _request;

        public ConversationRequestAdapter(ConversationRequest request)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));
        }

        #region INpcConfiguration Implementation

        /// <summary>
        /// Unique identifier for this NPC
        /// </summary>
        public string Id => _request.NpcId;

        /// <summary>
        /// Display name of the NPC (uses ID as fallback)
        /// </summary>
        public string Name => _request.NpcId;

        /// <summary>
        /// System prompt that defines the NPC's personality and behavior
        /// </summary>
        public string SystemPrompt => _request.SystemPrompt;

        /// <summary>
        /// TTS streaming mode
        /// </summary>
        public string TtsStreamingMode => _request.TtsStreamingMode;

        /// <summary>
        /// TTS model to use
        /// </summary>
        public string TtsModel => _request.TtsModel;

        /// <summary>
        /// TTS voice ID
        /// </summary>
        public string VoiceId => _request.VoiceId;

        /// <summary>
        /// Language code
        /// </summary>
        public string Language => _request.Language;

        /// <summary>
        /// STT provider to use
        /// </summary>
        public string SttProvider => _request.SttProvider;

        /// <summary>
        /// LLM provider to use
        /// </summary>
        public string LlmProvider => _request.LlmProvider;

        /// <summary>
        /// LLM model to use
        /// </summary>
        public string LlmModel => _request.LlmModel;

        /// <summary>
        /// Temperature for LLM responses
        /// </summary>
        public float Temperature => _request.Temperature;

        /// <summary>
        /// Maximum tokens for LLM response
        /// </summary>
        public int MaxTokens => _request.MaxTokens;

        /// <summary>
        /// Whether this NPC can be interrupted during speech
        /// </summary>
        public bool AllowInterruption => _request.AllowInterruption;

        /// <summary>
        /// How long user must speak to trigger interruption
        /// </summary>
        public float InterruptionPersistenceTime => _request.InterruptionPersistenceTime;
        
        /// <summary>
        /// Whether this NPC is currently active (adapter always returns false)
        /// </summary>
        public bool IsActive => false;

        /// <summary>
        /// Whether this NPC is currently producing speech (adapter always returns false)
        /// </summary>
        public bool IsTalking => false;

        // Animation events (not used by adapter, but required by interface)
        // Suppress CS0067 warnings since these are intentionally not used
#pragma warning disable CS0067
        public event Action OnStartListening;
        public event Action OnStopListening;
        public event Action OnStartSpeaking;
        public event Action OnStopSpeaking;
#pragma warning restore CS0067

        #endregion
    }
}