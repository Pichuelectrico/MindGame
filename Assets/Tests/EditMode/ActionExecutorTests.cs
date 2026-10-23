using System.Collections.Generic;
using Game.Actions;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    internal sealed class StubDriver : IAnimationDriver
    {
        public readonly List<string> Played = new List<string>();
        public void Play(string stateName, bool useRootMotion) => Played.Add(stateName);
        public bool HasState(string stateName) => true;
    }

    public sealed class ActionExecutorTests
    {
        private static ActionDefinition Def(string id, float duration, float stamina,
            bool needsWeapon, int role, string fallback, params string[] aliases)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("duration").floatValue = duration;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("requiresWeapon").boolValue = needsWeapon;
            so.FindProperty("roleRestriction").intValue = role;
            so.FindProperty("fallbackActionId").stringValue = fallback;
            var ap = so.FindProperty("aliases");
            ap.arraySize = aliases.Length;
            for (int i = 0; i < aliases.Length; i++)
                ap.GetArrayElementAtIndex(i).stringValue = aliases[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static ActionManifest Manifest()
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var defs = new[]
            {
                Def("Idle", 1f, 0f, false, 0, "Idle", "idle"),
                Def("Dodge", 0.6f, 15f, false, 0, "Roll", "dodge", "esquiva"),
                Def("Roll", 0.9f, 15f, false, 0, "BlockCenter", "roll"),
                Def("BlockCenter", 0.7f, 5f, false, 0, "Idle", "block"),
                Def("AttackSword", 1.3f, 12f, true, 0, "Dodge", "attack", "ataca"),
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

        private static ActionExecutor Executor(out StubDriver driver)
        {
            var go = new GameObject("ExecutorTest");
            var ex = go.AddComponent<ActionExecutor>();
            driver = new StubDriver();
            ex.BindDriver(driver);
            ex.BindManifest(Manifest());
            ex.Stamina = 100f;
            ex.HasWeapon = true;
            return ex;
        }

        [Test]
        public void Queue_RespectsCapacity()
        {
            var q = new ActionQueue(2);
            var def = Def("Idle", 1f, 0f, false, 0, "Idle");
            Assert.IsTrue(q.TryEnqueue(new QueuedAction(def, ActionDirection.None, null)));
            Assert.IsTrue(q.TryEnqueue(new QueuedAction(def, ActionDirection.None, null)));
            Assert.IsFalse(q.TryEnqueue(new QueuedAction(def, ActionDirection.None, null)));
        }

        [Test]
        public void FullPlan_RunsToCompletion()
        {
            var ex = Executor(out var driver);
            bool finished = false;
            ex.PlanFinished += () => finished = true;
            Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Dodge\"},{\"type\":\"Roll\"}]}"));
            ex.StartPlan();
            Assert.IsTrue(ex.IsRunning);
            ex.Tick(1f); // Dodge 0.6 done
            ex.Tick(1f); // Roll 0.9 done
            Assert.IsTrue(finished);
            Assert.IsFalse(ex.IsRunning);
            // FinishPlan snaps back to Idle on completion (hotfix2 contract:
            // movement continuity — the idle crossfade plays in the same tick).
            Assert.AreEqual(new[] { "Dodge", "Roll", "Idle" }, driver.Played.ToArray());
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void MissingWeapon_SubstitutesFallback_WithWarning()
        {
            var ex = Executor(out var driver);
            ex.HasWeapon = false; // AttackSword unavailable -> Dodge (affordable at 100 stamina)
            string from = null, to = null;
            ex.ActionSubstituted += (a, b, r) => { from = a; to = b; };
            Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"AttackSword\",\"target\":\"left\"}]}"));
            ex.StartPlan();
            Assert.AreEqual("AttackSword", from);
            Assert.AreEqual("Dodge", to);
            Assert.AreEqual(new[] { "Dodge" }, driver.Played.ToArray());
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void OversizedPlan_Rejected()
        {
            var ex = Executor(out _);
            Assert.IsFalse(ex.LoadPlan("{\"actions\":[{\"type\":\"Idle\"},{\"type\":\"Idle\"},{\"type\":\"Idle\"},{\"type\":\"Idle\"},{\"type\":\"Idle\"},{\"type\":\"Idle\"},{\"type\":\"Idle\"}]}"));
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void StopPlan_CancelsAndClears()
        {
            var ex = Executor(out _);
            bool finished = false;
            ex.PlanFinished += () => finished = true;
            Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Dodge\"},{\"type\":\"Roll\"}]}"));
            ex.StartPlan();
            ex.Tick(0.1f);
            ex.StopPlan();
            Assert.IsFalse(ex.IsRunning);
            Assert.IsFalse(finished);
            Assert.IsTrue(ex.Queue.IsEmpty);
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void RunAction_DisplacesTransform()
        {
            var ex = Executor(out _);
            Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Idle\"}]}"));
            // Swap manifest def for a Run-like one via direct queue injection.
            ex.Queue.Clear();
            var runDef = Def("Run", 1.2f, 0f, false, 0, "Idle", "run");
            ex.Queue.TryEnqueue(new QueuedAction(runDef, Game.Actions.ActionDirection.None, null));
            ex.StartPlan();
            Vector3 start = ex.transform.position;
            ex.Tick(1.2f); // full Run duration at 3.0 m/s
            float moved = Vector3.Distance(start, ex.transform.position);
            Assert.Greater(moved, 3.0f);
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void PausedExecutor_DoesNotAdvance()
        {
            var ex = Executor(out var driver);
            bool finished = false;
            ex.PlanFinished += () => finished = true;
            Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Dodge\"}]}"));
            ex.StartPlan();
            ex.TickingEnabled = false;
            ex.Tick(10f);
            Assert.IsFalse(finished);
            Assert.IsNotNull(ex.Current);
            ex.TickingEnabled = true;
            ex.Tick(10f);
            Assert.IsTrue(finished);
            Object.DestroyImmediate(ex.gameObject);
        }
    }
}
