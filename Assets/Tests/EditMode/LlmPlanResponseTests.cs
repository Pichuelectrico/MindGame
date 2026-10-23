using System.Threading.Tasks;
using Game.AI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    internal sealed class FakeTransport : ILlmTransport
    {
        private readonly TransportResponse[] script;
        public int Calls { get; private set; }
        public string LastBody { get; private set; }

        public FakeTransport(params TransportResponse[] script) { this.script = script; }

        public Task<TransportResponse> PostAsync(string url, string jsonBody, string apiKey, float timeoutSeconds)
        {
            LastBody = jsonBody;
            var r = script[System.Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(r);
        }
    }

    internal sealed class ThrowingTransport : ILlmTransport
    {
        public Task<TransportResponse> PostAsync(string url, string jsonBody, string apiKey, float timeoutSeconds)
        {
            throw new System.Exception("boom");
        }
    }

    public sealed class LlmPlanResponseTests
    {
        private static LlmConfig TestConfig(int retries = 2)
        {
            var c = ScriptableObject.CreateInstance<LlmConfig>();
            // Honor the parameter: without this the config keeps its serialized
            // defaults (maxRetries=2, backoff=500ms) and retry-count asserts lie.
            var so = new UnityEditor.SerializedObject(c);
            so.FindProperty("maxRetries").intValue = retries;
            so.FindProperty("retryBackoffMs").intValue = 0; // keep tests fast
            so.ApplyModifiedPropertiesWithoutUndo();
            return c;
        }

        [Test]
        public void ValidPlan_Deserializes()
        {
            var p = LlmPlanResponse.FromJson(
                "{\"actions\":[{\"type\":\"Dodge\",\"target\":\"left\"},{\"type\":\"Attack\",\"target\":\"center\"}]}");
            Assert.IsTrue(p.IsValid());
            Assert.AreEqual(2, p.actions.Length);
            Assert.AreEqual("Dodge", p.actions[0].type);
            Assert.AreEqual("left", p.actions[0].target);
        }

        [Test]
        public void TooManyActions_IsInvalid()
        {
            var sb = new System.Text.StringBuilder("{\"actions\":[");
            for (int i = 0; i < 7; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"type\":\"Dodge\"}");
            }
            sb.Append("]}");
            Assert.IsFalse(LlmPlanResponse.FromJson(sb.ToString()).IsValid());
        }

        [Test]
        public void EmptyType_And_EmptyBody_AreInvalid()
        {
            Assert.IsFalse(LlmPlanResponse.FromJson("{\"actions\":[{\"type\":\"\"}]}").IsValid());
            Assert.IsFalse(LlmPlanResponse.FromJson("{\"actions\":[]}").IsValid());
            Assert.IsFalse(LlmPlanResponse.FromJson("not json").IsValid());
            Assert.IsFalse(LlmPlanResponse.FromJson(null).IsValid());
        }

        [Test]
        public async Task Client_ReturnsPlan_OnSuccess()
        {
            var t = new FakeTransport(new TransportResponse
                { Ok = true, Body = "{\"actions\":[{\"type\":\"Dodge\"}]}" });
            var client = new LlmClient(TestConfig(), t);
            var r = await client.SendCombatStateAsync(new CombatStateSnapshot(), "sys");
            Assert.IsTrue(r.Success);
            Assert.IsTrue(r.Plan.IsValid());
            Assert.IsTrue(t.LastBody.Contains("\"messages\":["));
            Assert.IsTrue(t.LastBody.Contains("\"role\":\"system\""));
            Assert.IsTrue(t.LastBody.Contains("\"role\":\"user\""));
            Assert.IsTrue(t.LastBody.Contains("sys"));
        }

        [Test]
        public async Task Client_RetriesThenSucceeds()
        {
            var t = new FakeTransport(
                new TransportResponse { Ok = false, Error = "timeout" },
                new TransportResponse { Ok = true, Body = "{\"actions\":[{\"type\":\"Block\"}]}" });
            var client = new LlmClient(TestConfig(), t);
            var r = await client.SendCombatStateAsync(new CombatStateSnapshot(), null);
            Assert.IsTrue(r.Success);
            Assert.AreEqual(2, t.Calls);
        }

        [Test]
        public async Task Client_FailsAfterRetries_WithError_NotException()
        {
            var t = new FakeTransport(new TransportResponse { Ok = false, Error = "down" });
            var client = new LlmClient(TestConfig(retries: 1), t);
            // Client contract: never throws; failure is explicit in the result.
            var r = await client.SendCombatStateAsync(new CombatStateSnapshot(), null);
            Assert.IsFalse(r.Success);
            Assert.IsNotNull(r.Error);
            Assert.AreEqual(2, t.Calls); // 1 initial + 1 retry
        }

        [Test]
        public async Task Client_SurvivesThrowingTransport()
        {
            var client = new LlmClient(TestConfig(), new ThrowingTransport());
            var r = await client.SendCombatStateAsync(new CombatStateSnapshot(), null);
            Assert.IsFalse(r.Success);
        }

        [Test]
        public async Task Client_NullConfig_ReturnsFailure()
        {
            var client = new LlmClient(null, new FakeTransport(
                new TransportResponse { Ok = true, Body = "{\"actions\":[{\"type\":\"Idle\"}]}" }));
            var r = await client.SendCombatStateAsync(new CombatStateSnapshot(), null);
            Assert.IsFalse(r.Success);
        }
    }
}
