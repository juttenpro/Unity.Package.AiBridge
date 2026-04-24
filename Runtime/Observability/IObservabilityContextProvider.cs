using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Observability
{
    /// <summary>
    /// Supplied by the host Unity project (Training Platform, AI Coach, etc.) to populate
    /// the <see cref="ObservabilityContext"/> on outbound WebSocket messages. AIBridge itself
    /// cannot know about TrainingGlobals, LoginController, or ModeManager — those live in
    /// the consuming project. This interface is the seam.
    ///
    /// Called once per outbound message (SessionStart, TextInput, AnalysisRequest,
    /// DirectTTS). Implementations must be cheap and must NOT throw — the observability
    /// path must never break the actual conversation send path.
    ///
    /// Returning <c>null</c> is valid and means "no context to send" — anonymous or
    /// pre-login sessions should do this.
    ///
    /// Privacy gate: the returned context must never contain a UserId. The
    /// <see cref="ObservabilityContext"/> type itself has no UserId field, so this is a
    /// compile-time guarantee; the interface contract makes it explicit.
    /// </summary>
    public interface IObservabilityContextProvider
    {
        /// <summary>
        /// Build the observability context for the current moment. May return null.
        /// Must not throw — exceptions are swallowed by <see cref="AIBridgeObservability"/>
        /// so a provider bug cannot kill a live conversation.
        /// </summary>
        ObservabilityContext GetCurrentContext();
    }
}
