namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Pure decision function for when a safety-net-deferred audio teardown should end.
    /// </summary>
    /// <remarks>
    /// Companion to <see cref="StreamEndDecision"/>. When <c>StreamEndDecision</c> returns
    /// <c>false</c> (defer teardown), <c>NpcClientBase</c> schedules a coroutine that polls
    /// this function each frame to decide when to actually run the deferred teardown.
    ///
    /// <para>Production rationale — incident 2026-05-18 13:58:</para>
    /// v1.17.3 introduced the defer but had no end condition. When the server's
    /// <c>AudioStreamEnd</c> arrived 2s after the safety-net trigger nobody ran the
    /// teardown, so <c>_receivedStreamCount</c> stayed &gt; 0 and the next user turn's
    /// first OGG header was misclassified as "additional OGG header, part of ongoing
    /// stream" — no fresh <c>StartAudioStream</c>, decoder still vervuild, player still
    /// in <c>_forceStop=true</c> zombie state. Result: next turn was completely silent.
    ///
    /// <para>The fix: end the defer on whichever happens first:</para>
    /// <list type="number">
    /// <item><description>Server signals end-of-stream (clean path).</description></item>
    /// <item><description>Hard timeout passes (server crash safety net).</description></item>
    /// </list>
    /// </remarks>
    public static class DeferExpiry
    {
        /// <summary>
        /// Default maximum defer window in seconds before forcing teardown when no server
        /// signal arrives. 5 seconds covers typical Voxtral chunk-rate dips (observed ≤ 3s
        /// in production sessions) with comfortable margin, while preventing the stream
        /// state from leaking indefinitely if the backend crashes mid-response.
        /// </summary>
        public const float DefaultMaxDeferSeconds = 5f;

        /// <summary>
        /// Decides whether a deferred teardown should end on this frame.
        /// </summary>
        /// <param name="elapsedSeconds">Seconds since the defer started.</param>
        /// <param name="serverStreamEnd">
        /// True once <see cref="Audio.Playback.StreamingAudioPlayer.MarkServerStreamEnd"/>
        /// has been called for this turn (server emitted its <c>AudioStreamEnd</c>).
        /// </param>
        /// <param name="maxDeferSeconds">
        /// Hard upper bound on how long the defer may last. Defaults to
        /// <see cref="DefaultMaxDeferSeconds"/>; tests can pass other values.
        /// </param>
        /// <returns>
        /// True → run the deferred teardown now. False → keep waiting.
        /// </returns>
        public static bool ShouldEndDefer(float elapsedSeconds, bool serverStreamEnd, float maxDeferSeconds = DefaultMaxDeferSeconds)
            => serverStreamEnd || elapsedSeconds >= maxDeferSeconds;
    }
}
