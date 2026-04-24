using System;
using Tsc.AIBridge.Messages;
using UnityEngine;

namespace Tsc.AIBridge.Observability
{
    /// <summary>
    /// Static registration + lookup point for the observability context provider.
    /// The host Unity project registers its <see cref="IObservabilityContextProvider"/>
    /// implementation once at startup (typically from TrainingInitializer), and AIBridge
    /// pulls the current context from here when building outbound WebSocket messages.
    ///
    /// Why a static registry: <see cref="Core.RequestOrchestrator"/> and friends are
    /// MonoBehaviour singletons created via scene setup, so constructor injection is not
    /// available. A static registry keeps the surface tiny (one property), matches the
    /// existing singleton style in this package, and avoids dragging a DI container in.
    ///
    /// Safety guarantees:
    /// - A null provider is valid — <see cref="TryGetContext"/> returns null, which maps
    ///   to "no observability JSON emitted" (backward compatible with older backends).
    /// - A throwing provider must never break the send path — exceptions from
    ///   <see cref="IObservabilityContextProvider.GetCurrentContext"/> are caught and
    ///   logged, and null is returned.
    ///
    /// Thread model: the property is read once per outbound message on the Unity main
    /// thread. Writes at startup are single-threaded. If a future host project needs
    /// thread-safe swapping at runtime, wrap with <c>volatile</c> — not done today to
    /// keep the surface trivial.
    /// </summary>
    public static class AIBridgeObservability
    {
        /// <summary>
        /// Registered provider, or null when the host project did not register one.
        /// Host project (Training Platform) assigns this once during app startup.
        /// </summary>
        public static IObservabilityContextProvider Provider { get; set; }

        /// <summary>
        /// Returns the current <see cref="ObservabilityContext"/> from the registered
        /// provider, or null if no provider is registered, the provider returns null,
        /// or the provider throws. Never propagates exceptions.
        /// </summary>
        public static ObservabilityContext TryGetContext()
        {
            var provider = Provider;
            if (provider == null)
            {
                return null;
            }

            try
            {
                return provider.GetCurrentContext();
            }
            catch (Exception ex)
            {
                // Log and swallow — a broken observability provider must never cause a
                // conversation to fail. This is the difference between degraded metrics
                // and a visibly broken app.
                Debug.LogWarning(
                    $"[AIBridgeObservability] Provider threw while building context; " +
                    $"message will be sent without observability IDs. Error: {ex.Message}");
                return null;
            }
        }
    }
}
