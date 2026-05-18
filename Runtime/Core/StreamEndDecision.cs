namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Pure decision function for whether a playback-complete event should tear down the
    /// audio stream state, or whether teardown should be deferred to let late chunks finish.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="NpcClientBase"/> to make the policy unit-testable without
    /// instantiating a MonoBehaviour or simulating a WebSocket session.
    ///
    /// <para>Background — production incident 2026-05-18 11:48:</para>
    /// <list type="number">
    /// <item>Server (Voxtral) was actively streaming a 78-char response (~7-8s audio).</item>
    /// <item>Briefly no chunks arrived for 1,50s → client safety-net timeout fired.</item>
    /// <item><c>StopPlayback</c> → <c>OnPlaybackComplete</c> → <c>ResetAudioStateForNextTurn(false)</c>.</item>
    /// <item>That reset called <c>audioProcessor.EndAudioStream()</c> AND
    /// <c>audioMessageHandler.Reset()</c>, which sets <c>_receivedStreamCount = 0</c>
    /// and resets the Opus decoder.</item>
    /// <item>233ms later the server's next chunk arrived. Its OGG page magic ("OggS") was
    /// detected by <see cref="Handlers.AudioMessageHandler.ProcessBinaryMessage"/>; with
    /// <c>_receivedStreamCount</c> now 0, the chunk was treated as the FIRST OGG header of
    /// a new stream → <c>StartAudioStream</c> was called → buffer cleared, decoder reset
    /// AGAIN. The remaining ~6 seconds of audio that the server kept sending were processed
    /// against a freshly reset stream, but the player perceived it as a tiny new stream
    /// followed immediately by <c>Server signalled end-of-audio-stream</c>.</item>
    /// </list>
    ///
    /// The fix: only tear down stream state when we KNOW the stream is truly done.
    /// "Truly done" = explicit interruption OR explicit server signal
    /// (<see cref="Audio.Playback.StreamingAudioPlayer.IsServerStreamEnd"/>).
    /// A safety-net timeout alone is NOT enough to tear down — late chunks may still arrive.
    ///
    /// <para>Trade-off considered:</para> if a safety-net fires AND no server signal ever arrives
    /// (e.g., server crash), the stream remains "logically open" until the next user turn,
    /// at which point <c>StartAudioStream</c> resets everything cleanly. Acceptable: better
    /// to keep state alive briefly than to truncate real audio.
    /// </remarks>
    public static class StreamEndDecision
    {
        /// <summary>
        /// Decides whether playback-complete should call <c>EndAudioStream</c> +
        /// <c>AudioMessageHandler.Reset</c>, or defer teardown to allow late chunks
        /// to continue decoding into the existing stream.
        /// </summary>
        /// <param name="wasInterrupted">
        /// True if playback was forcibly stopped (user PTT, NPC cancel, shutdown).
        /// Always tear down — there is no "rest of the stream" to wait for.
        /// </param>
        /// <param name="serverStreamEnd">
        /// True if the server has signalled <c>AudioStreamEnd</c> for this turn.
        /// The server only emits that signal after its TTS pipeline has finished,
        /// so this is the authoritative "stream is truly done" indicator.
        /// </param>
        /// <returns>
        /// True → call EndAudioStream + handler Reset (normal teardown).
        /// False → safety-net fired prematurely; keep stream state intact so late
        /// chunks via <see cref="Handlers.AudioMessageHandler.ProcessBinaryMessage"/>
        /// flow into the still-open stream instead of re-triggering StartAudioStream.
        /// </returns>
        public static bool ShouldTearDownAudioStream(bool wasInterrupted, bool serverStreamEnd)
            => wasInterrupted || serverStreamEnd;
    }
}
