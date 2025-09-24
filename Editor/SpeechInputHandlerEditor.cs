using SimulationCrew.AIBridge.Input;
using UnityEditor;
using UnityEngine;

namespace SimulationCrew.AIBridge.Editor
{
    /// <summary>
    /// Custom editor for SpeechInputHandler to provide better Inspector UI.
    /// Disables/enables settings based on toggle states.
    /// </summary>
    [CustomEditor(typeof(SpeechInputHandler))]
    public class SpeechInputHandlerEditor : UnityEditor.Editor
    {
        // Cached serialized properties
        private SerializedProperty _stopRecordingDelay;
        private SerializedProperty _talkButtons;

        // Smart Mic Offset
        private SerializedProperty _useSmartMicOffset;
        private SerializedProperty _smartOffsetMaxDuration;
        private SerializedProperty _silenceThreshold;

        // Voice Activation
        private SerializedProperty _useVoiceActivation;
        private SerializedProperty _voiceActivationPreBufferTime;
        private SerializedProperty _voiceActivationOnsetTime;
        private SerializedProperty _voiceActivationSilenceTimeout;

        // Debug
        private SerializedProperty _enableVerboseLogging;

        private void OnEnable()
        {
            // Cache all properties - only the ones that actually exist
            _talkButtons = serializedObject.FindProperty("talkButtons");
            _stopRecordingDelay = serializedObject.FindProperty("stopRecordingDelay");

            _useSmartMicOffset = serializedObject.FindProperty("useSmartMicOffset");
            _smartOffsetMaxDuration = serializedObject.FindProperty("smartOffsetMaxDuration");
            _silenceThreshold = serializedObject.FindProperty("silenceThreshold");

            _useVoiceActivation = serializedObject.FindProperty("useVoiceActivation");
            _voiceActivationPreBufferTime = serializedObject.FindProperty("voiceActivationPreBufferTime");
            _voiceActivationOnsetTime = serializedObject.FindProperty("voiceActivationOnsetTime");
            _voiceActivationSilenceTimeout = serializedObject.FindProperty("voiceActivationSilenceTimeout");

            _enableVerboseLogging = serializedObject.FindProperty("enableVerboseLogging");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Input Configuration - header komt van [Header] attribute
            if (_talkButtons != null)
                EditorGUILayout.PropertyField(_talkButtons);
            EditorGUILayout.Space();

            // Recording Stop Mode
            EditorGUILayout.LabelField("Recording Stop Mode", EditorStyles.boldLabel);

            if (_useSmartMicOffset != null)
            {
                EditorGUILayout.PropertyField(_useSmartMicOffset, new GUIContent("Use Smart Mic Offset",
                    "When enabled: Uses VAD to detect when user stops speaking\nWhen disabled: Uses fixed delay after PTT release"));

                EditorGUILayout.Space(5);

                // Show different settings based on mode
                if (_useSmartMicOffset.boolValue)
                {
                    // Smart Mic Offset is ON - show VAD settings
                    EditorGUILayout.HelpBox("Smart Mode: Recording stops when silence is detected (max duration limited)", MessageType.Info);

                    EditorGUI.indentLevel++;
                    if (_smartOffsetMaxDuration != null)
                        EditorGUILayout.PropertyField(_smartOffsetMaxDuration, new GUIContent("Max Duration",
                            "Maximum time to wait for silence after PTT release"));
                    if (_silenceThreshold != null)
                        EditorGUILayout.PropertyField(_silenceThreshold, new GUIContent("Silence Threshold",
                            "How long silence must be detected to stop recording"));
                    EditorGUI.indentLevel--;
                }
                else
                {
                    // Smart Mic Offset is OFF - show fixed delay
                    EditorGUILayout.HelpBox("Fixed Mode: Recording stops after a fixed delay", MessageType.Info);

                    EditorGUI.indentLevel++;
                    if (_stopRecordingDelay != null)
                        EditorGUILayout.PropertyField(_stopRecordingDelay, new GUIContent("Stop Delay",
                            "Fixed delay before stopping recording after PTT release"));
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space();
            }

            // Voice Activation Settings - header komt van [Header] attribute
            if (_useVoiceActivation != null)
            {
                EditorGUILayout.PropertyField(_useVoiceActivation);

                // Disable settings when toggle is off
                using (new EditorGUI.DisabledGroupScope(!_useVoiceActivation.boolValue))
                {
                    EditorGUI.indentLevel++;
                    if (_voiceActivationPreBufferTime != null)
                        EditorGUILayout.PropertyField(_voiceActivationPreBufferTime);
                    if (_voiceActivationOnsetTime != null)
                        EditorGUILayout.PropertyField(_voiceActivationOnsetTime);
                    if (_voiceActivationSilenceTimeout != null)
                        EditorGUILayout.PropertyField(_voiceActivationSilenceTimeout);
                    EditorGUI.indentLevel--;
                }

                // Show warning if both are enabled
                if (_useSmartMicOffset != null && _useSmartMicOffset.boolValue && _useVoiceActivation.boolValue)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(
                        "Note: Smart Mic Offset is automatically bypassed when Voice Activation is enabled. " +
                        "Voice Activation handles its own silence detection.",
                        MessageType.Info);
                }
            }

            EditorGUILayout.Space();

            // Debug Settings - header komt van [Header] attribute
            if (_enableVerboseLogging != null)
                EditorGUILayout.PropertyField(_enableVerboseLogging);

            serializedObject.ApplyModifiedProperties();
        }
    }
}