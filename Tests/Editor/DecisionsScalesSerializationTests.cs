using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// CONTRACT (DA1, Decisions on Azure EU): an analysis request carries the score scale of every integer variable in
    /// ConversationContext.decisionsScales. The ApiOrchestrator reads exactly this shape; a renamed key or a different
    /// nesting makes every Decisions analysis a configuration error ("no scale for ScaleAssertive"). Without scales (no
    /// Decisions, or no integer variable) the key must be absent.
    ///
    /// The expected JSON is written out literally, so a change on either side shows here and in the backend's twin test.
    ///
    /// NOT COVERED: how the Training Platform finds the scale (its own tests), what the backend checks.
    /// </summary>
    [TestFixture]
    public class DecisionsScalesSerializationTests
    {
        [Test]
        public void Scales_AreSentAsLevelsAndZeroNotAssessable_PerVariableName()
        {
            var context = new ConversationContext
            {
                decisionsScales = new Dictionary<string, DecisionsScale>
                {
                    ["ScaleAssertive"] = new DecisionsScale { Levels = new List<int> { 0, 1, 2, 3, 4, 5 }, ZeroNotAssessable = true },
                    ["ScoreIntroduceYourself"] = new DecisionsScale { Levels = new List<int> { 1, 5 }, ZeroNotAssessable = false },
                }
            };

            var sent = JObject.Parse(JsonConvert.SerializeObject(context))["decisionsScales"];
            var expected = JObject.Parse(
                "{\"ScaleAssertive\":{\"levels\":[0,1,2,3,4,5],\"zeroNotAssessable\":true}," +
                "\"ScoreIntroduceYourself\":{\"levels\":[1,5],\"zeroNotAssessable\":false}}");

            Assert.That(JToken.DeepEquals(sent, expected), Is.True,
                $"the backend reads decisionsScales in exactly this shape; anything else fails every Decisions analysis.\nsent: {sent}");
        }

        [Test]
        public void NoScales_SendsNoKey()
        {
            var context = JObject.Parse(JsonConvert.SerializeObject(new ConversationContext { llmProvider = "azure-openai" }));

            Assert.That(context.ContainsKey("decisionsScales"), Is.False,
                "an analysis without Decisions must not send decisionsScales; the request must stay as before");
        }
    }
}
