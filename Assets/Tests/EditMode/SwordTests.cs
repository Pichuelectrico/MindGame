using Game.Combat;
using Game.Execution;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    public sealed class SwordTests
    {
        private static Game.Actions.ActionDefinition Def(string id, float duration, bool needsWeapon)
        {
            var def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("duration").floatValue = duration;
            so.FindProperty("requiresWeapon").boolValue = needsWeapon;
            so.FindProperty("staminaCost").floatValue = 0f;
            so.FindProperty("fallbackActionId").stringValue = "Idle";
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static Game.Actions.ActionManifest Manifest()
        {
            var m = ScriptableObject.CreateInstance<Game.Actions.ActionManifest>();
            var defs = new[] { Def("Idle", 1f, false), Def("ThrowWeapon", 1f, true) };
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            p.arraySize = defs.Length;
            for (int i = 0; i < defs.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        private static (SwordManager manager, ActionExecutor exec, HealthSystem hp) Scene()
        {
            var mgrGo = new GameObject("SwordMgr");
            var mgr = mgrGo.AddComponent<SwordManager>();
            var go = new GameObject("Fighter");
            go.transform.position = new Vector3(0f, 0f, 5f);
            var exec = go.AddComponent<ActionExecutor>();
            exec.BindDriver(new StubDriver());
            exec.BindManifest(Manifest());
            exec.Stamina = 100f;
            var hp = go.AddComponent<HealthSystem>();
            return (mgr, exec, hp);
        }

        [Test]
        public void FullCycle_Map_Equipped_Thrown_Respawned()
        {
            var (mgr, exec, hp) = Scene();
            mgr.ForceSpawn();
            Assert.AreEqual(SwordState.InMap, mgr.State);
            mgr.Tick(0.3f); // pickup poll: fighter stands on the spawn
            Assert.AreEqual(SwordState.Equipped, mgr.State);
            Assert.IsTrue(exec.HasWeapon);
            Assert.IsTrue(mgr.SwordPosition != Vector3.zero);
            // Throw connects at 50%: sword lost, then respawns.
            Assert.IsTrue(exec.LoadPlan("{\"actions\":[{\"type\":\"ThrowWeapon\"}]}"));
            exec.StartPlan();
            exec.Tick(0.6f);
            Assert.AreEqual(SwordState.InFlight, mgr.State);
            Assert.IsFalse(exec.HasWeapon);
            mgr.Tick(0.5f);
            Assert.AreEqual(SwordState.Respawning, mgr.State);
            mgr.Tick(30.1f);
            Assert.AreEqual(SwordState.InMap, mgr.State);
            Object.DestroyImmediate(mgr.gameObject);
            Object.DestroyImmediate(exec.gameObject);
        }

        [Test]
        public void HolderDeath_DropsSwordToRespawn()
        {
            var (mgr, exec, hp) = Scene();
            mgr.ForceSpawn();
            mgr.Tick(0.3f);
            Assert.AreEqual(SwordState.Equipped, mgr.State);
            hp.ApplyDamage(10);
            Assert.AreEqual(SwordState.Respawning, mgr.State);
            Assert.IsFalse(exec.HasWeapon);
            Object.DestroyImmediate(mgr.gameObject);
            Object.DestroyImmediate(exec.gameObject);
        }
    }
}
