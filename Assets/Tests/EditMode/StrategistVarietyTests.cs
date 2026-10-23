using System.Threading.Tasks;
using Game.AI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // [AI-F8][lead] Estratega variety: persona self-awareness in the strategy
    // context, the deterministic variety rotation, and strategist telemetry
    // (LastStrategyRaw / LastStrategyLatencyMs). Written against the
    // "boss stuck in Distance" bug: the small strategist model answered
    // "Distance" on every re-ask and Fighter NEVER appeared in the whole
    // llm-decisions.jsonl history — and the strategist ask itself was a
    // black box (no raw answer, no latency, only boss_mode on change).
    public sealed class StrategistVarietyTests
    {
        // ===== Persona-aware strategy context ==============================

        [Test]
        public void StrategyContext_WithPersona_ShowsCurrentPersonaAndHold()
        {
            string ctx = LlmBrain.BuildStrategyContext(
                new CombatStateSnapshot(), BossPersona.Distance, 14f);
            StringAssert.Contains("Current persona: Distance", ctx);
            StringAssert.Contains("held 14s", ctx);
            StringAssert.Contains("switch to a different persona", ctx);
        }

        [Test]
        public void StrategyContext_WithoutPersona_KeepsLegacyShape()
        {
            // First ask / legacy callers (F52 shape): no persona lines at all.
            string ctx = LlmBrain.BuildStrategyContext(new CombatStateSnapshot());
            StringAssert.DoesNotContain("Current persona", ctx);
        }

        [Test]
        public void StrategyPrompt_KeepsNoThinkFirst_AndAsksForVariety()
        {
            string prompt = LlmBrain.BuildStrategyPrompt();
            StringAssert.StartsWith("/no_think", prompt); // F52 contract intact
            StringAssert.Contains("vary your choice", prompt); // [AI-F8] variety pressure
        }

        [Test]
        public void PersonaWord_None_ShowsAsNormal()
        {
            Assert.AreEqual("Normal", LlmBrain.PersonaWord(BossPersona.None));
            Assert.AreEqual("Distance", LlmBrain.PersonaWord(BossPersona.Distance));
            Assert.AreEqual("Fighter", LlmBrain.PersonaWord(BossPersona.Fighter));
            Assert.AreEqual("Normal", LlmBrain.PersonaWord(BossPersona.Normal));
        }

        // ===== Deterministic variety rotation (hold guard) ==================

        [Test]
        public void NextVarietyPersona_CyclesAllThree()
        {
            // Distance -> Fighter (the never-seen persona gets forced first),
            // Fighter -> Normal, Normal -> Distance. Cycle covers all three.
            Assert.AreEqual(BossPersona.Fighter, LlmBrain.NextVarietyPersona(BossPersona.Distance));
            Assert.AreEqual(BossPersona.Normal, LlmBrain.NextVarietyPersona(BossPersona.Fighter));
            Assert.AreEqual(BossPersona.Distance, LlmBrain.NextVarietyPersona(BossPersona.Normal));
        }

        [Test]
        public void NextVarietyPersona_None_StaysNormal()
        {
            // None (no persona yet — first ask): the guard never fires before
            // a persona exists, so the rotation must not invent one.
            Assert.AreEqual(BossPersona.Normal, LlmBrain.NextVarietyPersona(BossPersona.None));
        }

        // ===== Strategist telemetry (raw answer + latency) ==================

        // Scripted transport: the model answers "Fighter" (chat envelope shape
        // parsed by LlmClient.ExtractContent — same shape as the real Ollama).
        private sealed class FighterTransport : ILlmTransport
        {
            public Task<TransportResponse> PostAsync(string url, string jsonBody,
                string apiKey, float timeoutSeconds)
            {
                return Task.FromResult(new TransportResponse
                {
                    Ok = true,
                    Body = "{\"choices\":[{\"message\":{\"content\":\"Fighter\"}}]}"
                });
            }
        }

        [Test]
        public async Task DecideStrategy_ScriptedFighter_ReturnsFighter_AndExposesRawTelemetry()
        {
            var brain = new LlmBrain();
            var persona = await brain.DecideStrategyAsync(
                new CombatStateSnapshot(), TestConfig(), new FighterTransport());
            Assert.AreEqual(BossPersona.Fighter, persona);
            StringAssert.Contains("Fighter", brain.LastStrategyRaw);
            Assert.That(brain.LastStrategyLatencyMs, Is.GreaterThanOrEqualTo(0L));
        }

        // Same helper shape as LlmBrainTests.TestConfig (endpoint unreachable,
        // zero retries — nothing here touches the real network).
        private static LlmConfig TestConfig()
        {
            var c = ScriptableObject.CreateInstance<LlmConfig>();
            var so = new SerializedObject(c);
            so.FindProperty("endpointUrl").stringValue = "http://127.0.0.1:9/v1/chat";
            so.FindProperty("maxRetries").intValue = 0;
            so.FindProperty("retryBackoffMs").intValue = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            return c;
        }
    }
}
