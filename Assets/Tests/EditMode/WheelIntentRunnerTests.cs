using Game.Actions;
using Game.Combat;
using Game.Core;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    public sealed class WheelIntentRunnerTests
    {
        private static ActionManifest Manifest()
        {
            var manifest = ScriptableObject.CreateInstance<ActionManifest>();
            var run = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var rso = new SerializedObject(run);
            rso.FindProperty("actionId").stringValue = "Run";
            rso.FindProperty("duration").floatValue = 1.2f;
            rso.FindProperty("staminaCost").floatValue = 0f;
            rso.FindProperty("fallbackActionId").stringValue = "Idle";
            rso.ApplyModifiedPropertiesWithoutUndo();
            var idle = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var iso = new SerializedObject(idle);
            iso.FindProperty("actionId").stringValue = "Idle";
            iso.FindProperty("duration").floatValue = 1f;
            iso.FindProperty("fallbackActionId").stringValue = "Idle";
            iso.ApplyModifiedPropertiesWithoutUndo();
            var so = new SerializedObject(manifest);
            var p = so.FindProperty("actions");
            p.arraySize = 2;
            p.GetArrayElementAtIndex(0).objectReferenceValue = run;
            p.GetArrayElementAtIndex(1).objectReferenceValue = idle;
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
            return manifest;
        }

        private sealed class Rig
        {
            public GameObject Player;
            public ActionExecutor Exec;
            public HealthSystem Hp;
            public WheelIntentRunner Runner;
            public SwordManager Sword;
            public TimeStateMachine Time;
        }

        private static Rig Build(Vector3 playerPos, float timeout = 5f)
        {
            var rig = new Rig();
            var mgrGo = new GameObject("SwordMgr");
            rig.Sword = mgrGo.AddComponent<SwordManager>();
            rig.Sword.ForceSpawn();
            var go = new GameObject("WheelPlayer");
            go.transform.position = playerPos;
            rig.Player = go;
            rig.Exec = go.AddComponent<ActionExecutor>();
            rig.Exec.BindDriver(new StubDriver());
            rig.Exec.BindManifest(Manifest());
            rig.Exec.Stamina = 100f;
            rig.Hp = go.AddComponent<HealthSystem>();
            rig.Runner = go.AddComponent<WheelIntentRunner>();
            var so = new SerializedObject(rig.Runner);
            so.FindProperty("executor").objectReferenceValue = rig.Exec;
            so.FindProperty("health").objectReferenceValue = rig.Hp;
            so.FindProperty("timeoutSeconds").floatValue = timeout;
            so.ApplyModifiedPropertiesWithoutUndo();
            rig.Time = new TimeStateMachine();
            rig.Time.EnterPlanning();
            return rig;
        }

        private static void Teardown(Rig rig)
        {
            Object.DestroyImmediate(rig.Sword.gameObject);
            Object.DestroyImmediate(rig.Player);
        }

        [Test]
        public void RejectWhenAlreadyArmed()
        {
            var rig = Build(Vector3.zero);
            rig.Exec.HasWeapon = true;
            Assert.IsFalse(rig.Runner.RunFromWheel(WheelIntent.GoPickupWeapon, rig.Time));
            Assert.IsFalse(rig.Runner.HasActiveIntent);
            Teardown(rig);
        }

        [Test]
        public void RejectHealthWhenNoPickups()
        {
            var rig = Build(Vector3.zero);
            Assert.AreEqual(0, HealthPickup.Active.Count);
            Assert.IsFalse(rig.Runner.RunFromWheel(WheelIntent.GoTakeHealth, rig.Time));
            Teardown(rig);
        }

        [Test]
        public void SteerReachAndPickup()
        {
            var rig = Build(Vector3.zero);
            rig.Player.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // back to goal
            Assert.IsTrue(rig.Runner.RunFromWheel(WheelIntent.GoPickupWeapon, rig.Time));
            Assert.AreEqual(CombatPhase.Running, rig.Time.Current); // exited Planning
            Quaternion start = rig.Player.transform.rotation;
            for (int i = 0; i < 80 && rig.Runner.HasActiveIntent; i++)
            {
                rig.Runner.TickIntent(0.1f);
                rig.Exec.Tick(0.1f);
                rig.Sword.Tick(0.1f);
            }
            Assert.IsFalse(rig.Runner.HasActiveIntent);
            Assert.IsTrue(rig.Exec.HasWeapon); // auto-pickup fired on proximity
            Assert.AreNotEqual(start, rig.Player.transform.rotation); // steered
            Teardown(rig);
        }

        [Test]
        public void TimeoutCancels()
        {
            var rig = Build(new Vector3(0f, 0f, -40f), timeout: 0.5f);
            Assert.IsTrue(rig.Runner.RunFromWheel(WheelIntent.GoPickupWeapon, rig.Time));
            rig.Runner.TickIntent(0.6f);
            Assert.IsFalse(rig.Runner.HasActiveIntent);
            Assert.IsFalse(rig.Exec.HasWeapon);
            Teardown(rig);
        }

        [Test]
        public void DamageCancels()
        {
            var rig = Build(Vector3.zero);
            Assert.IsTrue(rig.Runner.RunFromWheel(WheelIntent.GoPickupWeapon, rig.Time));
            rig.Hp.ApplyDamage(3);
            rig.Runner.TickIntent(0.1f);
            Assert.IsFalse(rig.Runner.HasActiveIntent);
            Teardown(rig);
        }

        [Test]
        public void HealthRiseCompletes()
        {
            var rig = Build(Vector3.zero);
            var pickupGo = new GameObject("HpPickup");
            pickupGo.AddComponent<HealthPickup>();
            rig.Hp.ApplyDamage(5);
            Assert.IsTrue(rig.Runner.RunFromWheel(WheelIntent.GoTakeHealth, rig.Time));
            rig.Hp.Restore(5); // pickup poll effect
            rig.Runner.TickIntent(0.1f);
            Assert.IsFalse(rig.Runner.HasActiveIntent);
            Object.DestroyImmediate(pickupGo);
            Teardown(rig);
        }

        [Test]
        public void PickupRegistryTracksNearest()
        {
            var a = new GameObject("PA").AddComponent<HealthPickup>();
            a.transform.position = new Vector3(10f, 0f, 0f);
            var b = new GameObject("PB").AddComponent<HealthPickup>();
            b.transform.position = new Vector3(2f, 0f, 0f);
            Assert.AreEqual(b, HealthPickup.NearestTo(Vector3.zero));
            Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(b.gameObject);
            Assert.IsNull(HealthPickup.NearestTo(Vector3.zero));
        }
    }
}
