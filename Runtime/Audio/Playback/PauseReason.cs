namespace Tsc.AIBridge.Audio.Playback
{
    /// <summary>
    /// Identifies the source of an audio pause. Multiple sources can pause simultaneously;
    /// audio only resumes when ALL active sources have released their pause.
    ///
    /// Introduced to fix the Quest Home-button bug (audio stuck after focus return): a single
    /// boolean flag couldn't distinguish training-level pauses (PauseManager) from OS-level
    /// pauses (focus loss), so the wrong resume path blocked recovery.
    /// </summary>
    public enum PauseReason
    {
        /// <summary>
        /// Training-level pause driven by user action (PauseManager, coach orb, TrainingPause).
        /// The backend is notified via PauseStream to stop TTS generation. Legacy default for
        /// callers that don't specify a reason.
        /// </summary>
        External,

        /// <summary>
        /// OS-level focus loss (Quest Home short press, Alt-Tab in a windowed build).
        /// The app is still running in the background. Backend is NOT notified — the stream
        /// continues and buffers locally via Opus decoder queue, so the NPC can catch up when
        /// focus returns.
        /// </summary>
        OsFocusLoss,

        /// <summary>
        /// OS-level application pause (Quest Home long press, VR headset powered off, Android
        /// backgrounding). More disruptive than focus loss but treated the same way at the audio
        /// layer: no backend notification, resume picks up the queued audio.
        /// </summary>
        OsApplicationPause,

        /// <summary>
        /// Unity Editor pause state (Editor play mode paused by the developer).
        /// Auto-resumes when the developer un-pauses the editor. No backend involvement.
        /// </summary>
        EditorPause
    }
}
