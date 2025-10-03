using System;
using UnityEngine;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Simple implementation of INpcConfiguration for projects without PersonaSO/RuleSystem.
    /// Can be configured directly in Inspector or created at runtime.
    /// </summary>
    [Serializable]
    public class SimpleNpcConfiguration : INpcConfiguration
    {
        [Header("NPC Identity")]
        [SerializeField] private string id = Guid.NewGuid().ToString();
        [SerializeField] private string name = "Assistant";

        [Header("AI Configuration")]
        [TextArea(5, 10)]
        [SerializeField] private string systemPrompt = "Je bent een behulpzame assistent die in het Nederlands antwoordt.";

        [SerializeField] private string llmProvider = "openai";  // vertexai, openai, azure-openai
        [SerializeField] private string llmModel = "gpt-4o-mini";
        [Range(0f, 1f)]
        [SerializeField] private float temperature = 0.7f;
        [SerializeField] private int maxTokens = 500;

        [Header("Voice Configuration")]
        [SerializeField] private string ttsStreamingMode = "batch";
        [SerializeField] private string ttsModel = "eleven_turbo_v2_5";
        [SerializeField] private string ttsVoice = "onyx";
        [SerializeField] private string language = "nl-NL";
        [SerializeField] private string sttProvider = "google";

        [Header("Interruption Settings")]
        [SerializeField] private bool allowInterruption = true;
        [Range(0.5f, 3f)]
        [SerializeField] private float interruptionPersistenceTime = 1.5f;

        // Runtime state
        private bool isActive = false;
        private bool isTalking = false;

        // Properties
        public string Id => id;
        public string Name => name;
        public string SystemPrompt => systemPrompt;
        public System.Collections.Generic.List<Messages.ChatMessage> Messages => null; // No messages - orchestrator will get history from NPC client
        public string TtsStreamingMode => ttsStreamingMode;
        public string TtsModel => ttsModel;
        public string VoiceId => ttsVoice;
        public string Language => language;
        public string SttProvider => sttProvider;
        public string LlmProvider => llmProvider;
        public string LlmModel => llmModel;
        public float Temperature => temperature;
        public int MaxTokens => maxTokens;

        // Interruption properties
        public bool AllowInterruption => allowInterruption;
        public float InterruptionPersistenceTime => interruptionPersistenceTime;
        public bool IsActive => isActive;
        public bool IsTalking => isTalking;

        // State management methods
        public void SetActive(bool active) => isActive = active;
        public void SetTalking(bool talking) => isTalking = talking;

        // Events
        public event Action OnStartListening;
        public event Action OnStopListening;
        public event Action OnStartSpeaking;
        public event Action OnStopSpeaking;

        // Event triggers (can be called from UI or code)
        public void TriggerStartListening() => OnStartListening?.Invoke();
        public void TriggerStopListening() => OnStopListening?.Invoke();
        public void TriggerStartSpeaking() => OnStartSpeaking?.Invoke();
        public void TriggerStopSpeaking() => OnStopSpeaking?.Invoke();

        /// <summary>
        /// Create a default configuration
        /// </summary>
        public static SimpleNpcConfiguration CreateDefault()
        {
            return new SimpleNpcConfiguration();
        }

        /// <summary>
        /// Create a configuration with specific settings
        /// </summary>
        public static SimpleNpcConfiguration Create(string name, string systemPrompt, string llmProvider = "openai", string llmModel = "gpt-4o-mini")
        {
            return new SimpleNpcConfiguration
            {
                id = Guid.NewGuid().ToString(),
                name = name,
                systemPrompt = systemPrompt,
                llmProvider = llmProvider,
                llmModel = llmModel
            };
        }

        /// <summary>
        /// Set the NPC name (for testing purposes)
        /// </summary>
        public void SetName(string newName)
        {
            this.name = newName;
        }
    }
}