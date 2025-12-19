# AI Bridge Best Practices

This guide covers patterns, anti-patterns, and production tips for building robust AI conversation systems with AI Bridge.

## Table of Contents

1. [Architecture Patterns](#architecture-patterns)
2. [Performance Optimization](#performance-optimization)
3. [Error Handling](#error-handling)
4. [VR & Spatial Audio](#vr--spatial-audio)
5. [Conversation Design](#conversation-design)
6. [Testing Strategies](#testing-strategies)
7. [Production Readiness](#production-readiness)
8. [Common Anti-Patterns](#common-anti-patterns)

## Architecture Patterns

### Single Point of Orchestration

Always use `RequestOrchestrator` as your single entry point for conversations.

```csharp
// GOOD: Single orchestrator manages all conversation flow
public class ConversationManager : MonoBehaviour
{
    private RequestOrchestrator _orchestrator;

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;
    }

    public void TalkToNpc(string npcId)
    {
        _orchestrator.StartAudioRequest(npcId);
    }
}
```

```csharp
// BAD: Multiple entry points cause state confusion
public class BadConversationManager : MonoBehaviour
{
    private WebSocketClient _webSocket;
    private SpeechInputHandler _speechHandler;

    public void TalkToNpc(string npcId)
    {
        // Don't bypass the orchestrator!
        _webSocket.SendSessionStartAsync(...); // Wrong
        _speechHandler.StartRecording(); // Wrong
    }
}
```

### NPC Configuration Separation

Separate NPC personality from conversation mechanics.

```csharp
// GOOD: Configuration is data-driven
[CreateAssetMenu(fileName = "NpcPersonality", menuName = "AI/NPC Personality")]
public class NpcPersonalitySO : ScriptableObject
{
    public string npcName;
    [TextArea(5, 20)]
    public string systemPrompt;
    public string voiceId;
    public float voiceSpeed = 1.0f;
    public bool allowInterruption = true;
}

public class ConfigurableNpcClient : NpcClientBase
{
    [SerializeField] private NpcPersonalitySO personality;

    public override string SystemPrompt => personality.systemPrompt;
    public override string VoiceId => personality.voiceId;
}
```

### Event-Driven Communication

Use events to decouple UI from conversation logic.

```csharp
// GOOD: Event-driven updates
public class ConversationUI : MonoBehaviour
{
    [SerializeField] private Text transcriptionText;
    [SerializeField] private Text responseText;

    private void OnEnable()
    {
        var orchestrator = RequestOrchestrator.Instance;
        orchestrator.OnTranscriptionReceived += UpdateTranscription;

        var npc = FindFirstObjectByType<NpcClientBase>();
        npc.OnAiResponseReceived += UpdateResponse;
        npc.OnAudioStarted += ShowSpeakingIndicator;
        npc.OnAudioStopped += HideSpeakingIndicator;
    }

    private void UpdateTranscription(string text)
    {
        transcriptionText.text = $"You: {text}";
    }

    private void UpdateResponse(AiResponseMessage response)
    {
        responseText.text = $"NPC: {response.content}";
    }
}
```

### Stateless NPC Clients

Keep NPC clients stateless where possible. Store conversation history externally.

```csharp
// GOOD: History managed separately
public class ConversationHistoryManager : MonoBehaviour
{
    private Dictionary<string, List<ChatMessage>> _histories = new();

    public List<ChatMessage> GetHistory(string npcId)
    {
        if (!_histories.TryGetValue(npcId, out var history))
        {
            history = new List<ChatMessage>();
            _histories[npcId] = history;
        }
        return history;
    }

    public void ClearHistory(string npcId)
    {
        if (_histories.ContainsKey(npcId))
        {
            _histories[npcId].Clear();
        }
    }
}
```

## Performance Optimization

### Minimize Latency

For real-time conversations, every millisecond matters.

```csharp
// GOOD: Pre-warm connection before user needs it
public class WarmupManager : MonoBehaviour
{
    private void Start()
    {
        // Connect WebSocket early
        WebSocketClient.Instance.EnsureConnectionAsync();

        // Pre-request microphone permission
        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            Application.RequestUserAuthorization(UserAuthorization.Microphone);
        }
    }
}
```

### Audio Buffer Sizing

Balance between latency and stability.

```csharp
// StreamingAudioPlayer Inspector settings:
// - Playback Complete Timeout: 0.15s (default, lower = faster response)
// - Initial Buffer Size: Adjust based on network quality

// For low-latency local network:
// playbackCompleteTimeout = 0.1f

// For mobile/variable network:
// playbackCompleteTimeout = 0.3f
```

### Avoid Allocations in Hot Paths

Audio processing runs at high frequency. Avoid GC pressure.

```csharp
// GOOD: Pre-allocated buffers
public class AudioProcessor : MonoBehaviour
{
    private float[] _processingBuffer = new float[1024];

    public void ProcessAudio(float[] samples)
    {
        // Use pre-allocated buffer
        Array.Copy(samples, _processingBuffer, samples.Length);
        // Process...
    }
}
```

```csharp
// BAD: Allocations every frame
public class BadAudioProcessor : MonoBehaviour
{
    public void ProcessAudio(float[] samples)
    {
        // Creates new array every call - GC pressure!
        var copy = samples.ToArray();
    }
}
```

### Connection Management

Maintain persistent connections, don't reconnect per-request.

```csharp
// GOOD: Connection lifecycle tied to application
// WebSocketClient.establishConnectionOnStart = true (Inspector)

// Handle disconnections gracefully
WebSocketClient.Instance.OnConnectionStateChanged += (state) =>
{
    if (state == ConnectionState.Disconnected)
    {
        // Show reconnecting UI, don't panic
        ShowReconnectingIndicator();
    }
};
```

## Error Handling

### Graceful Degradation

Handle backend failures without crashing.

```csharp
public class ResilientConversationManager : MonoBehaviour
{
    [SerializeField] private float retryDelay = 2f;
    [SerializeField] private int maxRetries = 3;

    public async void StartConversationWithRetry(string npcId)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (!WebSocketClient.Instance.IsConnected)
                {
                    await WebSocketClient.Instance.EnsureConnectionAsync();
                }

                RequestOrchestrator.Instance.StartAudioRequest(npcId);
                return; // Success
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Conversation attempt {attempt + 1} failed: {ex.Message}");

                if (attempt < maxRetries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryDelay));
                }
            }
        }

        // All retries failed
        ShowOfflineMessage();
    }
}
```

### Handle STT Failures

Sometimes speech recognition fails. Handle it gracefully.

```csharp
public class SttFailureHandler : MonoBehaviour
{
    private void Start()
    {
        var orchestrator = RequestOrchestrator.Instance;
        orchestrator.OnSttFailed += HandleSttFailure;
    }

    private void HandleSttFailure(NoTranscriptMessage failure)
    {
        switch (failure.reason)
        {
            case "silence":
                ShowHint("I didn't hear anything. Please try again.");
                break;

            case "timeout":
                ShowHint("Speech recognition timed out. Try speaking more clearly.");
                break;

            case "unclear":
                ShowHint("I couldn't understand that. Could you repeat?");
                break;

            default:
                Debug.LogWarning($"STT failed: {failure.reason}");
                ShowHint("Something went wrong. Please try again.");
                break;
        }
    }
}
```

### Session Cleanup

Always clean up sessions, even on errors.

```csharp
public class SessionManager : MonoBehaviour
{
    private void OnApplicationQuit()
    {
        // Cancel any active session
        if (RequestOrchestrator.Instance?.IsProcessingRequest() == true)
        {
            RequestOrchestrator.Instance.CancelCurrentSession("Application quit");
        }
    }

    private void OnDestroy()
    {
        // Clean up event subscriptions
        if (RequestOrchestrator.Instance != null)
        {
            RequestOrchestrator.Instance.OnTranscriptionReceived -= HandleTranscription;
        }
    }
}
```

## VR & Spatial Audio

### Proper AudioSource Configuration

```csharp
public class VRNpcSetup : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;

    private void Start()
    {
        // Essential for VR spatial audio
        audioSource.spatialBlend = 1.0f;  // Full 3D
        audioSource.dopplerLevel = 0f;    // Disable doppler (unrealistic for speech)
        audioSource.spread = 0f;          // Point source
        audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        audioSource.minDistance = 1f;
        audioSource.maxDistance = 20f;

        // Don't start automatically
        audioSource.playOnAwake = false;
    }
}
```

### Handle VR Headset Events

```csharp
public class VRPauseHandler : MonoBehaviour
{
    private RequestOrchestrator _orchestrator;

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;
    }

    // Called when headset is removed/worn
    private void OnApplicationPause(bool isPaused)
    {
        _orchestrator.HandlePauseStateChange(isPaused, "VR Headset");
    }

    // Called when app loses/gains focus (Quest menu, etc.)
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            _orchestrator.HandlePauseStateChange(true, "VR Focus Lost");
        }
    }
}
```

### Distance-Based Interaction

```csharp
public class ProximityConversation : MonoBehaviour
{
    [SerializeField] private NpcClientBase npc;
    [SerializeField] private float interactionDistance = 3f;
    [SerializeField] private Transform playerHead;

    private bool _canInteract;

    private void Update()
    {
        float distance = Vector3.Distance(
            transform.position,
            playerHead.position
        );

        bool wasInteractable = _canInteract;
        _canInteract = distance <= interactionDistance;

        if (_canInteract && !wasInteractable)
        {
            ShowInteractionHint("Press trigger to talk");
        }
        else if (!_canInteract && wasInteractable)
        {
            HideInteractionHint();
        }
    }
}
```

## Conversation Design

### System Prompt Structure

```csharp
// GOOD: Structured, clear system prompt
public static class SystemPrompts
{
    public static string CreateNpcPrompt(NpcPersonality personality)
    {
        return $@"You are {personality.name}, {personality.role}.

PERSONALITY:
{personality.traits}

CONVERSATION RULES:
- Keep responses concise (1-3 sentences for casual chat)
- Use natural, conversational language
- Stay in character at all times
- If asked about topics outside your expertise, redirect politely

CONTEXT:
{personality.contextInfo}

Remember: You're having a real-time voice conversation. Be natural and engaging.";
    }
}
```

### Conversation History Management

```csharp
public class SmartHistoryManager : MonoBehaviour
{
    private const int MaxHistoryTurns = 10;
    private const int MaxTokensEstimate = 2000;

    public List<ChatMessage> GetTrimmedHistory(List<ChatMessage> fullHistory)
    {
        var result = new List<ChatMessage>();
        int estimatedTokens = 0;

        // Keep most recent messages, respect token limit
        for (int i = fullHistory.Count - 1; i >= 0; i--)
        {
            var msg = fullHistory[i];
            int msgTokens = EstimateTokens(msg.content);

            if (estimatedTokens + msgTokens > MaxTokensEstimate)
                break;

            if (result.Count >= MaxHistoryTurns * 2) // User + assistant pairs
                break;

            result.Insert(0, msg);
            estimatedTokens += msgTokens;
        }

        return result;
    }

    private int EstimateTokens(string text)
    {
        // Rough estimate: ~4 characters per token
        return text.Length / 4;
    }
}
```

### Handle Interruptions

```csharp
public class InterruptionHandler : MonoBehaviour
{
    [SerializeField] private NpcClientBase npc;

    private void Start()
    {
        var orchestrator = RequestOrchestrator.Instance;

        // When user interrupts NPC
        orchestrator.OnInterruptionOccurred += HandleInterruption;
    }

    private void HandleInterruption()
    {
        // NPC acknowledges interruption naturally
        // The system handles stopping audio automatically

        Debug.Log("User interrupted NPC - audio stopped");

        // Optionally trigger a "listening" animation
        npc.GetComponent<Animator>()?.SetTrigger("ListenInterrupted");
    }
}
```

## Testing Strategies

### Mock Backend for Unit Tests

```csharp
public class MockWebSocketClient : IWebSocketConnection
{
    public Queue<string> ResponseQueue { get; } = new();

    public async Task SendJsonAsync(object message)
    {
        // Simulate network delay
        await Task.Delay(50);

        // Return next queued response
        if (ResponseQueue.TryDequeue(out var response))
        {
            OnMessageReceived?.Invoke(response);
        }
    }

    public event Action<string> OnMessageReceived;
}

[Test]
public async Task Conversation_ReturnsTranscription()
{
    var mockSocket = new MockWebSocketClient();
    mockSocket.ResponseQueue.Enqueue(@"{
        ""type"": ""transcription"",
        ""text"": ""Hello world""
    }");

    // Test your conversation logic...
}
```

### Integration Test Checklist

```markdown
## Conversation Flow Tests

- [ ] Basic PTT conversation completes successfully
- [ ] Long responses stream without gaps
- [ ] Short responses have minimal latency
- [ ] User can interrupt NPC mid-response
- [ ] Conversation history accumulates correctly
- [ ] Session cleans up after completion

## Error Handling Tests

- [ ] Graceful handling of network disconnect
- [ ] Recovery from WebSocket reconnection
- [ ] STT timeout handled with user feedback
- [ ] Backend error doesn't crash client

## Platform Tests

- [ ] Windows: Audio capture and playback
- [ ] macOS: Opus library loads correctly
- [ ] Android: Microphone permission flow
- [ ] VR: Spatial audio positioning
- [ ] VR: Headset pause/resume
```

### Load Testing

```csharp
// Test concurrent requests (should be queued)
[Test]
public async Task ConcurrentRequests_AreQueued()
{
    var orchestrator = RequestOrchestrator.Instance;

    // Fire multiple requests rapidly
    orchestrator.StartAudioRequest("npc1");
    orchestrator.StartAudioRequest("npc2"); // Should queue, not conflict

    Assert.IsTrue(orchestrator.IsProcessingRequest());

    // Only one should be active
    // Second should wait in queue
}
```

## Production Readiness

### Logging Configuration

```csharp
// Development: Verbose logging
WebSocketClient.Instance.enableVerboseLogging = true;
RequestOrchestrator.Instance.enableVerboseLogging = true;

// Production: Errors only
WebSocketClient.Instance.enableVerboseLogging = false;
RequestOrchestrator.Instance.enableVerboseLogging = false;
```

### Monitoring Metrics

```csharp
public class ConversationMetrics : MonoBehaviour
{
    private float _sessionStartTime;
    private float _firstAudioTime;

    private void Start()
    {
        var orchestrator = RequestOrchestrator.Instance;
        orchestrator.OnSessionStarted += () => _sessionStartTime = Time.time;

        var npc = FindFirstObjectByType<NpcClientBase>();
        npc.OnAudioStarted += () =>
        {
            _firstAudioTime = Time.time;
            float latency = _firstAudioTime - _sessionStartTime;
            Debug.Log($"[Metrics] Time to first audio: {latency:F2}s");

            // Send to analytics
            Analytics.LogEvent("conversation_latency", latency);
        };
    }
}
```

### API Key Security

```csharp
// NEVER do this in production
public class InsecureApiKey : MonoBehaviour
{
    // BAD: Hardcoded key in source code
    private const string API_KEY = "sk-1234567890"; // WRONG!
}

// GOOD: Use secure provider
public class SecureApiKeyProvider : MonoBehaviour, IAsyncApiKeyProvider
{
    [SerializeField] private string configServerUrl;

    public IEnumerator GetOrchestratorApiKeyAsync(Action<bool, string> callback)
    {
        // Fetch from secure backend
        using var request = UnityWebRequest.Get(configServerUrl);
        request.SetRequestHeader("Authorization", GetUserToken());
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            callback(true, request.downloadHandler.text);
        }
        else
        {
            callback(false, request.error);
        }
    }
}
```

## Common Anti-Patterns

### Anti-Pattern: Polling for State

```csharp
// BAD: Polling wastes CPU
private void Update()
{
    if (RequestOrchestrator.Instance.IsProcessingRequest())
    {
        // Check every frame - wasteful!
    }
}

// GOOD: Event-driven
private void Start()
{
    RequestOrchestrator.Instance.OnSessionStarted += HandleSessionStarted;
    RequestOrchestrator.Instance.OnSessionEnded += HandleSessionEnded;
}
```

### Anti-Pattern: Blocking Async Operations

```csharp
// BAD: Blocks main thread
public void StartConversation()
{
    WebSocketClient.Instance.EnsureConnectionAsync().Result; // BLOCKS!
    RequestOrchestrator.Instance.StartAudioRequest(npcId);
}

// GOOD: Async all the way
public async void StartConversation()
{
    await WebSocketClient.Instance.EnsureConnectionAsync();
    RequestOrchestrator.Instance.StartAudioRequest(npcId);
}
```

### Anti-Pattern: Ignoring Session State

```csharp
// BAD: Ignores current state
public void TalkToNpc(string npcId)
{
    // Doesn't check if session already active
    RequestOrchestrator.Instance.StartAudioRequest(npcId);
}

// GOOD: Check state first
public void TalkToNpc(string npcId)
{
    var orchestrator = RequestOrchestrator.Instance;

    if (orchestrator.IsProcessingRequest())
    {
        Debug.LogWarning("Already in conversation");
        return;
    }

    orchestrator.StartAudioRequest(npcId);
}
```

### Anti-Pattern: Leaking Event Subscriptions

```csharp
// BAD: Never unsubscribes
public class LeakyComponent : MonoBehaviour
{
    private void Start()
    {
        RequestOrchestrator.Instance.OnTranscriptionReceived += HandleTranscription;
        // Never cleaned up!
    }
}

// GOOD: Clean subscription lifecycle
public class CleanComponent : MonoBehaviour
{
    private RequestOrchestrator _orchestrator;

    private void OnEnable()
    {
        _orchestrator = RequestOrchestrator.Instance;
        _orchestrator.OnTranscriptionReceived += HandleTranscription;
    }

    private void OnDisable()
    {
        if (_orchestrator != null)
        {
            _orchestrator.OnTranscriptionReceived -= HandleTranscription;
        }
    }
}
```

### Anti-Pattern: Multiple Audio Sources

```csharp
// BAD: Creating AudioSources dynamically
public void PlayResponse()
{
    var source = gameObject.AddComponent<AudioSource>(); // New source each time!
    source.Play();
}

// GOOD: Reuse single AudioSource
public class NpcAudio : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource; // Configured once

    public void PlayResponse()
    {
        // StreamingAudioPlayer manages this automatically
    }
}
```

### Anti-Pattern: Hardcoded Timeouts

```csharp
// BAD: Magic numbers
await Task.Delay(5000); // Why 5 seconds?

// GOOD: Named constants with documentation
private const float NetworkTimeoutSeconds = 5f; // Based on p99 backend latency
await Task.Delay(TimeSpan.FromSeconds(NetworkTimeoutSeconds));
```

## Summary

Key takeaways for production AI Bridge implementations:

1. **Use RequestOrchestrator** as single entry point
2. **Handle errors gracefully** with user feedback
3. **Pre-warm connections** before user needs them
4. **Clean up subscriptions** to prevent memory leaks
5. **Test on all target platforms** especially VR
6. **Monitor latency metrics** in production
7. **Keep API keys secure** - never in source code
8. **Design conversations** for real-time interaction
