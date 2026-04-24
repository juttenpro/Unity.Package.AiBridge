using System;
using NUnit.Framework;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.Observability;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: The AIBridge package offers a registration seam so the host
    /// project (Training Platform) can inject anonymous observability IDs (AppLogId,
    /// LessonId, CourseId, OrganizationId, AppMode) without the package needing to know
    /// anything about TrainingGlobals, LoginController, or ModeManager.
    ///
    /// WHY: AIBridge ships to other Unity projects too. It cannot take a hard dependency
    /// on the Training Platform's domain types. A tiny static registry + provider
    /// interface is the minimal seam that lets the host project push its context in.
    ///
    /// WHAT: Validates <see cref="AIBridgeObservability"/>: a null provider returns null,
    /// a real provider returns its value, and a throwing provider is swallowed so a
    /// provider bug can never kill a live conversation.
    ///
    /// HOW: Three tests cover the null/healthy/throwing paths. Each test resets the
    /// static <c>Provider</c> in tear-down so tests stay isolated.
    ///
    /// SUCCESS CRITERIA:
    /// - No provider registered → TryGetContext returns null
    /// - Registered provider → TryGetContext returns the provider's context
    /// - Provider throws → TryGetContext returns null without propagating the exception
    ///
    /// BUSINESS IMPACT:
    /// - Falen van null-handling = NullReferenceException crash op hot WebSocket path,
    ///   breekt live conversaties.
    /// - Falen van throwing-path = één bug in een provider-implementatie kan het hele
    ///   platform bricken. Die risk-containment is de reden dat de try/catch bestaat.
    /// </summary>
    [TestFixture]
    public class AIBridgeObservabilityTests
    {
        [TearDown]
        public void ResetProvider()
        {
            // Static registry must be cleared so tests don't leak into each other or into
            // unrelated tests that happen to run afterwards in the same AppDomain.
            AIBridgeObservability.Provider = null;
        }

        [Test]
        public void TryGetContext_WithNoProviderRegistered_ReturnsNull()
        {
            AIBridgeObservability.Provider = null;

            var result = AIBridgeObservability.TryGetContext();

            Assert.That(result, Is.Null,
                "No provider = no observability. Outbound messages must still send with " +
                "a null Observability field (backward compatible with older backends).");
        }

        [Test]
        public void TryGetContext_WithRegisteredProvider_ReturnsItsContext()
        {
            var expected = new ObservabilityContext
            {
                AppLogId = "log-abc",
                OrganizationId = 7,
                CourseId = "Occupationalhealth"
            };
            AIBridgeObservability.Provider = new StubProvider(() => expected);

            var result = AIBridgeObservability.TryGetContext();

            Assert.That(result, Is.SameAs(expected),
                "The registry must forward the provider's exact return value — no copying, " +
                "no fabrication.");
        }

        [Test]
        public void TryGetContext_WhenProviderThrows_ReturnsNullAndDoesNotPropagate()
        {
            AIBridgeObservability.Provider = new StubProvider(
                () => throw new InvalidOperationException("simulated provider bug"));

            // Must NOT throw. A broken provider implementation (e.g. a NullReferenceException
            // on a not-yet-initialised singleton during app startup) must never take down a
            // live conversation. Degraded metrics are acceptable; broken conversations are not.
            ObservabilityContext result = null;
            Assert.DoesNotThrow(() => result = AIBridgeObservability.TryGetContext());
            Assert.That(result, Is.Null);
        }

        [Test]
        public void TryGetContext_WhenProviderReturnsNull_ReturnsNull()
        {
            AIBridgeObservability.Provider = new StubProvider(() => null);

            var result = AIBridgeObservability.TryGetContext();

            Assert.That(result, Is.Null,
                "A provider that deliberately returns null (anonymous / pre-login session) " +
                "must be respected — the registry must not invent a context.");
        }

        private sealed class StubProvider : IObservabilityContextProvider
        {
            private readonly Func<ObservabilityContext> _builder;

            public StubProvider(Func<ObservabilityContext> builder)
            {
                _builder = builder;
            }

            public ObservabilityContext GetCurrentContext()
            {
                return _builder();
            }
        }
    }
}
