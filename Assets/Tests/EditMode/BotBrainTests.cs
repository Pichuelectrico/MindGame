using Game.AI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    public sealed class BotBrainTests
    {
        private static Game.Actions.ActionDefinition Def(string id, float range, float stamina,
            bool needsWeapon, int role, string fallback)
        {
            var def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("range").floatValue = range;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("requiresWeapon").boolValue = needsWeapon;
            so.FindProperty("roleRestriction").intValue = role;
            so.FindProperty("needsTarget").boolValue = true;
            so.FindProperty("fallbackActionId").stringValue = fallback;
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static Game.Actions.ActionManifest BrainManifest()
        {
            var m = ScriptableObject.CreateInstance<Game.Actions.ActionManifest>();
            var defs = new[]
            {
                Def("Idle", 0f, 0f, false, 0, "Idle"),
                Def("IdleFight", 0f, 0f, false, 0, "Idle"),
                Def("Run", 0f, 5f, false, 0, "Idle"),
                Def("Walk", 0f, 2f, false, 0, "Idle"),
                Def("Punch", 2f, 10f, false, 0, "Dodge"),
                Def("AttackSword", 2.2f, 12f, true, 0, "Punch"),
                Def("ThrowWeapon", 8f, 20f, true, 0, "Punch"),
                Def("BlockCenter", 0f, 5f, false, 0, "Idle"),
                Def("Dodge", 0f, 15f, false, 0, "Idle"),
                Def("BossHeavyAttack", 2.5f, 15f, false, 1, "Punch"),
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

        private static CombatStateSnapshot Versus(float dist, bool bossWeapon, float bossStamina,
            float bossHealth = 200f)
        {
            return new CombatStateSnapshot
            {
                phase = "Executing",
                entities = new[]
                {
                    new EntityState
                    {
                        id = "boss", isBoss = true, isTargetable = true,
                        position = new[] { 0f, 0f, 0f }, health = bossHealth, maxHealth = 200f,
                        stamina = bossStamina, weaponEquipped = bossWeapon
                    },
                    new EntityState
                    {
                        id = "player", isPlayerAvatar = true, isTargetable = true,
                        position = new[] { dist, 0f, 0f }, health = 100f, maxHealth = 100f,
                        stamina = 60f, weaponEquipped = true
                    },
                },
                distances = new[] { new EntityDistance { fromId = "boss", toId = "player", distance = dist } }
            };
        }

        [Test]
        public void CloseWithSword_AttacksHeavyAsBoss()
        {
            string json = new BotBrain().Decide(Versus(2f, true, 80f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossHeavyAttack", plan.actions[0].type);
            Assert.AreEqual("player", plan.actions[0].target);
        }

        [Test]
        public void CloseWithoutWeapon_AndWeakForHeavy_Punches()
        {
            // 12 stamina: BossHeavyAttack (15) unaffordable, Punch (10) affordable.
            string json = new BotBrain().Decide(Versus(1.5f, false, 12f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("Punch", plan.actions[0].type);
        }

        [Test]
        public void LowStaminaClose_Defends()
        {
            // 5 stamina: no attack affordable (Punch 10), BlockCenter (5) is.
            string json = new BotBrain().Decide(Versus(2f, false, 5f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BlockCenter", plan.actions[0].type);
        }

        // [F43] Aggressive boss: in melee range the interim brain emits CHAINS
        // (special + light hits) instead of a single timid swing.
        [Test]
        public void BossChain_InRange_EmitsTwoOrMoreAttacks()
        {
            // Unarmed boss, 80 stamina: special first, then the light catalog.
            string json = new BotBrain().Decide(Versus(2f, false, 80f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.GreaterOrEqual(plan.actions.Length, 2);
            Assert.AreEqual("BossHeavyAttack", plan.actions[0].type);
            Assert.AreEqual("player", plan.actions[0].target);
            for (int i = 1; i < plan.actions.Length; i++)
                Assert.AreEqual("Punch", plan.actions[i].type);
        }

        // [F43] The cadence default lives on the controller (serialized): 0.8s
        // decisions. The scene value is reapplied by Wire Sandbox (1.2 -> 0.8).
        [Test]
        public void BossDecisionInterval_DefaultIsAggressive()
        {
            var go = new GameObject("boss-controller-interval");
            try
            {
                var ctrl = go.AddComponent<Game.AI.BotController>();
                var so = new SerializedObject(ctrl);
                Assert.AreEqual(0.8f, so.FindProperty("decisionInterval").floatValue, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // [F45] En furia el boss encadena Heavy: the specials are re-chainable
        // and take priority (heavy-first passes) — Heavy, Heavy, then the light
        // Punch if stamina allows (15+15+10 = 40 <= 80).
        [Test]
        public void FuryChain_PrioritizesHeavyAttacks()
        {
            string json = new BotBrain { Furious = true }.Decide(Versus(2f, false, 80f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.GreaterOrEqual(plan.actions.Length, 2);
            Assert.AreEqual("BossHeavyAttack", plan.actions[0].type);
            Assert.AreEqual("BossHeavyAttack", plan.actions[1].type);
            Assert.AreEqual("player", plan.actions[0].target);
            // Heavy priority must beat the F18 weighted lights at least for the
            // first two slots; the tail may be a light if stamina/shape allows.
            Assert.GreaterOrEqual(
                System.Linq.Enumerable.Count(plan.actions, a => a.type == "BossHeavyAttack"), 2);
        }

        // [F45] Furia latch: first HP < 30% enrages permanently — cadence
        // decisionInterval/1.5 and speed composed FROM the captured base
        // (1.4 x 1.3 = 1.82), never a hard overwrite.
        [Test]
        public void Fury_LatchesBelow30Percent_AndComposesCadenceAndSpeed()
        {
            var go = new GameObject("boss-controller-furia");
            try
            {
                var ctrl = go.AddComponent<Game.AI.BotController>();
                var exec = go.AddComponent<Game.Execution.ActionExecutor>();
                var healthSys = go.AddComponent<Game.Combat.HealthSystem>();

                // Simulate the F43 sandbox wiring: base multiplier 1.4 set BEFORE
                // Bind — Bind/Start must capture it as the furia composition base.
                exec.LocomotionSpeedMultiplier = 1.4f;
                ctrl.Bind(null, exec, healthSys, null, null);

                var so = new SerializedObject(ctrl);
                so.FindProperty("enableFuria").boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();

                // Above the threshold (30% of 100): nothing latches.
                SetHealth(healthSys, 100, 100);
                ctrl.EvaluateFuria();
                Assert.IsFalse(ctrl.IsFuria);
                Assert.AreEqual(0.8f, ctrl.EffectiveDecisionInterval, 1e-3f);

                // 20% HP: first detection latches; cadence 0.8/1.5, speed 1.4x1.3.
                SetHealth(healthSys, 20, 100);
                ctrl.EvaluateFuria();
                Assert.IsTrue(ctrl.IsFuria);
                Assert.AreEqual(0.8f / 1.5f, ctrl.EffectiveDecisionInterval, 1e-3f);
                Assert.AreEqual(1.4f * 1.3f, exec.LocomotionSpeedMultiplier, 1e-3f);

                // One-way latch: healing back above 30% changes nothing.
                SetHealth(healthSys, 100, 100);
                ctrl.EvaluateFuria();
                Assert.IsTrue(ctrl.IsFuria);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void SetHealth(Game.Combat.HealthSystem hs, int current, int max)
        {
            var so = new SerializedObject(hs);
            so.FindProperty("currentHealth").intValue = current;
            so.FindProperty("maxHealth").intValue = max;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // [F46] BrainManifest + BossLaserBeam (BossOnly, 20m, 30 stamina, no
        // weapon, no target lock). Separate builder so the shared manifest and
        // its existing expectations stay untouched.
        private static Game.Actions.ActionManifest LaserManifest()
        {
            var m = BrainManifest();
            var laser = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var lso = new SerializedObject(laser);
            lso.FindProperty("actionId").stringValue = "BossLaserBeam";
            lso.FindProperty("range").floatValue = 20f;
            lso.FindProperty("staminaCost").floatValue = 30f;
            lso.FindProperty("requiresWeapon").boolValue = false;
            lso.FindProperty("needsTarget").boolValue = false;
            lso.FindProperty("roleRestriction").intValue = 1; // BossOnly
            lso.FindProperty("fallbackActionId").stringValue = "Idle";
            lso.ApplyModifiedPropertiesWithoutUndo();

            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            int idx = p.arraySize;
            p.InsertArrayElementAtIndex(idx);
            p.GetArrayElementAtIndex(idx).objectReferenceValue = laser;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        // [F46] Far from the player + laser off cooldown -> the boss casts
        // INSTEAD of running in. Deterministic: no Rng anywhere on this path.
        [Test]
        public void Laser_FarWithStaminaAndReady_CastsInsteadOfRun()
        {
            string json = new BotBrain().Decide(Versus(10f, true, 80f), LaserManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserBeam", plan.actions[0].type);
            Assert.AreEqual("player", plan.actions[0].target);
        }

        // [F46] In melee range the laser NEVER fires — step 1 (melee chain)
        // returns first. Any distance <= 6m is melee pressure.
        [Test]
        public void Laser_InMeleeRange_NeverChosen()
        {
            string json = new BotBrain().Decide(Versus(2f, true, 80f), LaserManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            foreach (var a in plan.actions)
                Assert.AreNotEqual("BossLaserBeam", a.type);
        }

        // [F46] On cooldown the rule is skipped entirely -> the boss runs in
        // exactly like pre-F46 (cooldown is the controller's resource knob).
        [Test]
        public void Laser_OnCooldown_SkippedAndRunsInstead()
        {
            string json = new BotBrain
            {
                RemainingCooldownOf = id => id == "BossLaserBeam" ? 7f : 0f
            }.Decide(Versus(10f, true, 80f), LaserManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("Run", plan.actions[0].type);
        }

        // [F46] Below the 30 stamina cost the cast is unaffordable — never chosen.
        [Test]
        public void Laser_WithoutStamina_NeverChosen()
        {
            string json = new BotBrain().Decide(Versus(10f, true, 25f), LaserManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            foreach (var a in plan.actions)
                Assert.AreNotEqual("BossLaserBeam", a.type);
        }

        // [F47] Manifest with BOTH ranged casts (priority / fallback tests).
        private static Game.Actions.ActionManifest TornadoManifest()
        {
            var m = LaserManifest(); // BrainManifest + BossLaserBeam
            var tornado = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var tso = new SerializedObject(tornado);
            tso.FindProperty("actionId").stringValue = "BossLaserTornado";
            tso.FindProperty("range").floatValue = 20f;
            tso.FindProperty("staminaCost").floatValue = 35f;
            tso.FindProperty("requiresWeapon").boolValue = false;
            tso.FindProperty("needsTarget").boolValue = false;
            tso.FindProperty("roleRestriction").intValue = 1; // BossOnly
            tso.FindProperty("fallbackActionId").stringValue = "Idle";
            tso.ApplyModifiedPropertiesWithoutUndo();

            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            int idx = p.arraySize;
            p.InsertArrayElementAtIndex(idx);
            p.GetArrayElementAtIndex(idx).objectReferenceValue = tornado;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        // [F47] Mid-range window (3-10m): ready + affordable -> tornado.
        [Test]
        public void Tornado_MidRange_ReadyAndAffordable_Casts()
        {
            string json = new BotBrain { ThrowChance = 0f }.Decide(Versus(5f, true, 80f), TornadoManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserTornado", plan.actions[0].type);
            Assert.AreEqual("player", plan.actions[0].target);
        }

        // [F47] En FURIA el tornado mantiene prioridad sobre el laser recto
        // dentro de su ventana (8m, ambos listos -> tornado, per spec).
        [Test]
        public void Tornado_Fury_PrioritizesOverStraightLaser()
        {
            string json = new BotBrain { Furious = true, ThrowChance = 0f }.Decide(Versus(8f, true, 80f), TornadoManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserTornado", plan.actions[0].type);
        }

        // [F47] Tornado on cooldown: the straight laser takes over at 8m
        // (its >6m window) — clean ranged fallback, no melee nonsense.
        [Test]
        public void Tornado_OnCooldown_FallsBackToStraightLaser()
        {
            string json = new BotBrain
            {
                ThrowChance = 0f,
                RemainingCooldownOf = id => id == "BossLaserTornado" ? 5f : 0f
            }.Decide(Versus(8f, true, 80f), TornadoManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserBeam", plan.actions[0].type);
        }

        // [F47] Outside the window the tornado never fires: <= 3m is melee
        // pressure; > 10m the straight laser owns the decision.
        [Test]
        public void Tornado_OutsideWindow_NeverChosen()
        {
            // Melee range: chain only.
            string meleeJson = new BotBrain().Decide(Versus(2f, true, 80f), TornadoManifest());
            var melee = LlmPlanResponse.FromJson(meleeJson);
            Assert.IsTrue(melee.IsValid());
            foreach (var a in melee.actions)
                Assert.AreNotEqual("BossLaserTornado", a.type);

            // Far: straight laser, not the tornado.
            string farJson = new BotBrain().Decide(Versus(12f, true, 80f), TornadoManifest());
            var far = LlmPlanResponse.FromJson(farJson);
            Assert.IsTrue(far.IsValid());
            Assert.AreEqual("BossLaserBeam", far.actions[0].type);
        }

        // [F49] Normal mode: the tornado window is (2,6] — at 8m with BOTH
        // casts ready the STRAIGHT laser wins (medium-distance tool, not the
        // pre-F49 mid-range tornado).
        [Test]
        public void Laser_NormalMode_At8m_PreferredOverTornado()
        {
            string json = new BotBrain { ThrowChance = 0f }.Decide(Versus(8f, true, 80f), TornadoManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserBeam", plan.actions[0].type);
        }

        // [F49] Upper bound of the normal tornado window: exactly 6m still
        // spins (window is (2,6] closed on the right).
        [Test]
        public void Tornado_NormalWindow_At6m_Casts()
        {
            string json = new BotBrain { ThrowChance = 0f }.Decide(Versus(6f, true, 80f), TornadoManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserTornado", plan.actions[0].type);
        }

        // [F49] Fury expands the tornado window to (2,9]: at 9m enraged the
        // boss still spins instead of firing the straight laser.
        [Test]
        public void Fury_TornadoWindow_At9m_Casts()
        {
            string json = new BotBrain { Furious = true }.Decide(Versus(9f, true, 80f), TornadoManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserTornado", plan.actions[0].type);
        }

        // ------------------------------------------------- [F51] Seek health

        // HP 50% + NO orbs: the seek rule is a no-op — the plan stays the
        // normal mid-range decision (straight laser at 8m).
        [Test]
        public void SeekHealth_NoOrbes_PlanUnchanged()
        {
            string json = new BotBrain { ThrowChance = 0f }.Decide(Versus(8f, true, 80f, bossHealth: 100f), LaserManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserBeam", plan.actions[0].type);
        }

        // HP 50% + orb live: run to the nearest orb (target = orb GO name).
        [Test]
        public void SeekHealth_HalfHpWithOrb_RunsToNearestOrb()
        {
            var orbGo = new GameObject("HealthOrbTest1");
            try
            {
                orbGo.transform.position = new Vector3(0f, 0f, 3f);
                orbGo.AddComponent<Game.Combat.HealthPickup>();

                string json = new BotBrain { ThrowChance = 0f }.Decide(Versus(8f, true, 80f, bossHealth: 100f), LaserManifest());
                var plan = LlmPlanResponse.FromJson(json);
                Assert.IsTrue(plan.IsValid());
                Assert.AreEqual("Run", plan.actions[0].type);
                Assert.AreEqual("HealthOrbTest1", plan.actions[0].target);
            }
            finally
            {
                Object.DestroyImmediate(orbGo);
            }
        }

        // HP 80% (above threshold): the orb is ignored.
        [Test]
        public void SeekHealth_AboveThreshold_IgnoresOrb()
        {
            var orbGo = new GameObject("HealthOrbTest2");
            try
            {
                orbGo.transform.position = new Vector3(0f, 0f, 3f);
                orbGo.AddComponent<Game.Combat.HealthPickup>();

                string json = new BotBrain { ThrowChance = 0f }.Decide(Versus(8f, true, 80f, bossHealth: 160f), LaserManifest());
                var plan = LlmPlanResponse.FromJson(json);
                Assert.IsTrue(plan.IsValid());
                Assert.AreEqual("BossLaserBeam", plan.actions[0].type);
            }
            finally
            {
                Object.DestroyImmediate(orbGo);
            }
        }

        // FURIA NEVER loots: 10% HP + orb live -> it attacks (straight laser
        // at 8m; the seek rule is gated by !Furious before anything else).
        [Test]
        public void SeekHealth_FuriaNeverLoots_AttacksInstead()
        {
            var orbGo = new GameObject("HealthOrbTest3");
            try
            {
                orbGo.transform.position = new Vector3(0f, 0f, 3f);
                orbGo.AddComponent<Game.Combat.HealthPickup>();

                string json = new BotBrain { Furious = true, ThrowChance = 0f }.Decide(Versus(8f, true, 80f, bossHealth: 20f), LaserManifest());
                var plan = LlmPlanResponse.FromJson(json);
                Assert.IsTrue(plan.IsValid());
                Assert.AreEqual("BossLaserBeam", plan.actions[0].type);
            }
            finally
            {
                Object.DestroyImmediate(orbGo);
            }
        }

        // No Rng anywhere on the seek path: identical snapshot + orb ->
        // identical plan, twice.
        [Test]
        public void SeekHealth_Deterministic_SameSnapshotSamePlan()
        {
            var orbGo = new GameObject("HealthOrbTest4");
            try
            {
                orbGo.transform.position = new Vector3(0f, 0f, 3f);
                orbGo.AddComponent<Game.Combat.HealthPickup>();

                string a = new BotBrain { ThrowChance = 0f }.Decide(Versus(8f, true, 80f, bossHealth: 100f), LaserManifest());
                string b = new BotBrain { ThrowChance = 0f }.Decide(Versus(8f, true, 80f, bossHealth: 100f), LaserManifest());
                Assert.AreEqual(a, b);
                StringAssert.Contains("\"target\":\"HealthOrbTest4\"", a);
            }
            finally
            {
                Object.DestroyImmediate(orbGo);
            }
        }

        [Test]
        public void Far_ApproachesWithRun()
        {
            string json = new BotBrain().Decide(Versus(20f, false, 80f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("Run", plan.actions[0].type);
        }

        [Test]
        public void EmptySnapshot_ValidNonEmptyPlan()
        {
            string json = new BotBrain().Decide(new CombatStateSnapshot(), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
        }

        [Test]
        public void Output_RoundtripsThroughSerializers()
        {
            // F11.1: throw is now probabilistic (ThrowChance 0.05 default) —
            // this test only cares about JSON round-trips, so force the old
            // deterministic throw.
            var brain = new BotBrain { ThrowChance = 1f };
            string json = brain.Decide(Versus(5f, true, 80f), BrainManifest());
            // ThrowWeapon in range 8: thrown at mid distance.
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("ThrowWeapon", plan.actions[0].type);
            // And the boss snapshot itself serializes.
            string state = CombatStateSerializer.ToJson(Versus(5f, true, 80f));
            Assert.IsTrue(CombatStateSerializer.FromJson(state).entities.Length == 2);
        }

        [Test]
        public void MidRange_ThrowIsRareResource()
        {
            // F11.1: with the default 5% chance, mid-range decisions must
            // almost never throw — prefer Run (close distance) / defensive
            // band. Across a deterministic seed sweep, ThrowWeapon should be
            // a small minority, and the rest should never be a throw.
            var s = Versus(5f, true, 80f);
            var m = BrainManifest();
            int throws = 0, runs = 0;
            for (int seed = 1; seed <= 100; seed++)
            {
                var brain = new BotBrain { Rng = new System.Random(seed) };
                var plan = LlmPlanResponse.FromJson(brain.Decide(s, m));
                if (plan.actions[0].type == "ThrowWeapon") throws++;
                else if (plan.actions[0].type == "Run") runs++;
            }
            Assert.LessOrEqual(throws, 15, "throw must be a rare minority (~5%)");
            Assert.Greater(runs, 60, "the default mid-range choice must be closing distance");
        }

        [Test]
        public void Deterministic_SameSnapshotSamePlan()
        {
            var s = Versus(2f, true, 80f);
            var m = BrainManifest();
            Assert.AreEqual(new BotBrain().Decide(s, m), new BotBrain().Decide(s, m));
        }

        [Test]
        public void Variety_NeverRepeatsAttackTwiceInARow()
        {
            var brain = new BotBrain();
            var s = Versus(2f, true, 80f);
            var m = BrainManifest();
            string first = LlmPlanResponse.FromJson(brain.Decide(s, m)).actions[0].type;
            string second = LlmPlanResponse.FromJson(brain.Decide(s, m)).actions[0].type;
            Assert.AreNotEqual(first, second);
        }

        // [F18] Weighted melee variety: armed -> AttackSword 50 / AttackKick 25 /
        // Punch 25 (AttackCombo share 0 while armed), unarmed -> AttackKick 40 /
        // Punch 40 / AttackCombo 20. Fresh seeded brains per roll keep the
        // anti-repeat gate inert so the empirical shares converge to the
        // configured ones (n=400, bands >= 3 binomial sigmas).
        [Test]
        public void MeleeVariety_WeightedShares()
        {
            var m = VarietyManifest();
            const int runs = 400;
            var armed = new System.Collections.Generic.Dictionary<string, int>();
            var unarmed = new System.Collections.Generic.Dictionary<string, int>();
            for (int seed = 1; seed <= runs; seed++)
            {
                var a = LlmPlanResponse.FromJson(
                    new BotBrain { Rng = new System.Random(seed) }.Decide(GruntVs(2f, true), m, "grunt"));
                Tally(armed, a.actions[0].type);
                var u = LlmPlanResponse.FromJson(
                    new BotBrain { Rng = new System.Random(seed) }.Decide(GruntVs(2f, false), m, "grunt"));
                Tally(unarmed, u.actions[0].type);
            }
            // Armed: 50 / 25 / 25, AttackCombo unreachable (share 0).
            AssertShare(armed, "AttackSword", runs, 0.36f, 0.64f);
            AssertShare(armed, "AttackKick", runs, 0.15f, 0.35f);
            AssertShare(armed, "Punch", runs, 0.15f, 0.35f);
            AssertShare(armed, "AttackCombo", runs, 0f, 0f);
            // Unarmed: 40 / 40 / 20.
            AssertShare(unarmed, "AttackKick", runs, 0.28f, 0.52f);
            AssertShare(unarmed, "Punch", runs, 0.28f, 0.52f);
            AssertShare(unarmed, "AttackCombo", runs, 0.10f, 0.30f);
        }

        private static Game.Actions.ActionManifest VarietyManifest()
        {
            var m = ScriptableObject.CreateInstance<Game.Actions.ActionManifest>();
            var defs = new[]
            {
                Def("Idle", 0f, 0f, false, 0, "Idle"),
                Def("IdleFight", 0f, 0f, false, 0, "Idle"),
                Def("Run", 0f, 5f, false, 0, "Idle"),
                Def("Punch", 2f, 10f, false, 0, "Idle"),
                Def("AttackKick", 2f, 10f, false, 0, "Idle"),
                Def("AttackCombo", 2f, 10f, false, 0, "Idle"),
                Def("AttackSword", 2.2f, 12f, true, 0, "Idle"),
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

        private static CombatStateSnapshot GruntVs(float dist, bool weapon)
        {
            return new CombatStateSnapshot
            {
                phase = "Executing",
                entities = new[]
                {
                    new EntityState
                    {
                        id = "grunt", isBoss = false, isTargetable = true,
                        position = new[] { 0f, 0f, 0f }, health = 20f, maxHealth = 20f,
                        stamina = 80f, weaponEquipped = weapon
                    },
                    new EntityState
                    {
                        id = "player", isPlayerAvatar = true, isTargetable = true,
                        position = new[] { dist, 0f, 0f }, health = 100f, maxHealth = 100f,
                        stamina = 60f, weaponEquipped = true
                    },
                },
                distances = new[] { new EntityDistance { fromId = "grunt", toId = "player", distance = dist } }
            };
        }

        private static void Tally(System.Collections.Generic.Dictionary<string, int> t, string id)
        {
            int n;
            t.TryGetValue(id, out n);
            t[id] = n + 1;
        }

        private static void AssertShare(System.Collections.Generic.Dictionary<string, int> t, string id,
            int runs, float minShare, float maxShare)
        {
            int n = 0;
            t.TryGetValue(id, out n);
            float share = (float)n / runs;
            Assert.GreaterOrEqual(share, minShare, id + " share too low: " + share.ToString("F3"));
            Assert.LessOrEqual(share, maxShare, id + " share too high: " + share.ToString("F3"));
        }

        // ===== [F52] Strategist personas (BotBrain layer, pure/deterministic) =====

        // Distance: mid range with the laser ready -> straight laser, never a
        // Run approach. KiteManifest mirrors the REAL post-F49 reach (12m).
        [Test]
        public void Distance_LaserReady_At8m_CastsInsteadOfApproach()
        {
            string json = new BotBrain { Persona = BossPersona.Distance, ThrowChance = 0f }
                .Decide(Versus(8f, true, 80f), KiteManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserBeam", plan.actions[0].type);
        }

        // Distance: both lasers on cooldown at 8m -> NEVER Run-approaches;
        // holds ground (IdleFight) until a beam comes off cooldown.
        [Test]
        public void Distance_LasersDown_At8m_NeverApproaches()
        {
            string json = new BotBrain
            {
                Persona = BossPersona.Distance,
                ThrowChance = 0f,
                RemainingCooldownOf = id =>
                    id == "BossLaserBeam" || id == "BossLaserTornado" ? 9f : 0f
            }.Decide(Versus(8f, true, 80f), KiteManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("IdleFight", plan.actions[0].type);
            foreach (var a in plan.actions)
            {
                Assert.AreNotEqual("Run", a.type);
                Assert.AreNotEqual("Walk", a.type);
                Assert.AreNotEqual("BossLaserBeam", a.type);
            }
        }

        // Distance: player inside 5m -> escape. With Retreat wired the escape
        // is a backward-walk step (negative TravelDistance along forward).
        [Test]
        public void Distance_Pressured_RetreatsWhenWired()
        {
            string json = new BotBrain { Persona = BossPersona.Distance, ThrowChance = 0f }
                .Decide(Versus(3f, true, 80f), KiteManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("Retreat", plan.actions[0].type);
        }

        // Distance: without the Retreat def the escape degrades to dodge-spam.
        [Test]
        public void Distance_Pressured_EscapesWithDodgeWhenNoRetreat()
        {
            string json = new BotBrain { Persona = BossPersona.Distance, ThrowChance = 0f }
                .Decide(Versus(3f, true, 80f), KiteManifest(withRetreat: false));
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossDodge", plan.actions[0].type);
        }

        // Distance: furia keeps the expanded tornado window (2,9] and its
        // priority over the straight laser — at 8m ENRAGED the tornado wins
        // (the non-fury answer at 8m is the straight laser).
        [Test]
        public void Distance_Furia_At8m_TornadoPriorityKeeps()
        {
            string json = new BotBrain
            {
                Persona = BossPersona.Distance, ThrowChance = 0f, Furious = true
            }.Decide(Versus(8f, true, 80f), KiteManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossLaserTornado", plan.actions[0].type);
        }

        // Distance: the laser respects the def's REACH (12m) — beyond it the
        // persona does not waste a cast the beam cannot complete; holds ground.
        [Test]
        public void Distance_LaserBeyondDefReach_HoldsGround()
        {
            string json = new BotBrain { Persona = BossPersona.Distance, ThrowChance = 0f }
                .Decide(Versus(13f, true, 80f), KiteManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("IdleFight", plan.actions[0].type);
        }

        // Distance: the F51 orb rule runs BEFORE the kite overrides — a
        // wounded Distance boss still goes looting (personas respect it).
        [Test]
        public void Distance_WoundedWithOrb_SeeksHealthFirst()
        {
            var orb = new GameObject("HealthOrbDistance");
            try
            {
                orb.transform.position = new Vector3(0f, 0f, 3f);
                orb.AddComponent<Game.Combat.HealthPickup>();
                string json = new BotBrain { Persona = BossPersona.Distance, ThrowChance = 0f }
                    .Decide(Versus(8f, true, 80f, bossHealth: 100f), KiteManifest());
                var plan = LlmPlanResponse.FromJson(json);
                Assert.IsTrue(plan.IsValid());
                Assert.AreEqual("Run", plan.actions[0].type);
                Assert.AreEqual("HealthOrbDistance", plan.actions[0].target);
            }
            finally { Object.DestroyImmediate(orb); }
        }

        // Fighter: unarmed + sword in map -> the sword comes FIRST, even in
        // melee range (elevated above the melee chain; classic 2b behind it).
        [Test]
        public void Fighter_UnarmedSwordInMap_GoesForSwordFirst()
        {
            var snap = Versus(2f, false, 80f);
            snap.swordState = "InMap";
            string json = new BotBrain { Persona = BossPersona.Fighter, ThrowChance = 0f }
                .Decide(snap, BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("Run", plan.actions[0].type);
        }

        // Fighter: armed at (6, 8] -> DETERMINISTIC throw (ThrowChance=0
        // disables the F11.1 gamble and the persona rule still fires).
        [Test]
        public void Fighter_ArmedMidRange_ThrowsDeterministically()
        {
            string json = new BotBrain { Persona = BossPersona.Fighter, ThrowChance = 0f }
                .Decide(Versus(7f, true, 80f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("ThrowWeapon", plan.actions[0].type);
        }

        // Furia x Fighter: the melee chain keeps fury's heavy-first bias —
        // the persona never downgrades the enrage cadence (cadence/speed are
        // controller-side; the BossChain order stays).
        [Test]
        public void Furia_Fighter_InRange_HeavyFirstChainKeeps()
        {
            string json = new BotBrain
            {
                Persona = BossPersona.Fighter, ThrowChance = 0f, Furious = true
            }.Decide(Versus(2f, true, 80f), BrainManifest());
            var plan = LlmPlanResponse.FromJson(json);
            Assert.IsTrue(plan.IsValid());
            Assert.AreEqual("BossHeavyAttack", plan.actions[0].type);
            Assert.GreaterOrEqual(plan.actions.Length, 2);
        }

        // Persona None vs Normal: byte-identical output (the strategist's
        // "balanced" answer must not change classic behavior).
        [Test]
        public void PersonaNormal_MatchesClassicRules()
        {
            var classic = new BotBrain { ThrowChance = 0f }
                .Decide(Versus(10f, true, 80f), LaserManifest());
            var normal = new BotBrain { Persona = BossPersona.Normal, ThrowChance = 0f }
                .Decide(Versus(10f, true, 80f), LaserManifest());
            Assert.AreEqual(classic, normal);
        }

        // [F52] Kite manifest: BrainManifest-equivalent + the lasers at the
        // REAL post-F49 reach (12m) + BossDodge + optional Retreat def.
        private static Game.Actions.ActionManifest KiteManifest(bool withRetreat = true)
        {
            var m = ScriptableObject.CreateInstance<Game.Actions.ActionManifest>();
            var defs = new System.Collections.Generic.List<Game.Actions.ActionDefinition>
            {
                Def("Idle", 0f, 0f, false, 0, "Idle"),
                Def("IdleFight", 0f, 0f, false, 0, "Idle"),
                Def("Run", 0f, 5f, false, 0, "Idle"),
                Def("Walk", 0f, 5f, false, 0, "Idle"),
                Def("Punch", 2f, 10f, false, 0, "Idle"),
                Def("AttackSword", 2.2f, 12f, true, 0, "Idle"),
                Def("ThrowWeapon", 8f, 20f, true, 0, "Idle"),
                Def("BossHeavyAttack", 2.5f, 25f, false, 1, "Idle"),
                // No-target defs (dodge/retreat/lasers: needsTarget false).
                NoTargetDef("BossDodge", 0f, 15f, 1),
                NoTargetDef("BossLaserBeam", 12f, 30f, 1),
                NoTargetDef("BossLaserTornado", 12f, 35f, 1),
            };
            if (withRetreat) defs.Add(NoTargetDef("Retreat", 0f, 10f, 1));
            var so = new SerializedObject(m);
            var p = so.FindProperty("actions");
            p.arraySize = defs.Count;
            for (int i = 0; i < defs.Count; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.FindProperty("maxPlanSize").intValue = 6;
            so.ApplyModifiedPropertiesWithoutUndo();
            return m;
        }

        // needsTarget false + BossOnly-capable def (dodge/retreat/lasers).
        private static Game.Actions.ActionDefinition NoTargetDef(
            string id, float range, float stamina, int role)
        {
            var def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("range").floatValue = range;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("requiresWeapon").boolValue = false;
            so.FindProperty("needsTarget").boolValue = false;
            so.FindProperty("roleRestriction").intValue = role;
            so.FindProperty("clipName").stringValue = "Idle";
            so.FindProperty("fallbackActionId").stringValue = "Idle";
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }
    }
}
