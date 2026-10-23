using Game.Actions;
using Game.Combat;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    public sealed class ReactionQueueTests
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
            var defs = new[]
            {
                Def("Idle", 1f), Def("Punch", 1.1f),
                Def("HitReaction", 0.7f), Def("Death", 2.5f),
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
            var go = new GameObject("ReactionTest");
            var ex = go.AddComponent<ActionExecutor>();
            driver = new StubDriver();
            ex.BindDriver(driver);
            ex.BindManifest(Manifest());
            ex.Stamina = 100f;
            return ex;
        }

        [Test]
        public void DamageDuringAction_InterruptsImmediately()
        {
            var ex = Executor(out var driver);
            int finished = 0;
            ex.PlanFinished += () => finished++;
            Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Punch\"},{\"type\":\"Punch\"}]}"));
            ex.StartPlan();
            ex.Tick(0.2f); // mid-swing
            // Director path: stop the running plan, reaction plays NOW.
            ex.StopPlan();
            ex.QueueReaction("HitReaction");
            Assert.AreEqual(new[] { "Punch", "HitReaction" }, driver.Played.ToArray());
            ex.Tick(1.0f); // reaction completes
            Assert.AreEqual(1, finished);
            Assert.GreaterOrEqual(ex.RecoveryUntilTime, UnityEngine.Time.time);
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void DamageWhileIdle_ReactsImmediately()
        {
            var ex = Executor(out var driver);
            ex.QueueReaction("HitReaction");
            Assert.IsTrue(ex.IsRunning);
            Assert.AreEqual(new[] { "HitReaction" }, driver.Played.ToArray());
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void DeathPending_WinsOverHitReaction()
        {
            var ex = Executor(out var driver);
            Assert.IsTrue(ex.LoadPlan("{\"actions\":[{\"type\":\"Punch\"}]}"));
            ex.StartPlan();
            ex.QueueReaction("HitReaction");
            ex.QueueReaction("HitReaction"); // collapse: still one
            ex.QueueReaction("Death"); // death overwrites
            ex.Tick(2.0f);
            Assert.AreEqual(new[] { "Punch", "Death" }, driver.Played.ToArray());
            Object.DestroyImmediate(ex.gameObject);
        }

        [Test]
        public void HealthSystem_CountsStreak_AndIgnoresDamageWhenDead()
        {
            var go = new GameObject("HpTest");
            var hp = go.AddComponent<HealthSystem>();
            hp.ApplyDamage(3);
            hp.ApplyDamage(3);
            Assert.AreEqual(2, hp.HitStreak);
            Assert.AreEqual(4, hp.CurrentHealth);
            hp.ApplyDamage(4); // dies at 0
            Assert.IsTrue(hp.IsDead);
            hp.ApplyDamage(3); // ignored
            Assert.AreEqual(0, hp.CurrentHealth);
            Object.DestroyImmediate(go);
        }
    }
}
