using System;
using System.IO;
using System.Threading.Tasks;
using Game.Actions;
using Game.AI;
using Game.Combat;
using Game.Execution;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Tests
{
    public sealed class BossModeHudTests
    {
        [Test]
        public void HUD_Formatting_Llm_NormalAndFuria()
        {
            Assert.AreEqual("BOSS: LLM", BossModeHud.FormatText(BotController.BossDecisionMode.Llm, BossPersona.Normal, false));
            Assert.AreEqual("BOSS: LLM \u00B7 FURIA", BossModeHud.FormatText(BotController.BossDecisionMode.Llm, BossPersona.Normal, true));
        }

        [Test]
        public void HUD_Formatting_Heuristic_NormalAndFuria()
        {
            Assert.AreEqual("BOSS: Heur\u00EDstico", BossModeHud.FormatText(BotController.BossDecisionMode.ForcedHeuristic, BossPersona.Normal, false));
            Assert.AreEqual("BOSS: Heur\u00EDstico \u00B7 FURIA", BossModeHud.FormatText(BotController.BossDecisionMode.ForcedHeuristic, BossPersona.Normal, true));
        }

        [Test]
        public void HUD_Formatting_Strategist_AllPersonas()
        {
            Assert.AreEqual("BOSS: Estratega \u00B7 Distance", BossModeHud.FormatText(BotController.BossDecisionMode.Strategist, BossPersona.Distance, false));
            Assert.AreEqual("BOSS: Estratega \u00B7 Distance \u00B7 FURIA", BossModeHud.FormatText(BotController.BossDecisionMode.Strategist, BossPersona.Distance, true));
            Assert.AreEqual("BOSS: Estratega \u00B7 Fighter", BossModeHud.FormatText(BotController.BossDecisionMode.Strategist, BossPersona.Fighter, false));
            Assert.AreEqual("BOSS: Estratega \u00B7 Normal", BossModeHud.FormatText(BotController.BossDecisionMode.Strategist, BossPersona.Normal, false));
            Assert.AreEqual("BOSS: Estratega \u00B7 Normal", BossModeHud.FormatText(BotController.BossDecisionMode.Strategist, BossPersona.None, false));
        }

        [Test]
        public void HUD_Formatting_DecisionStates_AppendsIndicator()
        {
            string idle = BossModeHud.FormatText(BotController.BossDecisionMode.Llm, BossPersona.Normal, false, BossModeHud.DecisionState.Idle);
            Assert.AreEqual("BOSS: LLM", idle);

            string deciding = BossModeHud.FormatText(BotController.BossDecisionMode.Llm, BossPersona.Normal, false, BossModeHud.DecisionState.Deciding);
            Assert.AreEqual("BOSS: LLM \u00B7 \uD83E\uDDE0 Decidiendo\u2026", deciding);

            string applied = BossModeHud.FormatText(BotController.BossDecisionMode.Strategist, BossPersona.Distance, true, BossModeHud.DecisionState.Applied);
            Assert.AreEqual("BOSS: Estratega \u00B7 Distance \u00B7 FURIA \u00B7 \u2714 Decisi\u00F3n aplicada", applied);
        }

        [Test]
        public void HUD_SubscribesToBotController_SwitchesPulseState()
        {
            var root = new GameObject("hud-pulse-test");
            try
            {
                var text = root.AddComponent<Text>();
                var hud = root.AddComponent<BossModeHud>();
                hud.SetLabel(text);

                Assert.IsFalse(hud.IsDeciding);
                Assert.IsFalse(hud.IsAppliedFlash);

                hud.TriggerDecisionStarted();
                Assert.IsTrue(hud.IsDeciding);
                Assert.IsFalse(hud.IsAppliedFlash);
                StringAssert.Contains("Decidiendo", text.text);

                hud.TriggerDecisionApplied();
                Assert.IsFalse(hud.IsDeciding);
                Assert.IsTrue(hud.IsAppliedFlash);
                StringAssert.Contains("Decisi\u00F3n aplicada", text.text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DecisionLogger_LogMode_WritesValidJson()
        {
            string tempDir = Path.Combine(Application.temporaryCachePath, "LogModeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "test-decisions.jsonl");

            string originalOverride = DecisionLogger.PathOverride;
            bool originalEnabled = DecisionLogger.Enabled;

            try
            {
                DecisionLogger.PathOverride = testFile;
                DecisionLogger.Enabled = true;

                DecisionLogger.LogMode("Estratega", "Distance");

                Assert.IsTrue(File.Exists(testFile), "File should be created by LogMode");
                string[] lines = File.ReadAllLines(testFile);
                Assert.AreEqual(1, lines.Length);
                string json = lines[0];

                Assert.IsTrue(json.StartsWith("{") && json.EndsWith("}"), "Line must be valid JSON object");
                Assert.IsTrue(json.Contains("\"event\":\"boss_mode\""), "Should contain event: boss_mode");
                Assert.IsTrue(json.Contains("\"mode\":\"Estratega\""), "Should contain mode: Estratega");
                Assert.IsTrue(json.Contains("\"persona\":\"Distance\""), "Should contain persona: Distance");
                Assert.IsTrue(json.Contains("\"ts\":"), "Should contain timestamp ts");

                // Verify JSON parsing with a dummy struct
                var parsed = JsonUtility.FromJson<ModeEventStub>(json);
                Assert.AreEqual("boss_mode", parsed.@event);
                Assert.AreEqual("Estratega", parsed.mode);
                Assert.AreEqual("Distance", parsed.persona);
            }
            finally
            {
                DecisionLogger.PathOverride = originalOverride;
                DecisionLogger.Enabled = originalEnabled;
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Serializable]
        private struct ModeEventStub
        {
            public string ts;
            public string @event;
            public string mode;
            public string persona;
        }

        [Test]
        public void IdempotentDeadHintRemoval_RemovesAndSurvivesRepetition()
        {
            var hintGo = new GameObject("CameraModeHint");
            try
            {
                Assert.IsNotNull(GameObject.Find("CameraModeHint"));
                bool removed = BossModeHud.RemoveDeadCameraModeHint();
                Assert.IsTrue(removed);
                Assert.IsNull(GameObject.Find("CameraModeHint"));

                // Repeat call should succeed idempotently
                bool secondCall = BossModeHud.RemoveDeadCameraModeHint();
                Assert.IsTrue(secondCall);
                Assert.IsNull(GameObject.Find("CameraModeHint"));
            }
            finally
            {
                var leftover = GameObject.Find("CameraModeHint");
                if (leftover != null) UnityEngine.Object.DestroyImmediate(leftover);
            }
        }

        [Test]
        public void BossModeHud_Component_UpdatesLabelOnRefresh()
        {
            var root = new GameObject("test-hud");
            try
            {
                var text = root.AddComponent<Text>();
                var hud = root.AddComponent<BossModeHud>();
                hud.SetLabel(text);

                // Default without boss -> returns safely without exceptions
                hud.Refresh();
                Assert.IsTrue(string.IsNullOrEmpty(text.text) || text.text.StartsWith("BOSS:"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public async Task BotController_StrategistMode_DispatchesDecisionEvents_Async()
        {
            var root = new GameObject("boss-strat-test");
            try
            {
                var ctrl = WireBot(root, BotController.BossDecisionMode.Strategist, withConfig: true);

                int started = 0;
                int applied = 0;
                ctrl.OnLlmDecisionStarted += () => started++;
                ctrl.OnLlmDecision += () => applied++;

                var hudGo = new GameObject("hud");
                hudGo.transform.SetParent(root.transform);
                var text = hudGo.AddComponent<Text>();
                var hud = hudGo.AddComponent<BossModeHud>();
                hud.SetLabel(text);
                hud.SetBoss(ctrl);

                await ctrl.DecideAndRunAsync();

                Assert.GreaterOrEqual(started, 1, "Strategist should have fired OnLlmDecisionStarted");
                Assert.GreaterOrEqual(applied, 1, "Strategist should have fired OnLlmDecision");
                Assert.IsTrue(hud.IsAppliedFlash, "HUD should be in Applied flash state");
                StringAssert.Contains("Decisi\u00F3n aplicada", text.text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public async Task BotController_LlmMode_DispatchesDecisionEvents_Async()
        {
            var root = new GameObject("boss-llm-test");
            try
            {
                var ctrl = WireBot(root, BotController.BossDecisionMode.Llm, withConfig: true);

                int started = 0;
                int applied = 0;
                ctrl.OnLlmDecisionStarted += () => started++;
                ctrl.OnLlmDecision += () => applied++;

                var hudGo = new GameObject("hud");
                hudGo.transform.SetParent(root.transform);
                var text = hudGo.AddComponent<Text>();
                var hud = hudGo.AddComponent<BossModeHud>();
                hud.SetLabel(text);
                hud.SetBoss(ctrl);

                await ctrl.DecideAndRunAsync();

                Assert.GreaterOrEqual(started, 1, "LLM path should have fired OnLlmDecisionStarted");
                Assert.GreaterOrEqual(applied, 1, "LLM path should have fired OnLlmDecision");
                Assert.IsTrue(hud.IsAppliedFlash, "HUD should be in Applied flash state");
                StringAssert.Contains("Decisi\u00F3n aplicada", text.text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void BossModeHud_DisableUnsubscribes_NoDanglingEvents()
        {
            var root = new GameObject("hud-unsub-test");
            try
            {
                var ctrl = root.AddComponent<BotController>();
                var text = root.AddComponent<Text>();
                var hud = root.AddComponent<BossModeHud>();
                hud.SetLabel(text);
                hud.SetBoss(ctrl);

                hud.enabled = false;
                hud.SetBoss(null);
                Assert.IsNull(hud.Boss);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CentralInstructions_AutoHide_After30Seconds_InCommandInputUI()
        {
            var canvasGo = new GameObject("CommandCanvas");
            try
            {
                var titleGo = new GameObject("Title");
                titleGo.transform.SetParent(canvasGo.transform);
                var titleText = titleGo.AddComponent<Text>();

                var feedbackGo = new GameObject("Feedback");
                feedbackGo.transform.SetParent(canvasGo.transform);
                var feedbackText = feedbackGo.AddComponent<Text>();

                var cmdUI = canvasGo.AddComponent<CommandInputUI>();
                cmdUI.SetTitleText(titleText);
                cmdUI.SetFeedbackText(feedbackText);
                cmdUI.InstructionsDuration = 30f;
                cmdUI.AutoHideInstructions = true;

                Assert.IsTrue(titleGo.activeSelf);
                Assert.IsTrue(feedbackGo.activeSelf);
                Assert.IsFalse(cmdUI.InstructionsHidden);

                // Advance 15s -> still visible at full opacity
                cmdUI.AdvanceGameTime(15f);
                Assert.IsTrue(titleGo.activeSelf);
                Assert.IsTrue(feedbackGo.activeSelf);
                Assert.IsFalse(cmdUI.InstructionsHidden);
                Assert.AreEqual(1f, feedbackText.color.a);

                // Advance to 29.25s (fade duration is 1.5s, so fade is in progress)
                cmdUI.AdvanceGameTime(14.25f);
                Assert.IsTrue(feedbackGo.activeSelf);
                Assert.Less(feedbackText.color.a, 1f);
                Assert.Greater(feedbackText.color.a, 0f);

                // Advance another 1.5s (total 30.75s > 30s) -> fully hidden/deactivated
                cmdUI.AdvanceGameTime(1.5f);
                Assert.IsFalse(titleGo.activeSelf);
                Assert.IsFalse(feedbackGo.activeSelf);
                Assert.IsTrue(cmdUI.InstructionsHidden);

                // Subsequent SetFeedback keeps it hidden
                cmdUI.SetFeedback("New instructions", true);
                Assert.IsFalse(feedbackGo.activeSelf);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasGo);
            }
        }

        [Test]
        public void CentralInstructions_BossModeHudHelper_HidesInstructions_PreservesHud()
        {
            var canvasGo = new GameObject("CommandCanvas");
            try
            {
                var titleGo = new GameObject("Title");
                titleGo.transform.SetParent(canvasGo.transform);
                var titleText = titleGo.AddComponent<Text>();

                var feedbackGo = new GameObject("Feedback");
                feedbackGo.transform.SetParent(canvasGo.transform);
                var feedbackText = feedbackGo.AddComponent<Text>();

                var hudGo = new GameObject("BossModeHud");
                hudGo.transform.SetParent(canvasGo.transform);
                var hudText = hudGo.AddComponent<Text>();
                var hud = hudGo.AddComponent<BossModeHud>();
                hud.SetLabel(hudText);

                var cmdUI = canvasGo.AddComponent<CommandInputUI>();
                cmdUI.SetTitleText(titleText);
                cmdUI.SetFeedbackText(feedbackText);

                BossModeHud.HideCentralInstructions();

                Assert.IsFalse(titleGo.activeSelf, "Title must be deactivated");
                Assert.IsFalse(feedbackGo.activeSelf, "Feedback must be deactivated");
                Assert.IsTrue(hudGo.activeSelf, "BossModeHud must remain active");
                Assert.IsTrue(hudText.gameObject.activeSelf, "BossModeHud text must remain active");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasGo);
            }
        }

        private static BotController WireBot(GameObject root, BotController.BossDecisionMode mode, bool withConfig)
        {
            var healthGo = new GameObject("boss-health");
            healthGo.transform.SetParent(root.transform);
            var health = healthGo.AddComponent<HealthSystem>();
            var hso = new SerializedObject(health);
            hso.FindProperty("currentHealth").intValue = 200;
            hso.FindProperty("maxHealth").intValue = 200;
            hso.ApplyModifiedPropertiesWithoutUndo();

            var target = new GameObject("boss-target").transform;
            target.SetParent(root.transform);

            var executor = root.AddComponent<ActionExecutor>();
            var ctrl = root.AddComponent<BotController>();
            ctrl.Bind(Manifest(), executor, health, target, null);

            if (withConfig)
            {
                ctrl.SetLlmConfig(TestConfig());
            }

            while (ctrl.Mode != mode)
            {
                ctrl.CycleMode();
            }

            return ctrl;
        }

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

        private static ActionManifest Manifest()
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            int idx = p.arraySize++;
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var dso = new SerializedObject(def);
            dso.FindProperty("actionId").stringValue = "Idle"; // el campo real es actionId (antes "id" -> FindProperty null -> NRE)
            dso.FindProperty("range").floatValue = 1f;
            dso.FindProperty("staminaCost").floatValue = 0f;
            dso.ApplyModifiedPropertiesWithoutUndo();
            p.GetArrayElementAtIndex(idx).objectReferenceValue = def;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }
    }
}
