using Tsc.AIBridge.Messages;
using UnityEngine;

namespace Tsc.AIBridge.Input
{
    /// <summary>
    /// Process-lifetime holder for the player's vocal-delivery baseline, scoped to ONE LESSON.
    ///
    /// WHY this exists instead of a plain field on <see cref="SpeechInputHandler"/>: that component sits on
    /// the course "System" prefab, and every Navigator.LoadLevel / LoadMenu is a LoadSceneMode.Single load,
    /// so it is DESTROYED on every attempt. A lesson runs several attempts of only a few player turns each,
    /// so a per-instance baseline restarted before it could ever become statistically usable — field data
    /// (2026-08-03) shows runs of all-zero z-scores alternating with absurd ones computed against a
    /// two-sample reference.
    ///
    /// SCOPE = LESSON, enforced here rather than trusted to a caller: <see cref="EnsureLessonScope"/> drops
    /// the state as soon as a different lesson is seen, so the reference cannot leak across lessons or
    /// courses via an entry path that forgot to reset. In memory only — never persisted to disk, and only
    /// ever leaves the client as the carried state on SessionStart.
    ///
    /// PRIVACY: this state is derived from a specific player's voice, so it must not outlive that player.
    /// A lesson change is handled here; call <see cref="Reset"/> on logout or player change.
    /// </summary>
    public static class ProsodyBaselineStore
    {
        private static ProsodyBaselineState _state = new ProsodyBaselineState();
        private static string _lessonId = string.Empty;

        /// <summary>The current lesson's baseline. Never null.</summary>
        public static ProsodyBaselineState State
        {
            get => _state;
            set => _state = value ?? new ProsodyBaselineState();
        }

        /// <summary>
        /// Drop the baseline when the lesson changes. An empty/unknown lessonId does NOT change scope:
        /// AI-coach conversations carry no lesson, and must not wipe a running lesson's reference.
        /// </summary>
        public static void EnsureLessonScope(string lessonId)
        {
            if (string.IsNullOrEmpty(lessonId) || lessonId == _lessonId) return;

            _lessonId = lessonId;
            _state = new ProsodyBaselineState();
            Debug.Log($"[Prosody] Vocal baseline reset — new lesson scope '{lessonId}'.");
        }

        /// <summary>Drop the baseline and its lesson scope. Call on logout / player change.</summary>
        public static void Reset()
        {
            _state = new ProsodyBaselineState();
            _lessonId = string.Empty;
        }
    }
}
