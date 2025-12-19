# AI Bridge Examples

Real-world implementation examples for common use cases.

## Table of Contents

1. [Basic Conversation NPC](#basic-conversation-npc)
2. [VR Training Scenario](#vr-training-scenario)
3. [Customer Service Bot](#customer-service-bot)
4. [Multi-NPC Environment](#multi-npc-environment)
5. [Language Learning App](#language-learning-app)
6. [Interactive Museum Guide](#interactive-museum-guide)
7. [Healthcare Training Simulation](#healthcare-training-simulation)

---

## Basic Conversation NPC

A simple NPC that responds to player questions.

### Scene Setup

```
Scene
├── Player
│   └── Camera (with AudioListener)
│
├── AIBridgeManager
│   ├── WebSocketClient
│   ├── RequestOrchestrator
│   ├── SpeechInputHandler
│   ├── MicrophoneCapture
│   └── EnvironmentApiKeyProvider
│
└── NPC_Shopkeeper
    ├── 3D Model
    ├── Animator
    ├── AudioSource (3D spatial)
    ├── StreamingAudioPlayer
    └── ShopkeeperNPC (custom script)
```

### ShopkeeperNPC.cs

```csharp
using System;
using System.Collections.Generic;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

public class ShopkeeperNPC : NpcClientBase
{
    [Header("NPC Settings")]
    [SerializeField] private string npcName = "Marcus the Shopkeeper";

    [TextArea(5, 15)]
    [SerializeField] private string systemPrompt = @"You are Marcus, a friendly medieval shopkeeper.
You sell weapons, armor, and potions.
Keep responses brief and in-character.
Use medieval speech patterns occasionally.";

    [Header("Voice Settings")]
    [SerializeField] private string voiceId = "21m00Tcm4TlvDq8ikWAM"; // ElevenLabs voice
    [SerializeField] private string ttsModel = "eleven_turbo_v2_5";
    [SerializeField, Range(0.7f, 1.3f)] private float voiceSpeed = 1.0f;

    [Header("LLM Settings")]
    [SerializeField] private string llmProvider = "openai";
    [SerializeField] private string llmModel = "gpt-4o-mini";
    [SerializeField, Range(0f, 1f)] private float temperature = 0.7f;

    [Header("Components")]
    [SerializeField] private Animator animator;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private StreamingAudioPlayer streamingPlayer;

    // Conversation history
    private List<ChatMessage> _history = new();

    public override string NpcName => npcName;
    public override string SystemPrompt => systemPrompt;
    public override string VoiceId => voiceId;
    public override string TtsModel => ttsModel;
    public override float TtsSpeed => voiceSpeed;
    public override string LlmProvider => llmProvider;
    public override string LlmModel => llmModel;
    public override float Temperature => temperature;

    protected override void Awake()
    {
        base.Awake();

        // Configure audio source for 3D
        audioSource.spatialBlend = 1.0f;
        audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        audioSource.minDistance = 1f;
        audioSource.maxDistance = 15f;
    }

    protected override void Start()
    {
        base.Start();

        // Subscribe to events
        OnAudioStarted += HandleSpeakingStarted;
        OnAudioStopped += HandleSpeakingStopped;
        OnAiResponseReceived += HandleResponse;
    }

    private void HandleSpeakingStarted()
    {
        animator?.SetBool("IsTalking", true);
    }

    private void HandleSpeakingStopped()
    {
        animator?.SetBool("IsTalking", false);
    }

    private void HandleResponse(AiResponseMessage response)
    {
        // Store in history for context
        _history.Add(new ChatMessage
        {
            role = "assistant",
            content = response.content
        });

        Debug.Log($"[{npcName}] {response.content}");
    }

    public override List<ChatMessage> GetApiHistoryAsChatMessages()
    {
        return _history;
    }

    public override void AddPlayerMessage(string message)
    {
        _history.Add(new ChatMessage
        {
            role = "user",
            content = message
        });
    }

    public override void ClearHistory()
    {
        _history.Clear();
    }
}
```

### PlayerConversationController.cs

```csharp
using Tsc.AIBridge.Core;
using UnityEngine;

public class PlayerConversationController : MonoBehaviour
{
    [SerializeField] private ShopkeeperNPC targetNpc;
    [SerializeField] private KeyCode talkKey = KeyCode.Space;
    [SerializeField] private float interactionDistance = 3f;

    private RequestOrchestrator _orchestrator;
    private bool _isRecording;

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;
    }

    private void Update()
    {
        if (!IsInRange()) return;

        if (Input.GetKeyDown(talkKey) && !_isRecording)
        {
            StartTalking();
        }

        if (Input.GetKeyUp(talkKey) && _isRecording)
        {
            StopTalking();
        }
    }

    private bool IsInRange()
    {
        return Vector3.Distance(transform.position, targetNpc.transform.position)
            <= interactionDistance;
    }

    private void StartTalking()
    {
        _isRecording = true;
        _orchestrator.StartAudioRequest(targetNpc.NpcId);
    }

    private void StopTalking()
    {
        _isRecording = false;
        _orchestrator.EndAudioRequest();
    }
}
```

---

## VR Training Scenario

A job interview training simulation for VR.

### InterviewTrainer.cs

```csharp
using System;
using System.Collections.Generic;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;
using UnityEngine.XR;

public class InterviewTrainer : MonoBehaviour
{
    [Header("Interview Settings")]
    [SerializeField] private InterviewScenarioSO scenario;
    [SerializeField] private InterviewerNPC interviewer;

    [Header("VR Input")]
    [SerializeField] private XRNode triggerHand = XRNode.RightHand;

    [Header("UI")]
    [SerializeField] private InterviewUI ui;
    [SerializeField] private FeedbackPanel feedbackPanel;

    private RequestOrchestrator _orchestrator;
    private InputDevice _controller;
    private bool _wasPressed;
    private int _questionIndex;
    private List<InterviewTurn> _sessionHistory = new();

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;
        _orchestrator.OnTranscriptionReceived += HandleUserResponse;

        interviewer.OnAiResponseReceived += HandleInterviewerResponse;
        interviewer.OnAudioStopped += HandleInterviewerFinishedSpeaking;

        // Start interview with first question
        StartInterview();
    }

    private void Update()
    {
        if (!_controller.isValid)
        {
            _controller = InputDevices.GetDeviceAtXRNode(triggerHand);
            return;
        }

        _controller.TryGetFeatureValue(CommonUsages.triggerButton, out bool isPressed);

        // PTT: Trigger pressed
        if (isPressed && !_wasPressed)
        {
            if (!interviewer.IsTalking)
            {
                StartRecording();
            }
        }

        // PTT: Trigger released
        if (!isPressed && _wasPressed)
        {
            StopRecording();
        }

        _wasPressed = isPressed;
    }

    private void StartInterview()
    {
        _questionIndex = 0;

        // Set up interviewer with scenario context
        interviewer.SetSystemPrompt(GenerateInterviewerPrompt());

        // Interviewer introduces themselves
        var introRequest = new ConversationRequest
        {
            NpcId = interviewer.NpcId,
            IsNpcInitiated = true,
            InitialPrompt = "Begin the interview by introducing yourself briefly and asking the first question."
        };

        _orchestrator.StartConversationRequest(introRequest);
        ui.ShowStatus("Interview Starting...");
    }

    private string GenerateInterviewerPrompt()
    {
        return $@"You are {scenario.interviewerName}, a professional interviewer conducting a job interview.

INTERVIEW CONTEXT:
- Position: {scenario.jobTitle}
- Company: {scenario.companyName}
- Interview Style: {scenario.interviewStyle}

QUESTIONS TO COVER (in order):
{FormatQuestions(scenario.questions)}

BEHAVIOR GUIDELINES:
- Ask one question at a time
- Listen to answers and provide brief acknowledgment
- If answer is incomplete, ask a follow-up question
- Stay professional but friendly
- Keep responses concise (this is spoken dialogue)

After all questions, thank the candidate and conclude the interview.";
    }

    private string FormatQuestions(List<InterviewQuestion> questions)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < questions.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {questions[i].text}");
            if (!string.IsNullOrEmpty(questions[i].followUpHint))
            {
                sb.AppendLine($"   (Follow-up if needed: {questions[i].followUpHint})");
            }
        }
        return sb.ToString();
    }

    private void StartRecording()
    {
        ui.ShowRecordingIndicator(true);
        _orchestrator.StartAudioRequest(interviewer.NpcId);
    }

    private void StopRecording()
    {
        ui.ShowRecordingIndicator(false);
        _orchestrator.EndAudioRequest();
        ui.ShowStatus("Processing...");
    }

    private void HandleUserResponse(string transcription)
    {
        _sessionHistory.Add(new InterviewTurn
        {
            speaker = "Candidate",
            text = transcription,
            timestamp = Time.time
        });

        ui.ShowStatus("Interviewer thinking...");
    }

    private void HandleInterviewerResponse(AiResponseMessage response)
    {
        _sessionHistory.Add(new InterviewTurn
        {
            speaker = "Interviewer",
            text = response.content,
            timestamp = Time.time
        });
    }

    private void HandleInterviewerFinishedSpeaking()
    {
        if (IsInterviewComplete())
        {
            ShowFeedback();
        }
        else
        {
            ui.ShowStatus("Hold trigger to respond");
        }
    }

    private bool IsInterviewComplete()
    {
        // Check if interviewer has concluded
        var lastResponse = _sessionHistory.FindLast(t => t.speaker == "Interviewer");
        return lastResponse?.text.Contains("thank you for your time") == true ||
               lastResponse?.text.Contains("conclude") == true;
    }

    private void ShowFeedback()
    {
        // Request AI analysis of the interview
        var analysisRequest = new AnalysisRequest
        {
            SessionHistory = _sessionHistory,
            EvaluationCriteria = scenario.evaluationCriteria
        };

        feedbackPanel.RequestFeedback(analysisRequest);
        ui.ShowStatus("Interview Complete - Generating Feedback...");
    }
}

[Serializable]
public class InterviewTurn
{
    public string speaker;
    public string text;
    public float timestamp;
}
```

### InterviewScenarioSO.cs

```csharp
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "InterviewScenario", menuName = "Training/Interview Scenario")]
public class InterviewScenarioSO : ScriptableObject
{
    public string scenarioName;
    public string jobTitle;
    public string companyName;
    public string interviewerName;

    [TextArea(3, 5)]
    public string interviewStyle;

    public List<InterviewQuestion> questions;

    [TextArea(5, 10)]
    public string evaluationCriteria;
}

[Serializable]
public class InterviewQuestion
{
    [TextArea(2, 4)]
    public string text;
    public string followUpHint;
    public string evaluationFocus;
}
```

---

## Customer Service Bot

A customer support agent for help desk scenarios.

### CustomerServiceBot.cs

```csharp
using System;
using System.Collections.Generic;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

public class CustomerServiceBot : NpcClientBase
{
    [Header("Service Configuration")]
    [SerializeField] private ServiceConfigSO serviceConfig;
    [SerializeField] private KnowledgeBaseSO knowledgeBase;

    [Header("Voice")]
    [SerializeField] private string voiceId = "EXAVITQu4vr4xnSDxMaL"; // Professional voice
    [SerializeField] private float voiceSpeed = 0.95f; // Slightly slower for clarity

    [Header("Conversation")]
    [SerializeField] private int maxHistoryTurns = 10;

    private List<ChatMessage> _conversationHistory = new();
    private CustomerInfo _currentCustomer;

    public override string NpcName => "Support Agent";
    public override string VoiceId => voiceId;
    public override float TtsSpeed => voiceSpeed;
    public override string LlmProvider => "openai";
    public override string LlmModel => "gpt-4o";
    public override float Temperature => 0.3f; // Lower for more consistent responses

    public override string SystemPrompt => GenerateSystemPrompt();

    private string GenerateSystemPrompt()
    {
        return $@"You are a professional customer service agent for {serviceConfig.companyName}.

COMPANY INFORMATION:
{serviceConfig.companyInfo}

PRODUCT KNOWLEDGE:
{knowledgeBase.GetFormattedKnowledge()}

CUSTOMER CONTEXT:
{(_currentCustomer != null ? FormatCustomerInfo() : "No customer context available.")}

SERVICE GUIDELINES:
1. Greet customers warmly and professionally
2. Listen actively to understand the issue
3. Provide accurate information from the knowledge base
4. If you don't know something, say so and offer to escalate
5. Always confirm the customer's issue is resolved before ending
6. Keep responses concise but complete

ESCALATION TRIGGERS:
- Technical issues beyond basic troubleshooting
- Billing disputes over ${serviceConfig.escalationThreshold}
- Complaints about employee conduct
- Legal or compliance questions

When escalating, explain why and what will happen next.";
    }

    private string FormatCustomerInfo()
    {
        return $@"Customer Name: {_currentCustomer.Name}
Account Type: {_currentCustomer.AccountType}
Customer Since: {_currentCustomer.CustomerSince}
Recent Purchases: {string.Join(", ", _currentCustomer.RecentPurchases)}
Open Tickets: {_currentCustomer.OpenTickets}";
    }

    public void SetCustomerContext(CustomerInfo customer)
    {
        _currentCustomer = customer;
    }

    public override List<ChatMessage> GetApiHistoryAsChatMessages()
    {
        // Keep only recent history to stay within context limits
        if (_conversationHistory.Count > maxHistoryTurns * 2)
        {
            var trimmedHistory = _conversationHistory
                .Skip(_conversationHistory.Count - maxHistoryTurns * 2)
                .ToList();
            return trimmedHistory;
        }
        return _conversationHistory;
    }

    public override void AddPlayerMessage(string message)
    {
        _conversationHistory.Add(new ChatMessage
        {
            role = "user",
            content = message
        });
    }

    protected override void HandleAiResponse(AiResponseMessage response)
    {
        base.HandleAiResponse(response);

        _conversationHistory.Add(new ChatMessage
        {
            role = "assistant",
            content = response.content
        });

        // Check for escalation keywords
        if (ShouldEscalate(response.content))
        {
            OnEscalationNeeded?.Invoke(_currentCustomer, _conversationHistory);
        }
    }

    private bool ShouldEscalate(string response)
    {
        var escalationPhrases = new[]
        {
            "transfer you to",
            "escalate this",
            "supervisor",
            "specialist",
            "technical team"
        };

        return escalationPhrases.Any(phrase =>
            response.ToLower().Contains(phrase));
    }

    public event Action<CustomerInfo, List<ChatMessage>> OnEscalationNeeded;

    public void StartNewConversation()
    {
        _conversationHistory.Clear();
    }
}

[Serializable]
public class CustomerInfo
{
    public string Name;
    public string AccountType;
    public string CustomerSince;
    public List<string> RecentPurchases;
    public int OpenTickets;
}
```

---

## Multi-NPC Environment

Managing conversations with multiple NPCs in one scene.

### NPCManager.cs

```csharp
using System.Collections.Generic;
using Tsc.AIBridge.Core;
using UnityEngine;

public class NPCManager : MonoBehaviour, INpcProvider
{
    [SerializeField] private List<NpcClientBase> npcs = new();

    private Dictionary<string, NpcClientBase> _npcLookup = new();
    private NpcClientBase _activeNpc;

    private void Awake()
    {
        // Build lookup dictionary
        foreach (var npc in npcs)
        {
            _npcLookup[npc.NpcId] = npc;
        }

        // Register as NPC provider
        var orchestrator = RequestOrchestrator.Instance;
        if (orchestrator != null)
        {
            // Set this as the NPC provider
            orchestrator.SetNpcProvider(this);
        }
    }

    public INpcConfiguration GetNpcConfiguration(string npcId)
    {
        if (_npcLookup.TryGetValue(npcId, out var npc))
        {
            return npc;
        }
        return null;
    }

    public NpcClientBase GetNpcClient(string npcId)
    {
        _npcLookup.TryGetValue(npcId, out var npc);
        return npc;
    }

    public NpcClientBase GetNearestNpc(Vector3 position, float maxDistance = float.MaxValue)
    {
        NpcClientBase nearest = null;
        float nearestDistance = maxDistance;

        foreach (var npc in npcs)
        {
            float distance = Vector3.Distance(position, npc.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = npc;
            }
        }

        return nearest;
    }

    public void SetActiveNpc(NpcClientBase npc)
    {
        // Deactivate previous
        if (_activeNpc != null)
        {
            _activeNpc.IsActive = false;
        }

        // Activate new
        _activeNpc = npc;
        if (_activeNpc != null)
        {
            _activeNpc.IsActive = true;
        }
    }
}
```

### MultiNPCConversationController.cs

```csharp
using Tsc.AIBridge.Core;
using UnityEngine;

public class MultiNPCConversationController : MonoBehaviour
{
    [SerializeField] private NPCManager npcManager;
    [SerializeField] private Transform playerHead;
    [SerializeField] private float interactionDistance = 3f;
    [SerializeField] private LayerMask npcLayer;

    private RequestOrchestrator _orchestrator;
    private NpcClientBase _currentTarget;
    private bool _isRecording;

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;
    }

    private void Update()
    {
        // Update target based on where player is looking
        UpdateTarget();

        // Handle PTT
        if (Input.GetKeyDown(KeyCode.Space) && _currentTarget != null)
        {
            StartTalkingTo(_currentTarget);
        }

        if (Input.GetKeyUp(KeyCode.Space) && _isRecording)
        {
            StopTalking();
        }
    }

    private void UpdateTarget()
    {
        // Raycast from player's view
        if (Physics.Raycast(playerHead.position, playerHead.forward,
            out RaycastHit hit, interactionDistance, npcLayer))
        {
            var npc = hit.collider.GetComponent<NpcClientBase>();
            if (npc != null && npc != _currentTarget)
            {
                SetTarget(npc);
            }
        }
        else
        {
            // Check proximity as fallback
            var nearestNpc = npcManager.GetNearestNpc(
                playerHead.position,
                interactionDistance
            );

            if (nearestNpc != _currentTarget)
            {
                SetTarget(nearestNpc);
            }
        }
    }

    private void SetTarget(NpcClientBase npc)
    {
        // Visual feedback for previous target
        if (_currentTarget != null)
        {
            _currentTarget.GetComponent<NPCHighlight>()?.SetHighlighted(false);
        }

        _currentTarget = npc;
        npcManager.SetActiveNpc(npc);

        // Visual feedback for new target
        if (_currentTarget != null)
        {
            _currentTarget.GetComponent<NPCHighlight>()?.SetHighlighted(true);
            ShowInteractionHint($"Talk to {_currentTarget.NpcName}");
        }
        else
        {
            HideInteractionHint();
        }
    }

    private void StartTalkingTo(NpcClientBase npc)
    {
        _isRecording = true;
        _orchestrator.StartAudioRequest(npc.NpcId);
    }

    private void StopTalking()
    {
        _isRecording = false;
        _orchestrator.EndAudioRequest();
    }

    private void ShowInteractionHint(string text) { /* UI implementation */ }
    private void HideInteractionHint() { /* UI implementation */ }
}
```

---

## Language Learning App

A conversational language tutor.

### LanguageTutor.cs

```csharp
using System;
using System.Collections.Generic;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

public class LanguageTutor : NpcClientBase
{
    [Header("Language Settings")]
    [SerializeField] private string targetLanguage = "Spanish";
    [SerializeField] private string nativeLanguage = "English";
    [SerializeField] private LanguageLevel studentLevel = LanguageLevel.Beginner;

    [Header("Lesson")]
    [SerializeField] private LessonSO currentLesson;

    [Header("Voice")]
    [SerializeField] private string voiceId; // Native speaker voice
    [SerializeField] private float voiceSpeed = 0.85f; // Slower for learners

    private List<ChatMessage> _lessonHistory = new();
    private int _correctAnswers;
    private int _totalQuestions;

    public override string NpcName => $"{targetLanguage} Tutor";
    public override string VoiceId => voiceId;
    public override float TtsSpeed => voiceSpeed;
    public override string LlmProvider => "openai";
    public override string LlmModel => "gpt-4o";

    public override string SystemPrompt => GenerateTutorPrompt();

    private string GenerateTutorPrompt()
    {
        return $@"You are a patient and encouraging {targetLanguage} language tutor.

STUDENT PROFILE:
- Native language: {nativeLanguage}
- Target language: {targetLanguage}
- Current level: {studentLevel}

CURRENT LESSON:
{(currentLesson != null ? currentLesson.GetLessonContent() : "Free conversation practice")}

TEACHING APPROACH:
1. Speak primarily in {targetLanguage} with {nativeLanguage} support based on level
2. For beginners: 70% target language, 30% native explanations
3. For intermediate: 90% target language, 10% native for complex concepts
4. For advanced: 100% target language

INTERACTION GUIDELINES:
- Gently correct mistakes by rephrasing correctly
- Praise progress and effort
- Ask follow-up questions to encourage speaking
- Introduce vocabulary naturally in context
- If student seems stuck, offer hints rather than answers

PRONUNCIATION GUIDANCE:
When correcting pronunciation, say ""try saying it like..."" and repeat slowly.

VOCABULARY TRACKING:
Note new words used and revisit them in conversation.

Remember: The goal is building confidence through conversation, not perfect grammar.";
    }

    protected override void Start()
    {
        base.Start();

        OnAiResponseReceived += AnalyzeForVocabulary;

        var orchestrator = RequestOrchestrator.Instance;
        orchestrator.OnTranscriptionReceived += AnalyzeStudentSpeech;
    }

    private void AnalyzeStudentSpeech(string transcription)
    {
        // Track student progress
        _lessonHistory.Add(new ChatMessage
        {
            role = "user",
            content = transcription
        });

        // Could analyze for vocabulary usage, grammar patterns, etc.
        OnStudentSpoke?.Invoke(transcription);
    }

    private void AnalyzeForVocabulary(AiResponseMessage response)
    {
        _lessonHistory.Add(new ChatMessage
        {
            role = "assistant",
            content = response.content
        });

        // Extract vocabulary for review later
        // This could use a separate AI call or local processing
    }

    public void StartLesson(LessonSO lesson)
    {
        currentLesson = lesson;
        _lessonHistory.Clear();
        _correctAnswers = 0;
        _totalQuestions = 0;

        // Tutor initiates with lesson introduction
        var request = new ConversationRequest
        {
            NpcId = NpcId,
            IsNpcInitiated = true,
            InitialPrompt = $"Begin the lesson on '{lesson.topic}'. Introduce the topic briefly and start with an easy warm-up question."
        };

        RequestOrchestrator.Instance.StartConversationRequest(request);
    }

    public void RequestPronunciationHelp(string word)
    {
        var request = new ConversationRequest
        {
            NpcId = NpcId,
            IsNpcInitiated = true,
            InitialPrompt = $"The student wants help pronouncing '{word}'. Say it slowly and clearly, then use it in a simple sentence."
        };

        RequestOrchestrator.Instance.StartConversationRequest(request);
    }

    public override List<ChatMessage> GetApiHistoryAsChatMessages()
    {
        return _lessonHistory;
    }

    public event Action<string> OnStudentSpoke;
    public event Action<string[]> OnNewVocabulary;
}

public enum LanguageLevel
{
    Beginner,
    Elementary,
    Intermediate,
    UpperIntermediate,
    Advanced
}
```

---

## Interactive Museum Guide

A knowledgeable guide for educational experiences.

### MuseumGuide.cs

```csharp
using System.Collections.Generic;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

public class MuseumGuide : NpcClientBase
{
    [Header("Guide Settings")]
    [SerializeField] private string guideName = "Dr. Helena";
    [SerializeField] private MuseumExhibitSO[] exhibits;

    [Header("Current Context")]
    [SerializeField] private MuseumExhibitSO currentExhibit;

    private List<ChatMessage> _tourHistory = new();
    private HashSet<string> _visitedExhibits = new();

    public override string NpcName => guideName;
    public override string LlmProvider => "vertexai";
    public override string LlmModel => "gemini-1.5-flash";
    public override float Temperature => 0.7f;

    public override string SystemPrompt => GenerateGuidePrompt();

    private string GenerateGuidePrompt()
    {
        return $@"You are {guideName}, an enthusiastic and knowledgeable museum guide.

CURRENT EXHIBIT:
{(currentExhibit != null ? FormatExhibitInfo(currentExhibit) : "Currently in the main hall.")}

MUSEUM LAYOUT:
{FormatAllExhibits()}

EXHIBITS VISITOR HAS SEEN:
{(_visitedExhibits.Count > 0 ? string.Join(", ", _visitedExhibits) : "None yet")}

INTERACTION STYLE:
- Share fascinating facts and stories
- Connect exhibits to broader historical/cultural context
- Encourage questions and curiosity
- Suggest related exhibits the visitor might enjoy
- Adjust depth of explanation based on visitor's questions
- Keep individual responses concise (1-2 minutes spoken)

VISITOR ENGAGEMENT:
- If visitor seems interested, offer deeper details
- If visitor asks ""what's next?"", suggest a logical path
- Reference connections between exhibits they've seen

Remember: You're bringing history/art/science to life through storytelling!";
    }

    private string FormatExhibitInfo(MuseumExhibitSO exhibit)
    {
        return $@"Name: {exhibit.exhibitName}
Era/Period: {exhibit.era}
Key Facts:
{exhibit.keyFacts}

Interesting Stories:
{exhibit.stories}

Common Questions:
{exhibit.commonQA}";
    }

    private string FormatAllExhibits()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var exhibit in exhibits)
        {
            sb.AppendLine($"- {exhibit.exhibitName} ({exhibit.era})");
        }
        return sb.ToString();
    }

    public void SetCurrentExhibit(MuseumExhibitSO exhibit)
    {
        currentExhibit = exhibit;
        _visitedExhibits.Add(exhibit.exhibitName);
    }

    public void StartTour()
    {
        _tourHistory.Clear();
        _visitedExhibits.Clear();

        var request = new ConversationRequest
        {
            NpcId = NpcId,
            IsNpcInitiated = true,
            InitialPrompt = "Welcome the visitor to the museum and offer to guide them. Ask if they have particular interests or would like your recommended tour."
        };

        RequestOrchestrator.Instance.StartConversationRequest(request);
    }

    public void ExplainCurrentExhibit()
    {
        if (currentExhibit == null) return;

        var request = new ConversationRequest
        {
            NpcId = NpcId,
            IsNpcInitiated = true,
            InitialPrompt = $"Give an engaging introduction to the {currentExhibit.exhibitName} exhibit. Share one fascinating fact that surprises most visitors."
        };

        RequestOrchestrator.Instance.StartConversationRequest(request);
    }

    public override List<ChatMessage> GetApiHistoryAsChatMessages()
    {
        return _tourHistory;
    }

    public override void AddPlayerMessage(string message)
    {
        _tourHistory.Add(new ChatMessage { role = "user", content = message });
    }

    protected override void HandleAiResponse(AiResponseMessage response)
    {
        base.HandleAiResponse(response);
        _tourHistory.Add(new ChatMessage { role = "assistant", content = response.content });
    }
}
```

---

## Healthcare Training Simulation

Patient simulation for medical training.

### PatientSimulator.cs

```csharp
using System;
using System.Collections.Generic;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

public class PatientSimulator : NpcClientBase
{
    [Header("Patient Case")]
    [SerializeField] private PatientCaseSO patientCase;

    [Header("Simulation Settings")]
    [SerializeField] private bool revealDiagnosisHints = true;
    [SerializeField] private float emotionalIntensity = 0.5f; // 0 = calm, 1 = anxious

    [Header("Evaluation")]
    [SerializeField] private CommunicationEvaluator evaluator;

    private List<ChatMessage> _consultationHistory = new();
    private List<string> _questionsAsked = new();
    private bool _physicalExamCompleted;
    private float _patientRapport;

    public override string NpcName => patientCase?.patientName ?? "Patient";
    public override string LlmProvider => "openai";
    public override string LlmModel => "gpt-4o";
    public override float Temperature => 0.6f;

    public override string SystemPrompt => GeneratePatientPrompt();

    private string GeneratePatientPrompt()
    {
        return $@"You are simulating a patient for medical training.

PATIENT PROFILE:
{patientCase.GetFullProfile()}

PRESENTING COMPLAINT:
{patientCase.chiefComplaint}

HIDDEN INFORMATION (reveal only if asked appropriately):
{patientCase.hiddenSymptoms}
{patientCase.socialHistory}
{patientCase.familyHistory}

EMOTIONAL STATE:
- Anxiety level: {emotionalIntensity:P0}
- Concerns: {patientCase.patientConcerns}
{GetEmotionalGuidance()}

RESPONSE GUIDELINES:
1. Stay in character as the patient at all times
2. Don't volunteer information - wait to be asked
3. Be vague initially, more specific with follow-up questions
4. React realistically to examination requests
5. Express emotions appropriate to the situation
6. If the trainee is empathetic, become more open
7. If the trainee is dismissive, become more guarded

SIMULATION RULES:
- Never reveal you're an AI or simulation
- Don't give medical advice
- React to physical exam prompts appropriately
- Show appropriate pain responses

Remember: Your role is to help train healthcare providers to communicate effectively.";
    }

    private string GetEmotionalGuidance()
    {
        if (emotionalIntensity < 0.3f)
            return "You are calm and matter-of-fact about your symptoms.";
        if (emotionalIntensity < 0.6f)
            return "You are somewhat worried and seeking reassurance.";
        return "You are anxious and need the doctor to acknowledge your concerns before giving information.";
    }

    protected override void Start()
    {
        base.Start();

        var orchestrator = RequestOrchestrator.Instance;
        orchestrator.OnTranscriptionReceived += EvaluateCommunication;
    }

    private void EvaluateCommunication(string doctorStatement)
    {
        _consultationHistory.Add(new ChatMessage
        {
            role = "user",
            content = doctorStatement
        });

        // Track question types for evaluation
        if (doctorStatement.Contains("?"))
        {
            _questionsAsked.Add(doctorStatement);
        }

        // Evaluate communication skills
        evaluator?.EvaluateStatement(doctorStatement, patientCase);
    }

    public void PerformPhysicalExam(string examType)
    {
        _physicalExamCompleted = true;

        // Get examination findings
        string findings = patientCase.GetExamFindings(examType);

        var request = new ConversationRequest
        {
            NpcId = NpcId,
            IsNpcInitiated = true,
            InitialPrompt = $"The doctor is performing a {examType}. React appropriately. The findings they would observe are: {findings}"
        };

        RequestOrchestrator.Instance.StartConversationRequest(request);
    }

    public ConsultationSummary GetConsultationSummary()
    {
        return new ConsultationSummary
        {
            QuestionsAsked = _questionsAsked,
            PhysicalExamCompleted = _physicalExamCompleted,
            PatientRapport = _patientRapport,
            ConsultationHistory = _consultationHistory,
            EvaluationScore = evaluator?.GetScore() ?? 0f
        };
    }

    public override List<ChatMessage> GetApiHistoryAsChatMessages()
    {
        return _consultationHistory;
    }

    public void StartConsultation()
    {
        _consultationHistory.Clear();
        _questionsAsked.Clear();
        _physicalExamCompleted = false;
        _patientRapport = 0.5f;

        // Patient arrives with chief complaint
        var request = new ConversationRequest
        {
            NpcId = NpcId,
            IsNpcInitiated = true,
            InitialPrompt = $"The doctor just entered. Greet them briefly and mention your main concern: {patientCase.chiefComplaint}"
        };

        RequestOrchestrator.Instance.StartConversationRequest(request);
    }
}

[Serializable]
public class ConsultationSummary
{
    public List<string> QuestionsAsked;
    public bool PhysicalExamCompleted;
    public float PatientRapport;
    public List<ChatMessage> ConsultationHistory;
    public float EvaluationScore;
}
```

---

## Summary

These examples demonstrate AI Bridge's flexibility across different domains:

| Example | Key Features |
|---------|-------------|
| Basic NPC | Simple conversation, history management |
| VR Training | XR input, evaluation, structured scenarios |
| Customer Service | Knowledge base integration, escalation |
| Multi-NPC | Dynamic targeting, NPC management |
| Language Learning | Educational design, progress tracking |
| Museum Guide | Context-aware, exhibit navigation |
| Healthcare | Realistic simulation, communication evaluation |

For more implementation details, see:
- [Getting Started](GettingStarted.md) - Basic setup
- [Best Practices](BestPractices.md) - Production patterns
- [API Reference](API-Reference.md) - Complete API documentation
