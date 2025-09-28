using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// Unit tests for NpcClientBase functionality
    /// </summary>
    public class NpcClientBaseTests
    {
        private TestNpcClient _testClient;
        private GameObject _gameObject;

        [SetUp]
        public void Setup()
        {
            _gameObject = new GameObject("TestNPC");
            _testClient = _gameObject.AddComponent<TestNpcClient>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void NpcName_ReturnsCorrectValue()
        {
            Assert.AreEqual("TestNPC", _testClient.NpcName);
        }

        [Test]
        public void LastResponseText_StartsAsEmpty()
        {
            Assert.IsEmpty(_testClient.LastResponseText);
        }

        [Test]
        public void LastResponseText_UpdatesCorrectly()
        {
            _testClient.SetResponse("Test response");
            Assert.AreEqual("Test response", _testClient.LastResponseText);
        }

        [UnityTest]
        public IEnumerator OnNpcResponse_EventFires()
        {
            bool eventFired = false;
            string receivedResponse = null;

            _testClient.OnNpcResponse.AddListener((response) =>
            {
                eventFired = true;
                receivedResponse = response;
            });

            _testClient.SetResponse("Event test");

            yield return null; // Wait a frame

            Assert.IsTrue(eventFired, "Event should have fired");
            Assert.AreEqual("Event test", receivedResponse);
        }

        /// <summary>
        /// Test implementation of NpcClientBase
        /// </summary>
        private class TestNpcClient : MonoBehaviour
        {
            public string NpcName => "TestNPC";
            public string LastResponseText { get; private set; } = "";
            public UnityEngine.Events.UnityEvent<string> OnNpcResponse = new UnityEngine.Events.UnityEvent<string>();

            public void SetResponse(string response)
            {
                LastResponseText = response;
                OnNpcResponse?.Invoke(response);
            }
        }
    }
}