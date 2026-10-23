using Game.AI;
using Game.Actions;
using Game.Combat;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // [F52] Boss decision modes: "\" cycling (public CycleMode — the keyboard
    // path is the exact same call), ForcedHeuristic gating (ZERO LLM calls
    // even with a usable config) and the Strategist persona cache (transport
    // down -> Normal + heuristic runs; re-ask cadence). EditMode only.
    public sealed class BossDecisionModeTests
    {
        [SetUp]
        public void SilenceDecisionLogger()
        {
            DecisionLogger.Enabled = false; // RunHeuristic logs; keep test junk out of the jsonl
        }

        [TearDown]
        public void RestoreDecisionLogger()
        {
            DecisionLogger.Enabled = true;
        }

        [Test]
        public void CycleMode_OrderAndWrap()
        {
            var root = new GameObject("boss-cycle");
            try
            {
                var ctrl = root.AddComponent<BotController>();
                Assert.AreEqual(BotController.BossDecisionMode.Llm, ctrl.Mode); // default
                ctrl.CycleMode();
                Assert.AreEqual(BotController.BossDecisionMode.ForcedHeuristic, ctrl.Mode);
                ctrl.CycleMode();
                Assert.AreEqual(BotController.BossDecisionMode.Strategist, ctrl.Mode);
                ctrl.CycleMode();
                Assert.AreEqual(BotController.BossDecisionMode.Llm, ctrl.Mode); // wrap
            }
            finally { Object.DestroyImmediate(root); }
        }

        // [F52] ForcedHeuristic: zero detailed AND zero strategy calls even
        // with a fully usable LlmConfig — the algorithm decides alone.
        [Test]
        public void ForcedHeuristic_MakesZeroLlmCalls()
        {
            var root = new GameObject("boss-forced");
            try
            {
                var ctrl = Wire(root, withConfig: true);
                while (ctrl.Mode != BotController.BossDecisionMode.ForcedHeuristic)
                    ctrl.CycleMode(); // Llm -> ForcedHeuristic
                ctrl.DecideAndRun();
                Assert.AreEqual(0, ctrl.LlmDetailedCalls);
                Assert.AreEqual(0, ctrl.LlmStrategyCalls);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // [F52] Strategist with NO llmConfig (transport down / unwired): the
        // ask resolves to Normal synchronously, the heuristic still runs, and
        // the detailed prompt is never consulted.
        [Test]
        public void Strategist_TransportDown_NormalPersona_HeuristicRuns()
        {
            var root = new GameObject("boss-strat");
            try
            {
                var ctrl = Wire(root, withConfig: false);
                while (ctrl.Mode != BotController.BossDecisionMode.Strategist)
                    ctrl.CycleMode(); // Llm -> ForcedHeuristic -> Strategist
                ctrl.DecideAndRun();
                Assert.AreEqual(BossPersona.Normal, ctrl.Persona);
                Assert.AreEqual(1, ctrl.LlmStrategyCalls);
                Assert.AreEqual(0, ctrl.LlmDetailedCalls);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // [F52] The persona is CACHED: a second decision inside the re-ask
        // window does not ask again (Time.time is constant in EditMode, so
        // both calls land in the same 6s window deterministically).
        [Test]
        public void Strategist_ReaskWithinWindow_OnlyOneStrategyCall()
        {
            var root = new GameObject("boss-strat2");
            try
            {
                var ctrl = Wire(root, withConfig: false);
                while (ctrl.Mode != BotController.BossDecisionMode.Strategist)
                    ctrl.CycleMode();
                ctrl.DecideAndRun();
                ctrl.DecideAndRun();
                Assert.AreEqual(1, ctrl.LlmStrategyCalls);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ===== wiring helpers =============================================

        private static BotController Wire(GameObject root, bool withConfig)
        {
            var healthGo = new GameObject("boss-mode-health");
            var health = healthGo.AddComponent<HealthSystem>();
            var hso = new SerializedObject(health);
            hso.FindProperty("currentHealth").intValue = 200;
            hso.FindProperty("maxHealth").intValue = 200;
            hso.ApplyModifiedPropertiesWithoutUndo();

            var target = new GameObject("boss-mode-target").transform;

            var executor = root.AddComponent<ActionExecutor>();
            var ctrl = root.AddComponent<BotController>();
            ctrl.Bind(Manifest(), executor, health, target, null);

            var cso = new SerializedObject(ctrl);
            cso.FindProperty("llmConfig").objectReferenceValue = withConfig ? TestConfig() : null;
            cso.ApplyModifiedPropertiesWithoutUndo();
            return ctrl;
        }

        private static LlmConfig TestConfig()
        {
            var c = ScriptableObject.CreateInstance<LlmConfig>();
            var so = new SerializedObject(c);
            so.FindProperty("endpointUrl").stringValue = "http://127.0.0.1:9/v1/chat";
            so.ApplyModifiedPropertiesWithoutUndo();
            return c;
        }

        private static ActionManifest Manifest()
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            void Add(string id, float range, float stamina, bool weapon, int role)
            {
                int idx = p.arraySize++;
                var def = ScriptableObject.CreateInstance<ActionDefinition>();
                var dso = new SerializedObject(def);
                dso.FindProperty("actionId").stringValue = id;
                dso.FindProperty("range").floatValue = range;
                dso.FindProperty("staminaCost").floatValue = stamina;
                dso.FindProperty("requiresWeapon").boolValue = weapon;
                dso.FindProperty("needsTarget").boolValue = false;
                dso.FindProperty("roleRestriction").intValue = role;
                dso.FindProperty("clipName").stringValue = "Idle";
                dso.FindProperty("fallbackActionId").stringValue = "IdleFight";
                dso.ApplyModifiedPropertiesWithoutUndo();
                p.GetArrayElementAtIndex(idx).objectReferenceValue = def;
            }
            Add("IdleFight", 0f, 0f, false, 0);
            Add("Run", 0f, 0f, false, 0);
            Add("Walk", 0f, 0f, false, 0);
            Add("BossDodge", 0f, 15f, false, 1);
            Add("BossHeavyAttack", 0f, 25f, false, 1);
            Add("Retreat", 0f, 10f, false, 1);
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }
    }
}
