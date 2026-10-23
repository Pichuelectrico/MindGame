using System.Collections.Generic;
using Game.Actions;
using Game.AI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    // [F8] Weighted defensive decisions (anti block-lock, user playtest:
    // "se pasan en una pelea de bloqueos que no es divertido"). Contract:
    // with a close threat and NO usable melee, the defensive band rolls
    // weighted — attack(=press forward) dominant, dodge medium, block
    // smallest — and a Block* decision shrinks the next block share
    // (anti-mirror). Without a threat, behavior is unchanged (IdleFight).
    // Distributions are deterministic: tests inject a seeded Random.
    public sealed class BotBrainWeightsTests
    {
        private static ActionDefinition Def(string id, float duration, float stamina, float range, string fallback)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("duration").floatValue = duration;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("range").floatValue = range;
            so.FindProperty("fallbackActionId").stringValue = fallback;
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

        private static CombatStateSnapshot Snapshot(float threatDistance)
        {
            var entities = new List<EntityState>
            {
                new EntityState
                {
                    id = "player",
                    name = "Y Bot",
                    isBoss = false,
                    isPlayerAvatar = true,
                    position = new float[3],
                    health = 100f,
                    maxHealth = 100f,
                    stamina = 100f,
                    isTargetable = true
                }
            };
            if (threatDistance >= 0f)
            {
                entities.Add(new EntityState
                {
                    id = "grunt#1",
                    name = "Grunt",
                    isBoss = false,
                    position = new[] { threatDistance, 0f, 0f },
                    health = 100f,
                    maxHealth = 100f,
                    stamina = 100f,
                    isTargetable = true
                });
            }
            return new CombatStateSnapshot
            {
                phase = "Running",
                elapsedTime = 0f,
                entities = entities.ToArray(),
                distances = new EntityDistance[0] // brain falls back to positions
            };
        }

        private static ActionManifest DefensiveManifest(bool withDodge)
        {
            if (withDodge)
                return Manifest(
                    Def("Punch", 1f, 0f, 3f, "Idle"),
                    Def("BlockCenter", 1f, 0f, 0f, "Idle"),
                    Def("Dodge", 1f, 0f, 0f, "Idle"),
                    Def("Run", 1f, 0f, 0f, "Idle"),
                    Def("IdleFight", 1f, 0f, 0f, "Idle"));
            return Manifest(
                Def("Punch", 1f, 0f, 3f, "Idle"),
                Def("BlockCenter", 1f, 0f, 0f, "Idle"),
                Def("Run", 1f, 0f, 0f, "Idle"),
                Def("IdleFight", 1f, 0f, 0f, "Idle"));
        }

        [Test]
        public void CloseThreat_BlockFrequency_Below25Percent()
        {
            var brain = new BotBrain { Rng = new System.Random(7) };
            var m = DefensiveManifest(withDodge: true);

            int blocks = 0;
            const int N = 300;
            for (int i = 0; i < N; i++)
            {
                var plan = brain.Decide(Snapshot(3.5f), m, "player"); // in band (<=4), out of melee (>3)
                if (plan != null && plan.Contains("BlockCenter")) blocks++;
            }

            Assert.Greater(blocks, 0, "the block share must still exist (sanity)");
            Assert.Less(blocks, N * 0.25,
                "with a close threat, blocking must stay under 25% of defensive decisions (default share 0.15)");
        }

        [Test]
        public void NoThreat_IdleFight_Unchanged()
        {
            var brain = new BotBrain { Rng = new System.Random(3) };
            var m = DefensiveManifest(withDodge: true);

            for (int i = 0; i < 10; i++)
            {
                var plan = brain.Decide(Snapshot(-1f), m, "player"); // no other entities
                Assert.IsNotNull(plan);
                Assert.IsTrue(plan.Contains("IdleFight"),
                    "without a threat the brain must keep the legacy IdleFight behavior");
            }
        }

        [Test]
        public void AntiMirror_BlockAfterBlock_GreatlyReduced()
        {
            // Without Dodge the shares renormalize to attack .65 / block .15
            // (block ~18.75%); right after a Block* decision the block share
            // x0.25 (~5.45%). Deterministic with the fixed seed.
            var brain = new BotBrain { Rng = new System.Random(11) };
            var m = DefensiveManifest(withDodge: false);

            int afterBlockBlocks = 0, afterBlockTotal = 0;
            int afterNonBlockBlocks = 0, afterNonBlockTotal = 0;
            bool prevWasBlock = false;

            const int N = 1000;
            for (int i = 0; i < N; i++)
            {
                var plan = brain.Decide(Snapshot(3.5f), m, "player");
                Assert.IsNotNull(plan);
                bool isBlock = plan.Contains("BlockCenter");
                if (prevWasBlock)
                {
                    afterBlockTotal++;
                    if (isBlock) afterBlockBlocks++;
                }
                else
                {
                    afterNonBlockTotal++;
                    if (isBlock) afterNonBlockBlocks++;
                }
                prevWasBlock = isBlock;
            }

            Assert.Greater(afterBlockTotal, 0, "some blocks must happen (sanity)");
            Assert.Greater(afterNonBlockTotal, 0, "some non-blocks must happen (sanity)");
            double pAfterBlock = (double)afterBlockBlocks / afterBlockTotal;
            double pAfterNonBlock = (double)afterNonBlockBlocks / afterNonBlockTotal;
            Assert.Less(pAfterBlock, pAfterNonBlock,
                "anti-mirror: blocking right after a block must be rarer than otherwise");
            Assert.Less(pAfterBlock, pAfterNonBlock * 0.75,
                "anti-mirror: the post-block block probability must drop well below the base share");
        }
    }
}
