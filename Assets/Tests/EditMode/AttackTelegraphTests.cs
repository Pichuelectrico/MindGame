using Game.VFX;
using NUnit.Framework;

namespace Game.Tests
{
    public sealed class AttackTelegraphTests
    {
        [Test]
        public void Categorize_MeleeExactIds_AreRed()
        {
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("AttackSword"));
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("AttackKick"));
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("AttackCombo"));
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("Punch"));
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("BossHeavyAttack"));
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("MutantPunch"));
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("SuperPunch"));
        }

        [Test]
        public void Categorize_MeleeContains_IsRed()
        {
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("Enemy_AttackSword_Heavy"));
            Assert.AreEqual(AttackTelegraphKind.Melee, AttackTelegraph.Categorize("superpunch"));
        }

        [Test]
        public void Categorize_Throw_IsYellow()
        {
            Assert.AreEqual(AttackTelegraphKind.Throw, AttackTelegraph.Categorize("ThrowWeapon"));
            Assert.AreEqual(AttackTelegraphKind.Throw, AttackTelegraph.Categorize("KnifeThrow"));
            Assert.AreEqual(AttackTelegraphKind.Throw, AttackTelegraph.Categorize("throw"));
        }

        [Test]
        public void Categorize_ThrowBeatsMeleeTokenIfBothPresent()
        {
            // Defensive: throw keyword wins so a hypothetical "ThrowPunch" stays yellow.
            Assert.AreEqual(AttackTelegraphKind.Throw, AttackTelegraph.Categorize("ThrowPunch"));
        }

        [Test]
        public void Categorize_NonAttack_IsNone()
        {
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize(null));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize(""));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("Idle"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("Roll"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("Dodge"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("Run"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("Walk"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("Block"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("GoPickupWeapon"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("GoTakeHealth"));
            Assert.AreEqual(AttackTelegraphKind.None, AttackTelegraph.Categorize("Death"));
        }
    }
}
