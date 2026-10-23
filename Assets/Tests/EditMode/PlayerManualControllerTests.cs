using NUnit.Framework;
using UnityEngine;
using Game.GameMode;
using Game.Execution;

namespace Game.Tests
{
    public sealed class PlayerManualControllerTests
    {
        private GameObject playerGo;
        private PlayerManualController controller;
        private ActionExecutor executor;

        [SetUp]
        public void SetUp()
        {
            playerGo = new GameObject("PlayerManualControllerTestPlayer");
            executor = playerGo.AddComponent<ActionExecutor>();
            controller = playerGo.AddComponent<PlayerManualController>();
            controller.Bind(executor);
        }

        [TearDown]
        public void TearDown()
        {
            if (playerGo != null)
            {
                Object.DestroyImmediate(playerGo);
            }
        }

        [Test]
        public void DefaultValues_MatchFeatureSpecifications()
        {
            // F32: 0.8s attack cooldown
            Assert.AreEqual(0.8f, controller.AttackCooldownSeconds, 0.001f);

            // F33: Q maps to BlockCenter
            Assert.AreEqual("BlockCenter", controller.BlockActionId);

            // F44: Sword attack action id defaults to AttackSword
            Assert.AreEqual("AttackSword", controller.SwordAttackActionId);
        }

        [Test]
        public void AttackCooldown_TriggersAndResetsCorrectly()
        {
            Assert.IsFalse(controller.IsAttackOnCooldown);

            controller.TriggerAttackCooldown();
            Assert.IsTrue(controller.IsAttackOnCooldown);
            Assert.Greater(controller.NextAttackTime, Time.time);

            controller.ResetAttackCooldown();
            Assert.IsFalse(controller.IsAttackOnCooldown);
        }

        [Test]
        public void BlockActionId_CanBeConfigured()
        {
            controller.BlockActionId = "BlockLeft";
            Assert.AreEqual("BlockLeft", controller.BlockActionId);
        }

        [Test]
        public void TryExecuteSwordSlash_WhenUnarmed_ReturnsFalse_AndDoesNotTriggerCooldown()
        {
            executor.HasWeapon = false;
            executor.Stamina = 100f;

            bool result = controller.TryExecuteSwordSlash();

            Assert.IsFalse(result);
            Assert.IsFalse(controller.IsAttackOnCooldown);
        }

        [Test]
        public void TryExecuteSwordSlash_WhenArmed_Executes_AndEnforcesCooldown()
        {
            executor.HasWeapon = true;
            executor.Stamina = 50f;

            Assert.IsNotNull(executor, "executor is null");
            Assert.IsFalse(executor.IsRunning, "executor.IsRunning is true");
            Assert.IsTrue(executor.HasWeapon, "executor.HasWeapon is false");
            Assert.GreaterOrEqual(executor.Stamina, 12f, "stamina < 12");
            Assert.IsFalse(controller.IsAttackOnCooldown, "controller is on cooldown");

            bool firstSlash = controller.TryExecuteSwordSlash();
            Assert.IsTrue(firstSlash, "firstSlash returned false");
            Assert.IsTrue(controller.IsAttackOnCooldown);

            // Second slash immediately during cooldown is rejected
            bool secondSlash = controller.TryExecuteSwordSlash();
            Assert.IsFalse(secondSlash);

            // Resetting cooldown and finishing current action allows slashing again
            executor.StopPlan();
            controller.ResetAttackCooldown();
            Assert.IsFalse(controller.IsAttackOnCooldown);

            bool thirdSlash = controller.TryExecuteSwordSlash();
            Assert.IsTrue(thirdSlash);
        }

        [Test]
        public void TryExecuteSwordSlash_WhenArmedButLowStamina_ReturnsFalse()
        {
            executor.HasWeapon = true;
            executor.Stamina = 5f; // requires 12

            bool result = controller.TryExecuteSwordSlash();
            Assert.IsFalse(result);
            Assert.IsFalse(controller.IsAttackOnCooldown);
        }
    }
}
