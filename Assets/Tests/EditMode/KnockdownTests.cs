using Game.Actions;
using Game.Combat;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // Full director loop: attacker impact -> victim damage -> queued reaction.
    public sealed class KnockdownTests
    {
        private static ActionDefinition Def(string id, float duration, float stamina,
            float range, bool heavy, string fallback)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("duration").floatValue = duration;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("range").floatValue = range;
            so.FindProperty("fallbackActionId").stringValue = fallback;
            so.FindProperty("isHeavy").boolValue = heavy;
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static ActionManifest Manifest()
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var defs = new[]
            {
                Def("Idle", 1f, 0f, 0f, false, "Idle"),
                Def("Punch", 1.1f, 0f, 3f, false, "Idle"),
                Def("AttackKick", 2.7f, 0f, 3f, true, "Idle"),
                Def("HitReaction", 0.7f, 0f, 0f, false, "Idle"),
                Def("Knockdown", 1.3f, 0f, 0f, false, "StandUp"),
                Def("StandUp", 8.3f, 0f, 0f, false, "Idle"),
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

        private sealed class Fighter
        {
            public GameObject Go;
            public ActionExecutor Exec;
            public HealthSystem Hp;
            public CombatDirector Director;
            public StubDriver Driver;
        }

        private static Fighter MakeFighter(string name, Vector3 pos, ActionManifest m)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var exec = go.AddComponent<ActionExecutor>();
            var driver = new StubDriver();
            exec.BindDriver(driver);
            exec.BindManifest(m);
            exec.Stamina = 100f;
            var hp = go.AddComponent<HealthSystem>();
            var dir = go.AddComponent<CombatDirector>();
            // Edit-mode tests never run OnEnable (CombatDirector has no
            // ExecuteAlways), so SerializedObject wiring alone never subscribes
            // the impact handler and damage silently never lands. Bind()
            // re-subscribes explicitly — the same runtime path GruntSpawner
            // uses for fresh fighters. time=null keeps the Running behavior.
            dir.Bind(exec, hp, null);
            return new Fighter { Go = go, Exec = exec, Hp = hp, Director = dir, Driver = driver };
        }

        private static void TickAll(Fighter f, float dt)
        {
            f.Exec.Tick(dt);
        }

        [Test]
        public void NormalHit_StaggersAfterSwing()
        {
            var m = Manifest();
            var attacker = MakeFighter("Attacker", new Vector3(400f, 0f, 400f), m); // rig lejos del origen: la escena real no debe interferir
            var victim = MakeFighter("Victim", new Vector3(402f, 0f, 400f), m);
            Assert.IsTrue(attacker.Exec.LoadPlan("{\"actions\":[{\"type\":\"Punch\"}]}"));
            attacker.Exec.StartPlan();
            attacker.Exec.Tick(1.2f); // swing completes, impact at 50% damaged victim
            Assert.AreEqual(9, victim.Hp.CurrentHealth);
            TickAll(victim, 0.1f); // victim was idle: reaction plays immediately
            Assert.AreEqual(new[] { "HitReaction" }, victim.Driver.Played.ToArray());
            Object.DestroyImmediate(attacker.Go);
            Object.DestroyImmediate(victim.Go);
        }

        [Test]
        public void HeavyHit_KnocksDown()
        {
            var m = Manifest();
            var attacker = MakeFighter("Attacker", new Vector3(400f, 0f, 400f), m); // rig lejos del origen: la escena real no debe interferir
            var victim = MakeFighter("Victim", new Vector3(402f, 0f, 400f), m);
            Assert.IsTrue(attacker.Exec.LoadPlan("{\"actions\":[{\"type\":\"AttackKick\"}]}"));
            attacker.Exec.StartPlan();
            attacker.Exec.Tick(3.0f);
            Assert.AreEqual(9, victim.Hp.CurrentHealth);
            TickAll(victim, 1.5f); // drain Knockdown -> StandUp plays
            Assert.AreEqual(new[] { "Knockdown", "StandUp" }, victim.Driver.Played.ToArray());
            Object.DestroyImmediate(attacker.Go);
            Object.DestroyImmediate(victim.Go);
        }

        [Test]
        public void FiveHitStreak_KnocksDown()
        {
            var m = Manifest();
            var attacker = MakeFighter("Attacker", new Vector3(400f, 0f, 400f), m); // rig lejos del origen: la escena real no debe interferir
            var victim = MakeFighter("Victim", new Vector3(402f, 0f, 400f), m);
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(attacker.Exec.LoadPlan("{\"actions\":[{\"type\":\"Punch\"}]}"));
                attacker.Exec.StartPlan();
                attacker.Exec.Tick(1.2f);
                TickAll(victim, 1.0f); // drain victim reaction so next hit lands mid-idle
            }
            Assert.AreEqual(5, victim.Hp.CurrentHealth);
            // 5th hit streaked: knockdown queued; drain it to reach StandUp.
            TickAll(victim, 1.5f);
            // 5th hit streaked: knockdown queued (2-action plan).
            CollectionAssert.AreEqual(new[] { "Knockdown", "StandUp" },
                victim.Driver.Played.GetRange(victim.Driver.Played.Count - 2, 2).ToArray());
            Object.DestroyImmediate(attacker.Go);
            Object.DestroyImmediate(victim.Go);
        }
    }
}
