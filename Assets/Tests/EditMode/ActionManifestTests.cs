using Game.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // Schema A contract (ActionDefinition.actionId/fallbackActionId/staminaCost/
    // roleRestriction/aliases + ActionManifest.Find/FindByAlias/ResolveFallback).
    public sealed class ActionManifestTests
    {
        private static ActionDefinition MakeDef(string id, string fallback,
            float stamina = 0f, int role = 0, params string[] aliases)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("fallbackActionId").stringValue = fallback;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("roleRestriction").intValue = role;
            var ap = so.FindProperty("aliases");
            ap.arraySize = aliases.Length;
            for (int i = 0; i < aliases.Length; i++)
                ap.GetArrayElementAtIndex(i).stringValue = aliases[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static ActionManifest MakeManifest(params ActionDefinition[] defs)
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            p.arraySize = defs.Length;
            for (int i = 0; i < defs.Length; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        // Evasion chain mirrors the real assets: Dodge->Roll->BlockCenter->Idle.
        private static ActionManifest EvasionManifest()
        {
            return MakeManifest(
                MakeDef("Idle", "Idle"),
                MakeDef("IdleFight", "Idle"),
                MakeDef("Dodge", "Roll", 15f),
                MakeDef("Roll", "BlockCenter", 15f),
                MakeDef("BlockCenter", "Idle", 5f));
        }

        [Test]
        public void FallbackChain_DoesNotCycle_LandsOnIdle()
        {
            var m = EvasionManifest();
            var dodge = m.Find("Dodge");
            // Zero stamina: Dodge->Roll->BlockCenter all unavailable -> Idle. Must return.
            var resolved = m.ResolveFallback(dodge, false, ActionRole.Any, 0f);
            Assert.IsNotNull(resolved);
            Assert.AreEqual("Idle", resolved.ActionId);
        }

        [Test]
        public void FallbackChain_PicksNearestAffordable()
        {
            var m = EvasionManifest();
            var roll = m.Find("Roll");
            // 5 stamina: BlockCenter affordable -> chosen before Idle.
            var resolved = m.ResolveFallback(roll, false, ActionRole.Any, 5f);
            Assert.AreEqual("BlockCenter", resolved.ActionId);
        }

        [Test]
        public void SelfFallback_ResolvesToIdle()
        {
            var m = MakeManifest(
                MakeDef("Idle", "Idle"),
                MakeDef("Loop", "Loop", 10f));
            var loop = m.Find("Loop");
            var resolved = m.ResolveFallback(loop, false, ActionRole.Any, 0f);
            Assert.IsNotNull(resolved);
            Assert.AreEqual("Idle", resolved.ActionId);
        }

        [Test]
        public void FindByAlias_ResolvesAliases()
        {
            var m = MakeManifest(
                MakeDef("Idle", "Idle"),
                MakeDef("AttackSword", "Punch", 12f, 0, "attack", "atacar", "sword"),
                MakeDef("BlockCenter", "Idle", 5f, 0, "block", "bloqueo", "block center"));
            Assert.AreEqual("AttackSword", m.FindByAlias("Attack").ActionId);
            Assert.AreEqual("AttackSword", m.FindByAlias("  attack ").ActionId);
            Assert.AreEqual("BlockCenter", m.FindByAlias("block center").ActionId);
            Assert.AreEqual("BlockCenter", m.FindByAlias("BLOCK CENTER").ActionId);
            // Direct id still wins.
            Assert.AreEqual("BlockCenter", m.FindByAlias("BlockCenter").ActionId);
            Assert.IsNull(m.FindByAlias("nonexistent"));
            Assert.IsNull(m.FindByAlias(null));
        }

        [Test]
        public void BossDodge_OnlyAvailableToBoss()
        {
            var bossDodge = MakeDef("BossDodge", "IdleFight", 15f, 1 /* BossOnly */,
                "boss dodge", "esquiva del jefe");
            Assert.IsTrue(bossDodge.IsAvailable(false, ActionRole.BossOnly, 15f));
            Assert.IsFalse(bossDodge.IsAvailable(false, ActionRole.Any, 15f));
            Assert.IsFalse(bossDodge.IsAvailable(false, ActionRole.BossOnly, 0f));
        }
    }
}
