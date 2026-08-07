using NUnit.Framework;
using Tsc.AIBridge.Core;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Audio stream state is only torn down when the stream is truly done.
    ///
    /// WHY: Production incident 2026-05-18 11:48 — server (Voxtral) was streaming a 78-char
    /// response (~7-8s audio); 1,50s of no data tripped the client safety-net which fired
    /// <c>OnPlaybackComplete</c> while the server was still streaming. The previous code
    /// unconditionally tore down stream state on that event, causing the NEXT chunk's OGG
    /// header to be interpreted as a NEW stream — buffer cleared, decoder reset, ~6s of
    /// remaining audio lost. The aibridge v1.17.2 resume path in
    /// <c>AudioStreamProcessor.ProcessReceivedAudio</c> never got a chance because the
    /// AudioMessageHandler entry point took over first.
    ///
    /// WHAT: <see cref="StreamEndDecision.ShouldTearDownAudioStream"/> encodes the policy:
    /// tear down only on explicit interruption or explicit server end-of-stream signal.
    /// A safety-net timeout alone (no server signal, no interruption) defers teardown.
    ///
    /// SUCCESS CRITERIA (streamIsOpen=true unless stated — a backend stream exists):
    /// - wasInterrupted=true,  serverStreamEnd=false → tear down (real interruption)
    /// - wasInterrupted=true,  serverStreamEnd=true  → tear down
    /// - wasInterrupted=false, serverStreamEnd=true  → tear down (server confirmed end)
    /// - wasInterrupted=false, serverStreamEnd=false → DEFER (safety-net premature)
    /// - streamIsOpen=false                          → tear down (no stream, nothing to wait for)
    ///
    /// WHY streamIsOpen (added 2026-08-07): the same OnPlaybackComplete also fires for locally
    /// played scripted reaction .wav files, where no backend stream is ever opened. Both other
    /// inputs are false there, so the two-input policy deferred and burned its full hard timeout
    /// waiting for a server signal that can never arrive — with a Queue-mode scripted reaction
    /// blocked behind it in NpcAudioPlayer.ProcessAudioQueue. Observed on Quest 2026-08-05
    /// (Leefstijlgesprekken intro): ~6s of silence between two instructions, with
    /// "Total received: 0 samples (0.00s)" and "EndAudioStream called without active stream"
    /// proving no stream existed.
    ///
    /// BUSINESS IMPACT:
    /// - With this decision in place, NpcClientBase.ResetAudioStateForNextTurn keeps
    ///   _isStreamingAudio=true and _receivedStreamCount>0 during a safety-net trigger.
    ///   Late chunks flow naturally into the open stream; no StartAudioStream re-trigger,
    ///   no decoder reset, no buffer wipe. The Voxtral truncation case from 2026-05-18 fits
    ///   the deferred branch and recovers without losing audio.
    /// - Scripted reactions no longer pay for a defer that protects streaming audio only.
    /// </summary>
    [TestFixture]
    public class StreamEndDecisionTests
    {
        /// <summary>
        /// Explicit interruption (user PTT, NPC cancel, shutdown) means there is no
        /// "rest of the stream" to wait for — always tear down regardless of whether the
        /// server's AudioStreamEnd happened to arrive first.
        /// </summary>
        [Test]
        public void Interruption_AlwaysTearsDown_RegardlessOfServerSignal()
        {
            Assert.IsTrue(StreamEndDecision.ShouldTearDownAudioStream(
                wasInterrupted: true, serverStreamEnd: false, streamIsOpen: true));
            Assert.IsTrue(StreamEndDecision.ShouldTearDownAudioStream(
                wasInterrupted: true, serverStreamEnd: true, streamIsOpen: true));
        }

        /// <summary>
        /// Server signalled AudioStreamEnd — the TTS pipeline is fully done streaming.
        /// Safe to tear down: every chunk that will ever arrive has already arrived.
        /// </summary>
        [Test]
        public void ServerStreamEnd_TearsDownOnNaturalCompletion()
        {
            Assert.IsTrue(StreamEndDecision.ShouldTearDownAudioStream(
                wasInterrupted: false, serverStreamEnd: true, streamIsOpen: true));
        }

        /// <summary>
        /// THE REGRESSION FIX: safety-net timeout fired but server has NOT confirmed end.
        /// Late chunks may still be in flight or about to be sent by the TTS provider.
        /// Defer teardown so the in-flight chunks decode into the existing stream rather
        /// than re-triggering StartAudioStream → buffer wipe → audio loss.
        /// </summary>
        [Test]
        public void SafetyNetTimeoutWithoutServerSignal_DefersTearDown()
        {
            Assert.IsFalse(StreamEndDecision.ShouldTearDownAudioStream(
                    wasInterrupted: false, serverStreamEnd: false, streamIsOpen: true),
                "Safety-net timeout alone must NOT tear down — late chunks recovery depends on it. " +
                "Production 2026-05-18 11:48 lost ~6s of Voxtral audio because the old code did.");
        }

        /// <summary>
        /// THE SCRIPTED-AUDIO FIX: a locally played reaction .wav raises the same
        /// OnPlaybackComplete, but no backend stream was ever opened. Deferring there waits the
        /// full hard timeout for a server signal that can never arrive, and a Queue-mode scripted
        /// reaction stays blocked behind it — the ~6s gap between intro instructions observed on
        /// Quest 2026-08-05.
        /// </summary>
        [Test]
        public void NoStreamOpen_TearsDownImmediately()
        {
            Assert.IsTrue(StreamEndDecision.ShouldTearDownAudioStream(
                    wasInterrupted: false, serverStreamEnd: false, streamIsOpen: false),
                "Without an open backend stream there are no late chunks to wait for. " +
                "Deferring here only delays the next scripted reaction.");
        }

        /// <summary>
        /// Guards the boundary between the two fixes: the ONLY difference between the deferring
        /// case and the tearing-down case is whether a stream is open. If a future change makes
        /// streamIsOpen dominate, the 2026-05-18 late-chunk recovery silently dies.
        /// </summary>
        [Test]
        public void OpenStream_IsTheOnlyReasonToDefer()
        {
            Assert.IsFalse(StreamEndDecision.ShouldTearDownAudioStream(
                    wasInterrupted: false, serverStreamEnd: false, streamIsOpen: true),
                "An open stream with no server signal and no interruption is the one and only defer case.");

            Assert.IsTrue(StreamEndDecision.ShouldTearDownAudioStream(
                    wasInterrupted: false, serverStreamEnd: false, streamIsOpen: false),
                "Flipping only streamIsOpen must flip the decision — that is what the new input is for.");
        }
    }
}
