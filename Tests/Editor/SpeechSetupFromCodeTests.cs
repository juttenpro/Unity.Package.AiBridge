using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tsc.AIBridge.Input;
using Tsc.AIBridge.WebSocket;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// WHY: an app that keeps its speech settings in its own data (the Assistant Platform) could only reach the custom
    /// vocabulary, the VAD silence and the base URL through private Inspector fields. 5.20.0 adds setters; the defaults
    /// stay as before, so the Training Platform notices nothing.
    ///
    /// NOT COVERED: the mapping of the boost into SessionStart inside RequestOrchestrator (built inline in the send
    /// coroutine, needs a real session), and that SetApiBaseUrl throws after Start (needs Play mode).
    /// </summary>
    [TestFixture]
    public class SpeechSetupFromCodeTests
    {
        readonly List<GameObject> _objects = new();

        T Create<T>() where T : Component
        {
            var go = new GameObject(typeof(T).Name);
            _objects.Add(go);
            return go.AddComponent<T>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
        }

        [Test]
        public void Defaults_AreThoseBefore520()
        {
            var input = Create<SpeechInputHandler>();

            Assert.That(input.CustomVocabularyBoost, Is.EqualTo(10f), "the boost was a fixed 10 before 5.20.0");
            Assert.That(input.VoiceActivationSilenceTimeout, Is.EqualTo(0.8f));
            Assert.That(input.ParsedCustomVocabulary, Is.Null);
        }

        [Test]
        public void SetCustomVocabulary_IsWhatTheTurnSends()
        {
            var input = Create<SpeechInputHandler>();

            input.SetCustomVocabulary(new[] { "Nplate", "romiplostim" }, 15f);

            Assert.That(input.ParsedCustomVocabulary, Is.EqualTo(new[] { "Nplate", "romiplostim" }),
                "the vocabulary set from code must be exactly what SessionStart sends");
            Assert.That(input.CustomVocabularyBoost, Is.EqualTo(15f));
        }

        [Test]
        public void SetCustomVocabulary_Empty_SendsNone()
        {
            var input = Create<SpeechInputHandler>();
            input.SetCustomVocabulary(new[] { "Nplate" }, 10f);

            input.SetCustomVocabulary(Array.Empty<string>(), 10f);

            Assert.That(input.ParsedCustomVocabulary, Is.Null);
        }

        /// <summary>A comma inside a word would be split into two words without a sound.</summary>
        [TestCase("Nplate, romiplostim")]
        [TestCase("a;b")]
        [TestCase("a\nb")]
        [TestCase(" ")]
        public void SetCustomVocabulary_WordWithSeparatorOrBlank_Throws(string word)
        {
            var input = Create<SpeechInputHandler>();

            Assert.Throws<ArgumentException>(() => input.SetCustomVocabulary(new[] { word }, 10f));
            Assert.That(input.ParsedCustomVocabulary, Is.Null, "a refused vocabulary must leave the old one in place");
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        public void SetCustomVocabulary_NonPositiveBoost_Throws(float boost)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Create<SpeechInputHandler>().SetCustomVocabulary(new[] { "Nplate" }, boost));
        }

        [Test]
        public void SetVoiceActivationSilenceTimeout_WithinInspectorRange_IsUsed()
        {
            var input = Create<SpeechInputHandler>();

            input.SetVoiceActivationSilenceTimeout(0.5f);

            Assert.That(input.VoiceActivationSilenceTimeout, Is.EqualTo(0.5f));
        }

        [TestCase(0.29f)]
        [TestCase(2.01f)]
        [TestCase(float.NaN)]
        public void SetVoiceActivationSilenceTimeout_OutsideRange_Throws(float seconds)
        {
            var input = Create<SpeechInputHandler>();

            Assert.Throws<ArgumentOutOfRangeException>(() => input.SetVoiceActivationSilenceTimeout(seconds));
            Assert.That(input.VoiceActivationSilenceTimeout, Is.EqualTo(0.8f));
        }

        [Test]
        public void SetApiBaseUrl_BeforeStart_IsUsedWithoutTrailingSlash()
        {
            var client = Create<WebSocketClient>();

            client.SetApiBaseUrl("https://orchestrator.example.test/");

            Assert.That(client.ApiBaseUrl, Is.EqualTo("https://orchestrator.example.test"),
                "a trailing slash would make //api/auth/token");
        }

        [TestCase("")]
        [TestCase("not a url")]
        [TestCase("ftp://orchestrator.example.test")]
        public void SetApiBaseUrl_NotHttp_Throws(string url)
        {
            var client = Create<WebSocketClient>();
            var before = client.ApiBaseUrl;

            Assert.Throws<ArgumentException>(() => client.SetApiBaseUrl(url));
            Assert.That(client.ApiBaseUrl, Is.EqualTo(before));
        }
    }
}
