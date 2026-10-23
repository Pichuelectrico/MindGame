using Game.Actions;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // Regression for the user-reported freeze: the decision wheel could open
    // while an auto-AI plan (or immediate reaction) was still playing
    // (TryOpen only checks the phase, not IsRunning). LoadPlan used to clear
    // the queue but leave IsRunning set, so the follow-up StartPlan() was a
    // no-op -> stuck in Executing with Current == null: player frozen and
    // the wheel unopenable. LoadPlan now cancels any in-flight plan first.
    public sealed class LoadPlanReplacementTests
    {
        private static ActionDefinition Def(string id, float duration)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("duration").floatValue = duration;
            so.FindProperty("staminaCost").floatValue = 0f;
            so.FindProperty("fallbackActionId").stringValue = "Idle";
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static ActionManifest Manifest()
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var defs = new[] { Def("Idle", 1f), Def("Punch", 1.1f) };
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            p.arraySize = defs.Length;
            for (int i = 0; i < defs.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        private static ActionExecutor Executor(out StubDriver driver)
        {
            var go = new GameObject("LoadPlanReplacementTest");
            var ex = go.AddComponent<ActionExecutor>();
            driver = new StubDriver();
            ex.BindDriver(driver);
            ex.BindManifest(Manifest());
            ex.Stamina = 100f;
            return ex;
        }

        [Test]
        public void LoadPlanWhileRunning_CancelledInFlight_NewPlanStartable()
        {
            var ex = Executor(out var driver);
            try
            {
                Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Punch\"},{\"type\":\"Punch\"}]}"));
                ex.StartPlan();
                ex.Tick(0.2f); // mid-swing of the auto-AI plan
                Assert.IsTrue(ex.IsRunning, "precondition: a plan is in flight");

                // Wheel path: EJECUTAR while the old plan is still playing.
                Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Punch\"}]}"),
                    "LoadPlan must succeed over an in-flight plan");
                Assert.IsFalse(ex.IsRunning,
                    "LoadPlan must cancel the in-flight plan (IsRunning was the freeze)");

                ex.StartPlan();
                Assert.IsTrue(ex.IsRunning, "the replacement plan must actually start");
                Assert.AreEqual(2, driver.Played.Count,
                    "old Punch (mid-swing, already played) + replacement Punch");
                ex.Tick(1.3f);
                Assert.IsFalse(ex.IsRunning, "replacement plan must complete");
            }
            finally
            {
                Object.DestroyImmediate(ex.gameObject);
            }
        }
    }
}
