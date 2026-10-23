using Game.Actions;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // [F7] Per-action impact timing: ActionImpact must fire at
    // ActionDefinition.ImpactTimeFraction of the action's duration instead of a
    // global hardcoded 50%. Ticks simulated at 0.1s.
    public sealed class ActionImpactTimingTests
    {
        private static ActionDefinition Def(string id, float duration, float? impactFraction)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("duration").floatValue = duration;
            so.FindProperty("staminaCost").floatValue = 0f;
            so.FindProperty("range").floatValue = 3f;
            so.FindProperty("fallbackActionId").stringValue = "Idle";
            so.FindProperty("isHeavy").boolValue = false;
            if (impactFraction.HasValue)
                so.FindProperty("impactTimeFraction").floatValue = impactFraction.Value;
            // When impactFraction is null we leave the serialized default intact:
            // exactly how existing .asset files (created before the field existed)
            // deserialize — they must keep the legacy 0.5 behavior.
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static ActionManifest Manifest(params ActionDefinition[] defs)
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            p.arraySize = defs.Length;
            for (int i = 0; i < defs.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        // Runs a single-action plan on a fresh executor, ticking 0.1s at a time,
        // and returns the 1-BASED tick number N at which ActionImpact fired:
        // the Tick call whose execution reached the impact moment (contract:
        // N * 0.1s == fraction * duration, inclusive — ActionExecutor sums
        // Elapsed BEFORE the check and compares with >=, so the earliest tick
        // with Elapsed >= impactTime fires, exactly like the legacy 0.5 path).
        private static int ImpactTick(ActionManifest m, string actionId, int maxTicks = 30)
        {
            var go = new GameObject(actionId);
            var exec = go.AddComponent<ActionExecutor>();
            exec.BindDriver(new StubDriver());
            exec.BindManifest(m);
            exec.Stamina = 100f;

            int tick = 0;
            int impactTickN = -1;
            exec.ActionImpact += _ => { if (impactTickN < 0) impactTickN = tick + 1; };

            Assert.IsTrue(exec.LoadPlan("{\"actions\":[{\"type\":\"" + actionId + "\"}]}"),
                "plan for " + actionId + " should load");
            exec.StartPlan();

            while (impactTickN < 0 && tick < maxTicks)
            {
                exec.Tick(0.1f);
                tick++;
            }
            Object.DestroyImmediate(go);
            return impactTickN;
        }

        private static int ImpactTickFor(float? fraction, string id)
        {
            var idle = Def("Idle", 1f, 0.5f);
            var m = fraction.HasValue
                ? Manifest(Def(id, 1f, fraction.Value), idle)
                : Manifest(Def(id, 1f, null), idle);
            return ImpactTick(m, id);
        }

        [Test]
        public void ImpactFraction_03_FiresAt30Percent()
        {
            Assert.AreEqual(3, ImpactTickFor(0.3f, "FastStrike"),
                "1s action with fraction 0.3 must fire ActionImpact on tick 3 (elapsed 0.3s)");
        }

        [Test]
        public void ImpactFraction_07_FiresAt70Percent()
        {
            Assert.AreEqual(7, ImpactTickFor(0.7f, "SlowStrike"),
                "1s action with fraction 0.7 must fire ActionImpact on tick 7 (elapsed 0.7s)");
        }

        [Test]
        public void ImpactFraction_07_ImpactsLaterThan_03()
        {
            int late = ImpactTickFor(0.7f, "LateHit");
            int early = ImpactTickFor(0.3f, "EarlyHit");
            Assert.Greater(late, early, "higher ImpactTimeFraction must impact strictly later");
        }

        [Test]
        public void DefaultFraction_KeepsLegacyHalf()
        {
            // No serialized write: simulates an old .asset without the field.
            Assert.AreEqual(5, ImpactTickFor(null, "LegacyHit"),
                "defaults must keep the legacy 50% behavior (tick 5 of a 1s action)");
        }
    }
}
