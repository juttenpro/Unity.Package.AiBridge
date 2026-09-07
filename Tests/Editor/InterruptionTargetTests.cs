using NUnit.Framework;
using Tsc.AIBridge.Audio.Interruption;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: an interruption is about the NPC the player is addressing. Every part of the
    /// decision — whether that NPC is speaking, whether it may be interrupted, how long the overlap must
    /// last, and whose audio is stopped — has to be about that one NPC.
    ///
    /// WHY: the manager only knew the NPC that RequestOrchestrator had most recently started a request
    /// for, which is a usable proxy only while a room holds a single NPC. Once NPCs take turns among
    /// themselves — the OR simulation's stress scenario — that is a bystander, and then:
    ///   - "is the NPC responding?" asked about the wrong NPC
    ///   - AllowInterruption and InterruptionPersistenceTime came from the wrong PersonaSO, so a persona
    ///     configured as non-interruptible could shield an unrelated one, or expose it
    ///   - an approved interruption called StopAudio() on the bystander, silencing the wrong NPC while
    ///     the one being talked over kept going
    ///
    /// WHAT: the addressed NPC decides. The request NPC remains the fallback so scenes that never set an
    /// addressee (menu coach, no camera target) behave exactly as before, and a scene-load timing gap
    /// still degrades to "interruption allowed" rather than blocking it.
    /// </summary>
    [TestFixture]
    public class InterruptionTargetTests
    {
        private const float FallbackPersistence = 0.4f;

        [Test]
        public void AddressedNpc_DecidesOverTheRequestNpc()
        {
            var addressed = new InterruptionTarget(allowInterruption: false, persistenceTime: 1.2f);
            var fromRequest = new InterruptionTarget(allowInterruption: true, persistenceTime: 0.2f);

            var target = InterruptionManager.ResolveInterruptionTarget(addressed, fromRequest, FallbackPersistence);

            Assert.IsFalse(target.AllowInterruption,
                "The NPC being talked over is non-interruptible; a bystander's setting must not override that.");
            Assert.AreEqual(1.2f, target.PersistenceTime, 0.0001f);
        }

        [Test]
        public void WithoutAnAddressedNpc_TheRequestNpcDecides()
        {
            var fromRequest = new InterruptionTarget(allowInterruption: false, persistenceTime: 0.9f);

            var target = InterruptionManager.ResolveInterruptionTarget(
                InterruptionTarget.None, fromRequest, FallbackPersistence);

            Assert.IsFalse(target.AllowInterruption, "Menu coach and no-camera-target paths keep the old behaviour.");
            Assert.AreEqual(0.9f, target.PersistenceTime, 0.0001f);
        }

        [Test]
        public void WithNeitherNpc_InterruptionStaysAllowedOnTheFallback()
        {
            var target = InterruptionManager.ResolveInterruptionTarget(
                InterruptionTarget.None, InterruptionTarget.None, FallbackPersistence);

            Assert.IsTrue(target.AllowInterruption,
                "A scene-load timing gap must degrade to 'interruptible', never to a silently unbreakable NPC.");
            Assert.AreEqual(FallbackPersistence, target.PersistenceTime, 0.0001f,
                "Matches the PersonaSO default; the pre-v1.6.16 value made interruption 3.75x harder.");
        }

        [Test]
        public void TheResolvedTargetIsAlwaysUsable()
        {
            Assert.IsTrue(InterruptionManager.ResolveInterruptionTarget(
                InterruptionTarget.None, InterruptionTarget.None, FallbackPersistence).IsPresent,
                "Callers must never have to null-check the outcome.");
        }

        [Test]
        public void None_CarriesNoSettings()
        {
            Assert.IsFalse(InterruptionTarget.None.IsPresent);
        }

        [Test]
        public void AnAddressedNpcThatForbidsInterruption_IsNotOverriddenByAPermissiveFallback()
        {
            // Regression guard for the shape of the bug: the addressed NPC is present, so nothing further
            // down the chain may be consulted at all.
            var addressed = new InterruptionTarget(allowInterruption: false, persistenceTime: 0.5f);

            var target = InterruptionManager.ResolveInterruptionTarget(addressed, InterruptionTarget.None, 99f);

            Assert.IsFalse(target.AllowInterruption);
            Assert.AreEqual(0.5f, target.PersistenceTime, 0.0001f);
        }
    }
}
