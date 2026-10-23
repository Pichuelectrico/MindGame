using System.Threading.Tasks;
using Game.AI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    public sealed class LlmBrainTests
    {
        private static Game.Actions.ActionDefinition Def(string id, bool needsTarget,
            float stamina, int role, string fallback, params string[] aliases)
        {
            var def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("needsTarget").boolValue = needsTarget;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("roleRestriction").intValue = role;
            so.FindProperty("fallbackActionId").stringValue = fallback;
            var ap = so.FindProperty("aliases");
            ap.arraySize = aliases.Length;
            for (int i = 0; i < aliases.Length; i++)
                ap.GetArrayElementAtIndex(i).stringValue = aliases[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        // Def + real range (F35: the tactical prompt lines carry the def's Range).
        private static Game.Actions.ActionDefinition DefWithRange(string id, bool needsTarget,
            float stamina, int role, string fallback, float range)
        {
            var def = Def(id, needsTarget, stamina, role, fallback);
            var so = new SerializedObject(def);
            so.FindProperty("range").floatValue = range;
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static Game.Actions.ActionManifest Manifest()
        {            var m = ScriptableObject.CreateInstance<Game.Actions.ActionManifest>();
            var defs = new[]
            {
                Def("Idle", false, 0f, 0, "Idle", "idle"),
                Def("IdleFight", false, 0f, 0, "Idle", "guard"),
                Def("Dodge", false, 15f, 0, "Idle", "dodge", "esquiva"),
                Def("AttackSword", true, 12f, 0, "Idle", "attack", "ataca"),
                Def("BossDodge", false, 15f, 1, "IdleFight", "boss dodge", "esquiva del jefe"),
            };
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            p.arraySize = defs.Length;
            for (int i = 0; i < defs.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        [Test]
        public void ValidatePlan_DiscardsUnknown_KeepsCanonical()
        {
            var plan = LlmPlanResponse.FromJson(
                "{\"actions\":[{\"type\":\"Bogus\"},{\"type\":\"ataca\",\"target\":\"left\"},{\"type\":\"\"}]}");
            string json = LlmBrain.ValidatePlan(plan, Manifest());
            Assert.AreEqual("{\"actions\":[{\"type\":\"AttackSword\",\"target\":\"left\"}]}", json);
        }

        [Test]
        public void ValidatePlan_EmptyBecomesNull()
        {
            var plan = LlmPlanResponse.FromJson("{\"actions\":[{\"type\":\"Nope\"}]}");
            Assert.IsNull(LlmBrain.ValidatePlan(plan, Manifest()));
            Assert.IsNull(LlmBrain.ValidatePlan(null, Manifest()));
        }

        [Test]
        public async Task DecideAsync_TransportFailure_YieldsNull()
        {
            var brain = new LlmBrain();
            var config = ScriptableObject.CreateInstance<LlmConfig>();
            var cso = new SerializedObject(config);
            cso.FindProperty("retryBackoffMs").intValue = 0;
            cso.ApplyModifiedPropertiesWithoutUndo();
            var t = new FakeTransport(new TransportResponse { Ok = false, Error = "down" });
            string json = await brain.DecideAsync(new CombatStateSnapshot(), Manifest(), config, t);
            Assert.IsNull(json);
        }

        [Test]
        public async Task DecideAsync_ValidResponse_YieldsCanonicalJson()
        {
            var brain = new LlmBrain();
            var config = ScriptableObject.CreateInstance<LlmConfig>();
            var cso = new SerializedObject(config);
            cso.FindProperty("retryBackoffMs").intValue = 0;
            cso.ApplyModifiedPropertiesWithoutUndo();
            var t = new FakeTransport(new TransportResponse
                { Ok = true, Body = "{\"actions\":[{\"type\":\"Dodge\"}]}" });
            string json = await brain.DecideAsync(new CombatStateSnapshot(), Manifest(), config, t);
            Assert.AreEqual("{\"actions\":[{\"type\":\"Dodge\"}]}", json);
        }

        // [F14.2] DecisionLogger needs the model's literal output: success must
        // carry the raw transport body next to the validated plan.
        [Test]
        public async Task DecideDetailed_SuccessCarriesRawBody()
        {
            var brain = new LlmBrain();
            var config = MakeConfig();
            var t = new FakeTransport(new TransportResponse
            {
                Ok = true,
                Body = "{\"choices\":[{\"message\":{\"content\":\"{\\\"actions\\\":[{\\\"type\\\":\\\"Dodge\\\"}]}\"}}]}"
            });
            var decision = await brain.DecideDetailedAsync(
                new CombatStateSnapshot(), Manifest(), config, t);
            Assert.AreEqual("{\"actions\":[{\"type\":\"Dodge\"}]}", decision.Plan);
            StringAssert.Contains("Dodge", decision.Raw);
        }

        // Invalid plan => null plan (heuristic fallback) BUT raw body survives
        // so the dashboard shows what the model actually said.
        [Test]
        public async Task DecideDetailed_InvalidPlanKeepsRawBody()
        {
            var brain = new LlmBrain();
            var config = MakeConfig();
            var t = new FakeTransport(new TransportResponse { Ok = true, Body = "not json" });
            var decision = await brain.DecideDetailedAsync(
                new CombatStateSnapshot(), Manifest(), config, t);
            Assert.IsNull(decision.Plan);
            Assert.AreEqual("not json", decision.Raw);
        }

        // Transport-level failure: no model output; raw is the diagnostic error (or null when error is empty), plan null.
        [Test]
        public async Task DecideDetailed_TransportFailureRawIsNull()
        {
            var brain = new LlmBrain();
            var config = MakeConfig();
            var t = new FakeTransport(new TransportResponse { Ok = false, Error = "down" });
            var decision = await brain.DecideDetailedAsync(
                new CombatStateSnapshot(), Manifest(), config, t);
            Assert.IsNull(decision.Plan);
            // [lead diag] transport error surfaces as "[transport] <error>" so the fallback
            // warning shows the REAL cause (policy/server/404) instead of "empty response".
            Assert.AreEqual("[transport] down", decision.Raw);
        }

        // [F38-fix][lead] Regression: the F35 multi-line system prompt leaked RAW newlines into
        // the payload JSON → Ollama 400 "invalid character '\n' in string literal" → every boss
        // decision fell back to BotBrain. Escape() must fold all control characters.
        [Test]
        public void BuildPayload_EscapesNewlinesQuotesAndBackslashes()
        {
            var client = new LlmClient(MakeConfig());
            var prompt = "line1\nline2 \"quoted\" back\\slash\ttab";
            var payload = client.BuildPayload(new CombatStateSnapshot(), prompt);
            Assert.IsFalse(payload.Contains("\n"), "raw newline leaked into payload → Ollama 400");
            Assert.IsFalse(payload.Contains("\t"), "raw tab leaked into payload → Ollama 400");
            StringAssert.Contains("line1\\nline2", payload);
            StringAssert.Contains("\\\"quoted\\\"", payload);
            StringAssert.Contains("back\\\\slash\\ttab", payload);
        }

        private static LlmConfig MakeConfig()
        {
            var config = ScriptableObject.CreateInstance<LlmConfig>();
            var cso = new SerializedObject(config);
            cso.FindProperty("retryBackoffMs").intValue = 0;
            cso.FindProperty("endpointUrl").stringValue = "http://localhost:1234/v1/chat";
            cso.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        [Test]
        public void BotBrain_PlayerAttacking_BossTwistsAway()
        {
            var snapshot = new CombatStateSnapshot
            {
                phase = "Executing",
                entities = new[]
                {
                    new EntityState
                    {
                        id = "boss", isBoss = true, isTargetable = true,
                        position = new[] { 0f, 0f, 0f },
                        stamina = 80f, weaponEquipped = false
                    },
                    new EntityState
                    {
                        id = "player", isPlayerAvatar = true, isTargetable = true,
                        position = new[] { 2f, 0f, 0f },
                        currentActionId = "AttackSword", isAttacking = true
                    },
                },
                distances = new[] { new EntityDistance { fromId = "boss", toId = "player", distance = 2f } }
            };
            string json = new BotBrain().Decide(snapshot, Manifest(), "boss");
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossDodge", plan.actions[0].type);
        }

        [Test]
        public void SystemPrompt_MentionsDodgeAndJsonOnly()
        {
            string prompt = LlmBrain.BuildSystemPrompt(new CombatStateSnapshot(), Manifest());
            Assert.IsTrue(prompt.Contains("BossDodge"));
            Assert.IsTrue(prompt.Contains("isAttacking"));
            Assert.IsTrue(prompt.Contains("ONLY that JSON"));
        }

        // [F35+F38] The prompt carries per-action tactical context — canonical
        // one-line tactics for known ids (with the def's real Range), generic
        // "(Xm)" lines for the rest — and F38 shortened every line while
        // keeping the tactical meaning. /no_think must stay at the start and
        // the JSON-only contract at the end.
        [Test]
        public void SystemPrompt_IncludesTacticalDescriptions()
        {
            // Local manifest: one canonical-known id with a real range, one
            // unknown id (generic line), one movement id (range 0 -> no suffix).
            var m = ScriptableObject.CreateInstance<Game.Actions.ActionManifest>();
            var defs = new[]
            {
                DefWithRange("BossHeavyAttack", false, 15f, 1, "IdleFight", 2.5f),
                DefWithRange("BossLaserBeam", false, 30f, 1, "Idle", 12f), // [F46][F49] ranged cast, 12m
                DefWithRange("BossLaserTornado", false, 35f, 1, "Idle", 12f), // [F47][F49] spinning variant, 12m
                DefWithRange("Mystery", false, 0f, 0, "Idle", 1.5f),
                Def("Run", false, 5f, 0, "Idle"),
            };
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            p.arraySize = defs.Length;
            for (int i = 0; i < defs.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();

            string prompt = LlmBrain.BuildSystemPrompt(new CombatStateSnapshot(), m);
            StringAssert.StartsWith("You are the Mutant boss in a tactical duel. /no_think ", prompt);
            // Canonical: fixed tactics + real range from the ActionDefinition.
            StringAssert.Contains("- BossHeavyAttack (2.5m): golpe pesado, windup largo (telegraph rojo), daño alto. Riesgo si el player esquiva.", prompt);
            // [F46] Ranged cast: canonical line with real range. [F49] 12m, [BAL-F1] 3s beam / 4.5s cast.
            StringAssert.Contains("- BossLaserBeam (12m): sustained 3s straight laser beam, 12m range, 4.5s cast (1.5s telegraphed wind-up); use when the player is 6-12m away and you can face them; you cannot move while casting.", prompt);
            // [F47] Spinning variant: canonical line with real range. [F49] 12m + (2,6] window, [BAL-F1] 2x360° over 4s.
            StringAssert.Contains("- BossLaserTornado (12m): spinning laser (two full 360° turns over 4s), 12m range, 5.5s cast (telegraphed wind-up); use when the player circles you at 2-6m; you cannot move while casting.", prompt);
            // Unknown id: generic line with range.
            StringAssert.Contains("- Mystery (1.5m)", prompt);
            // Movement id: no range noise.
            StringAssert.Contains("- Run: cerrar distancia antes de golpear.", prompt);
            // Boss-only hint survives from F14.
            StringAssert.Contains("[boss-only]", prompt);
            // Output contract intact.
            StringAssert.Contains("ONLY that JSON", prompt);
            // [F43] Predator bias rides along (aggressive boss prompt).
            StringAssert.Contains("aggressive apex predator", prompt);
            StringAssert.Contains("NEVER output Idle while the player is within 8m", prompt);
            // [F45] Enrage directive: unconditional (BuildSystemPrompt has no
            // snapshot access) — the wire's "furious":true confirms it live.
            StringAssert.Contains("When you are ENRAGED (furious), attack relentlessly with Heavy attacks and never retreat.", prompt);
            // [F49] Fury = tornado dominance (declarative bias on the prompt).
            StringAssert.Contains("While ENRAGED, strongly prefer BossLaserTornado over BossLaserBeam.", prompt);
            // And the shared manifest still documents its canonical actions.
            string shared = LlmBrain.BuildSystemPrompt(new CombatStateSnapshot(), Manifest());
            StringAssert.Contains("BossDodge: esquiva/retroceso: evita daño un instante, no causa daño.", shared);
        }

        // ===== [F52] Strategist: SECOND minimal prompt ======================

        // Local config with a reachable-looking endpoint (no network used —
        // the transport is scripted; same shape as LlmPlanResponseTests').
        // maxRetries/backoff a cero: determinista y rápido (el backoff default
        // de 500ms cede el hilo en cada retry).
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

        // /no_think stays FIRST (same StartsWith contract as the main prompt).
        [Test]
        public void StrategyPrompt_StartsWithNoThink_AndListsPersonas()
        {
            string p = LlmBrain.BuildStrategyPrompt();
            StringAssert.StartsWith("/no_think", p);
            StringAssert.Contains("Distance", p);
            StringAssert.Contains("Fighter", p);
            StringAssert.Contains("Normal", p);
        }

        // Exactly the 4 context lines: distance, HPs, sword state, orb.
        [Test]
        public void StrategyContext_HasTheFourLines()
        {
            var snap = new CombatStateSnapshot
            {
                phase = "Executing",
                swordState = "InMap",
                entities = new[]
                {
                    new EntityState { id = "boss", isBoss = true, isTargetable = true,
                        position = new[] { 0f, 0f, 0f }, health = 120f, maxHealth = 200f,
                        stamina = 80f, weaponEquipped = false },
                    new EntityState { id = "player", isPlayerAvatar = true, isTargetable = true,
                        position = new[] { 7f, 0f, 0f }, health = 80f, maxHealth = 100f, stamina = 60f },
                },
            };
            string ctx = LlmBrain.BuildStrategyContext(snap);
            StringAssert.Contains("Player distance: 7m", ctx);
            StringAssert.Contains("Boss HP: 120/200", ctx);
            StringAssert.Contains("Player HP: 80/100", ctx);
            StringAssert.Contains("Sword: InMap (boss armed: no)", ctx);
            StringAssert.Contains("Health orb available:", ctx);
        }

        [TestCase("Fighter", BossPersona.Fighter)]
        [TestCase("  fighter  ", BossPersona.Fighter)]
        [TestCase("DISTANCE", BossPersona.Distance)]
        [TestCase("Normal", BossPersona.Normal)]
        [TestCase("banana", BossPersona.Normal)]
        [TestCase("", BossPersona.Normal)]
        [TestCase(null, BossPersona.Normal)]
        public void ParsePersona_ContainsWins_ElseNormal(string raw, BossPersona expected)
        {
            Assert.AreEqual(expected, LlmBrain.ParsePersona(raw));
        }

        // A verbose reply naming two personas resolves to the FIRST mention.
        [Test]
        public void ParsePersona_EarliestMentionWins()
        {
            Assert.AreEqual(BossPersona.Fighter,
                LlmBrain.ParsePersona("The boss should be a Fighter, not Distance."));
            Assert.AreEqual(BossPersona.Distance,
                LlmBrain.ParsePersona("Go Distance mode; forget Fighter."));
        }

        // No config (transport down / unwired) -> Normal, zero transport calls.
        // [F52-fix] async Task + await (NUNCA GetAwaiter().GetResult()): un
        // bloqueo sincrónico del hilo principal dentro de un test EditMode
        // dead-locka al runner y congela toda la suite.
        [Test]
        public async Task StrategyAsync_NoConfig_ReturnsNormal()
        {
            var brain = new LlmBrain();
            var persona = await brain.DecideStrategyAsync(null, null);
            Assert.AreEqual(BossPersona.Normal, persona);
            Assert.AreEqual(0, brain.DetailedCallCount);
            Assert.AreEqual(1, brain.StrategyCallCount);
        }

        // A one-word answer parses through a scripted transport.
        [Test]
        public async Task StrategyAsync_OneWordAnswer_Parses()
        {
            var cfg = TestConfig();
            var t = new FakeTransport(new TransportResponse
            {
                Ok = true,
                Body = "{\"choices\":[{\"message\":{\"content\":\"Fighter\"}}]}"
            });
            var persona = await new LlmBrain()
                .DecideStrategyAsync(new CombatStateSnapshot(), cfg, t);
            Assert.AreEqual(BossPersona.Fighter, persona);
        }

        // Garbage body (no chat envelope) -> Normal, never throws.
        [Test]
        public async Task StrategyAsync_GarbageBody_ReturnsNormal()
        {
            var cfg = TestConfig();
            var t = new FakeTransport(new TransportResponse
            {
                Ok = true,
                Body = "<html>504 Gateway Timeout</html>"
            });
            var persona = await new LlmBrain()
                .DecideStrategyAsync(new CombatStateSnapshot(), cfg, t);
            Assert.AreEqual(BossPersona.Normal, persona);
        }
    }
}
