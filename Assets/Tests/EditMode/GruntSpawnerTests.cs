using System.Collections.Generic;
using Game.Combat;
using Game.Execution;
using Game.VFX;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // Drives GruntSpawner.NotifyGruntDied directly (no combat wait).
    public sealed class GruntSpawnerTests
    {
        [Test]
        public void EnsureAttackTelegraph_AddsOnce_Idempotent()
        {
            var go = new GameObject("GruntTelegraphTest");
            go.AddComponent<ActionExecutor>();
            var first = GruntSpawner.EnsureAttackTelegraph(go);
            var second = GruntSpawner.EnsureAttackTelegraph(go);
            Assert.IsNotNull(first);
            Assert.AreSame(first, second);
            Assert.AreEqual(1, go.GetComponents<AttackTelegraph>().Length);
            Object.DestroyImmediate(go);
        }

        private static GruntSpawner Spawner(int killsToWake = 10)
        {
            var go = new GameObject("SpawnerTest");
            var s = go.AddComponent<GruntSpawner>();
            var so = new SerializedObject(s);
            so.FindProperty("killsToWakeBoss").intValue = killsToWake;
            so.FindProperty("despawnSeconds").floatValue = 0f; // no delayed Destroy in EditMode
            so.ApplyModifiedPropertiesWithoutUndo();
            return s;
        }

        [Test]
        public void NineKills_BossSleeps_TenthWakes()
        {
            var s = Spawner();
            var totals = new List<int>();
            s.OnGruntKilled += totals.Add;
            for (int i = 0; i < 9; i++)
                s.NotifyGruntDied(new GameObject("victim"));
            Assert.IsFalse(s.BossAwake);
            s.NotifyGruntDied(new GameObject("victim"));
            Assert.IsTrue(s.BossAwake);
            Assert.AreEqual(10, s.KillCount);
            Assert.AreEqual(10, totals.Count);
            Assert.AreEqual(10, totals[9]);
            Object.DestroyImmediate(s.gameObject);
        }

        [Test]
        public void FurtherKills_StayAwake_CountGrows()
        {
            var s = Spawner(2);
            s.NotifyGruntDied(new GameObject("victim"));
            Assert.IsFalse(s.BossAwake);
            s.NotifyGruntDied(new GameObject("victim"));
            Assert.IsTrue(s.BossAwake);
            s.NotifyGruntDied(new GameObject("victim"));
            Assert.AreEqual(3, s.KillCount);
            Assert.IsTrue(s.BossAwake);
            Object.DestroyImmediate(s.gameObject);
        }
    }
}
