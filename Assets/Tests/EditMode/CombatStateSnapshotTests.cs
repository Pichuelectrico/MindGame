using Game.AI;
using UnityEngine;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class CombatStateSnapshotTests
    {
        private static CombatStateSnapshot Sample()
        {
            return new CombatStateSnapshot
            {
                phase = "Planning",
                elapsedTime = 12.5f,
                entities = new[]
                {
                    new EntityState
                    {
                        id = "boss", name = "Mutant", isBoss = true, isPlayerAvatar = false,
                        position = new[] { -1f, 0f, 0f }, rotation = new[] { 0f, 90f, 0f },
                        health = 200f, maxHealth = 200f, stamina = 80f,
                        weaponEquipped = false, isTargetable = true,
                        availableActions = new[] { "PreCombatIdle", "BossHeavyAttack", "Dodge" }
                    },
                    new EntityState
                    {
                        id = "player", name = "Y Bot", isBoss = false, isPlayerAvatar = true,
                        position = new[] { 1f, 0f, 2f }, rotation = new[] { 0f, -90f, 0f },
                        health = 100f, maxHealth = 100f, stamina = 60f,
                        weaponEquipped = true, isTargetable = true,
                        availableActions = new[] { "Idle", "AttackSword", "BlockLeft" }
                    }
                },
                distances = new[] { new EntityDistance { fromId = "boss", toId = "player", distance = 2.83f } }
            };
        }

        [Test]
        public void Roundtrip_PreservesAllFields()
        {
            var json = CombatStateSerializer.ToJson(Sample());
            var back = CombatStateSerializer.FromJson(json);
            Assert.AreEqual("Planning", back.phase);
            Assert.AreEqual(12.5f, back.elapsedTime, 1e-4f);
            Assert.AreEqual(2, back.entities.Length);
            Assert.AreEqual("boss", back.entities[0].id);
            Assert.IsTrue(back.entities[0].isBoss);
            Assert.AreEqual(new[] { -1f, 0f, 0f }, back.entities[0].position);
            Assert.IsTrue(back.entities[1].weaponEquipped);
            Assert.AreEqual(1, back.distances.Length);
            // [F38] 1-decimal wire format: 2.83 -> "2.8".
            Assert.AreEqual(2.8f, back.distances[0].distance, 0.05f);
            // [F38] Diet: rotation/availableActions/currentActionId are off the wire.
            StringAssert.DoesNotContain("rotation", json);
            StringAssert.DoesNotContain("availableActions", json);
            StringAssert.DoesNotContain("currentActionId", json);
            StringAssert.DoesNotContain("isTargetable", json);
        }

        [Test]
        public void OptionalFields_DefaultToSaneValues()
        {
            var back = CombatStateSerializer.FromJson("{}");
            Assert.AreEqual(string.Empty, back.phase ?? string.Empty);
            Assert.AreEqual(0f, back.elapsedTime);
            Assert.IsNotNull(back.entities);
            Assert.IsNotNull(back.distances);
        }

        [Test]
        public void NullAndEmptyInput_NeverReturnNull()
        {
            Assert.IsNotNull(CombatStateSerializer.FromJson(null));
            Assert.IsNotNull(CombatStateSerializer.FromJson(string.Empty));
            Assert.IsNotNull(CombatStateSerializer.ToJson(null));
        }

        [Test]
        public void MultipleEntities_AllSurviveRoundtrip()
        {
            var s = Sample();
            var extra = new EntityState { id = "minion1", name = "Minion", health = 30f, maxHealth = 30f };
            var all = new EntityState[s.entities.Length + 1];
            s.entities.CopyTo(all, 0);
            all[all.Length - 1] = extra;
            s.entities = all;
            var back = CombatStateSerializer.FromJson(CombatStateSerializer.ToJson(s));
            Assert.AreEqual(3, back.entities.Length);
            Assert.AreEqual("minion1", back.entities[2].id);
            Assert.AreEqual(30f, back.entities[2].health);
        }

        [Test]
        public void CurrentAction_Roundtrip_PreservesAttackFlagNotActionId()
        {
            var s = Sample();
            s.entities[1].currentActionId = "AttackSword";
            s.entities[1].isAttacking = true;
            var back = CombatStateSerializer.FromJson(CombatStateSerializer.ToJson(s));
            // [F38] Diet: isAttacking is on the wire (the prompt reacts to it);
            // currentActionId is not — the model never read it.
            Assert.IsTrue(back.entities[1].isAttacking);
            Assert.AreEqual(string.Empty, back.entities[1].currentActionId);
        }

        [Test]
        public void CurrentAction_DefaultsToEmptyAndNotAttacking()
        {
            Assert.AreEqual(string.Empty, new EntityState().currentActionId);
            Assert.IsFalse(new EntityState().isAttacking);

            var back = CombatStateSerializer.FromJson("{\"entities\":[{\"id\":\"boss\"}]}");
            Assert.AreEqual(1, back.entities.Length);
            Assert.AreEqual(string.Empty, back.entities[0].currentActionId);
            Assert.IsFalse(back.entities[0].isAttacking);

            var roundtrip = CombatStateSerializer.FromJson(CombatStateSerializer.ToJson(Sample()));
            Assert.AreEqual(string.Empty, roundtrip.entities[0].currentActionId);
            Assert.IsFalse(roundtrip.entities[0].isAttacking);
        }

        [Test]
        public void CurrentAction_AttackFlagDiffersPerEntity()
        {
            var s = Sample();
            s.entities[0].currentActionId = "BossDodge";
            s.entities[0].isAttacking = false;
            s.entities[1].currentActionId = "AttackSword";
            s.entities[1].isAttacking = true;
            var all = new EntityState[s.entities.Length + 1];
            s.entities.CopyTo(all, 0);
            all[all.Length - 1] = new EntityState { id = "minion1", currentActionId = "Punch", isAttacking = true };
            s.entities = all;

            var back = CombatStateSerializer.FromJson(CombatStateSerializer.ToJson(s));
            Assert.AreEqual(3, back.entities.Length);
            // [F38] The attack FLAG differs per entity on the wire; the action
            // id is dieted off (in-memory snapshots keep it — only the LLM
            // payload is compact).
            Assert.IsFalse(back.entities[0].isAttacking);
            Assert.IsTrue(back.entities[1].isAttacking);
            Assert.IsTrue(back.entities[2].isAttacking);
            Assert.AreEqual(string.Empty, back.entities[0].currentActionId);
            Assert.AreEqual(string.Empty, back.entities[2].currentActionId);
        }

        // [F38] Compaction contract: self (boss) + player + the 3 nearest
        // others survive; farther entities and their distance pairs are cut;
        // positions round to 1 decimal; unused fields never hit the wire.
        [Test]
        public void Compact_TruncatesToNearestFiveEntities()
        {
            var s = Sample();
            var grunts = new[]
            {
                new EntityState { id = "gruntA", name = "A", position = new[] { 1.234f, 0f, 0f }, health = 5f, maxHealth = 5f },
                new EntityState { id = "gruntB", name = "B", position = new[] { 10f, 0f, 0f }, health = 5f, maxHealth = 5f },
                new EntityState { id = "gruntC", name = "C", position = new[] { 20f, 0f, 0f }, health = 5f, maxHealth = 5f },
                new EntityState { id = "gruntD", name = "D", position = new[] { 2.567f, 0f, 0f }, health = 5f, maxHealth = 5f },
                new EntityState { id = "gruntE", name = "E", position = new[] { 30f, 0f, 0f }, health = 5f, maxHealth = 5f },
            };
            var all = new EntityState[s.entities.Length + grunts.Length];
            s.entities.CopyTo(all, 0);
            grunts.CopyTo(all, s.entities.Length);
            s.entities = all;
            s.distances = new[]
            {
                new EntityDistance { fromId = "boss", toId = "player", distance = 2.83f },
                new EntityDistance { fromId = "boss", toId = "gruntB", distance = 11f },
                new EntityDistance { fromId = "boss", toId = "gruntC", distance = 21f },
            };

            var json = CombatStateSerializer.ToJson(s);
            var back = CombatStateSerializer.FromJson(json);

            Assert.AreEqual(5, back.entities.Length); // boss + player + 3 nearest
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var e in back.entities) ids.Add(e.id);
            Assert.IsTrue(ids.Contains("boss"));
            Assert.IsTrue(ids.Contains("player"));
            Assert.IsTrue(ids.Contains("gruntA")); // 1.2m — nearest
            Assert.IsTrue(ids.Contains("gruntD")); // 3.6m
            Assert.IsTrue(ids.Contains("gruntB")); // 10m — 3rd nearest, still in budget
            Assert.IsFalse(ids.Contains("gruntC")); // 20m — cut
            Assert.IsFalse(ids.Contains("gruntE")); // 30m — cut
            // Distance pairs outside the kept set are gone (gruntC's pair cut,
            // gruntB's pair survives — both endpoints kept).
            Assert.AreEqual(2, back.distances.Length);
            Assert.AreEqual("boss", back.distances[0].fromId);
            Assert.AreEqual("player", back.distances[0].toId);
            Assert.AreEqual("gruntB", back.distances[1].toId);
            // Positions round to 1 decimal on the wire.
            StringAssert.Contains("1.2", json);
            StringAssert.Contains("2.6", json);
            StringAssert.DoesNotContain("1.234", json);
            StringAssert.DoesNotContain("2.567", json);
            // Unused fields never hit the wire.
            StringAssert.DoesNotContain("rotation", json);
            StringAssert.DoesNotContain("availableActions", json);
            StringAssert.DoesNotContain("currentActionId", json);
        }

        // [F38] The metric for the diet debate: same synthetic snapshot, old
        // full-fidelity JsonUtility JSON vs the new compact wire format.
        [Test]
        public void Compact_ReducesPayloadBytes()
        {
            var s = Sample();
            // Raw float noise like the in-game BuildSnapshot emits.
            s.entities[0].position = new[] { 12.345678f, 0.123456f, -4.5678901f };
            s.entities[0].rotation = new[] { 0f, 179.99998f, 0f };
            s.entities[1].position = new[] { -8.7654321f, 0.012345f, 3.456789f };
            s.elapsedTime = 123.45678f;

            string before = JsonUtility.ToJson(s);
            string after = CombatStateSerializer.ToJson(s);
            int beforeBytes = System.Text.Encoding.UTF8.GetByteCount(before);
            int afterBytes = System.Text.Encoding.UTF8.GetByteCount(after);
            float reduction = 100f * (beforeBytes - afterBytes) / beforeBytes;
            Debug.Log("[F38] snapshot payload before=" + beforeBytes + " bytes, after="
                + afterBytes + " bytes, reduction=" + reduction.ToString("0.0") + "%");

            Assert.Greater(beforeBytes, afterBytes);
            Assert.Greater(reduction, 20f); // diet must be substantial, not cosmetic
        }

        // [F45] Modo Furia on the wire: "furious":true rides ONLY on the enraged
        // entity; default/absent (false) never emits — keeps the compact payload.
        [Test]
        public void Compact_EmitsFuriousOnlyWhenTrue()
        {
            var s = Sample();
            s.entities[0].furious = true; // boss enraged, player calm

            var json = CombatStateSerializer.ToJson(s);
            StringAssert.Contains("\"furious\":true", json);
            StringAssert.DoesNotContain("\"furious\":false", json);

            // Round-trip: true survives JsonUtility; the untouched entity stays false.
            var back = CombatStateSerializer.FromJson(json);
            Assert.IsTrue(back.entities[0].furious);
            Assert.IsFalse(back.entities[1].furious);

            // No entity furious: the key is absent from the wire entirely.
            var calm = CombatStateSerializer.ToJson(Sample());
            StringAssert.DoesNotContain("furious", calm);
        }
    }
}
