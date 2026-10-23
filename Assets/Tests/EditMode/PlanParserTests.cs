using Game.Actions;
using Game.Planning;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests
{
    public sealed class PlanParserTests
    {
        private static ActionDefinition Def(string id, bool needsTarget, params string[] aliases)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("fallbackActionId").stringValue = "Idle";
            so.FindProperty("needsTarget").boolValue = needsTarget;
            var ap = so.FindProperty("aliases");
            ap.arraySize = aliases.Length;
            for (int i = 0; i < aliases.Length; i++)
                ap.GetArrayElementAtIndex(i).stringValue = aliases[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static ActionManifest TestManifest()
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var defs = new[]
            {
                Def("Idle", false, "idle", "quieto"),
                Def("Dodge", false, "dodge", "esquivar", "esquiva"),
                Def("AttackSword", true, "attack", "ataca", "atacar", "sword"),
                Def("ThrowWeapon", true, "throw", "lanza", "lanzar"),
                Def("BlockCenter", false, "block", "bloqueo", "bloquear", "bloquea"),
                Def("Jump", false, "jump", "salta", "saltar"),
                Def("Run", false, "run", "corre", "correr"),
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

        [Test]
        public void LiteralUserExample_ParsesToCanonicalJson()
        {
            var parser = new PlanParser();
            var r = parser.Parse(
                "esquiva, ataca al de la derecha y lanza la espada al del centro",
                TestManifest());
            Assert.IsTrue(r.Success, string.Join("; ", r.Errors.ToArray()));
            Assert.AreEqual(
                "{\"actions\":[{\"type\":\"Dodge\"},{\"type\":\"AttackSword\",\"target\":\"right\"},{\"type\":\"ThrowWeapon\",\"target\":\"center\"}]}",
                r.PlanJson);
        }

        [Test]
        public void EnglishCommands_Work()
        {
            var r = new PlanParser().Parse("dodge then attack right", TestManifest());
            Assert.IsTrue(r.Success);
            Assert.AreEqual(
                "{\"actions\":[{\"type\":\"Dodge\"},{\"type\":\"AttackSword\",\"target\":\"right\"}]}",
                r.PlanJson);
        }

        [Test]
        public void UnknownCommand_FallsBackToBlockCenter_WithWarning()
        {
            var r = new PlanParser().Parse("baila un tango", TestManifest());
            Assert.IsTrue(r.Success);
            Assert.AreEqual("{\"actions\":[{\"type\":\"BlockCenter\"}]}", r.PlanJson);
            Assert.AreEqual(1, r.Warnings.Count);
            Assert.AreEqual(1, r.AppliedFallbacks.Count);
        }

        [Test]
        public void MoreThanMaxPlanSize_IsError()
        {
            var r = new PlanParser().Parse(
                "salta, corre, esquiva, salta, corre, esquiva, salta", TestManifest());
            Assert.IsFalse(r.Success);
            Assert.AreEqual(1, r.Errors.Count);
        }

        [Test]
        public void Aliases_AreCaseInsensitive_NoTargetWhenNotNeeded()
        {
            var r = new PlanParser().Parse("ESQUIVA a la Izquierda", TestManifest());
            Assert.IsTrue(r.Success);
            // Dodge doesn't need a target: key omitted even though direction given.
            Assert.AreEqual("{\"actions\":[{\"type\":\"Dodge\"}]}", r.PlanJson);
        }

        [Test]
        public void EmptyInput_IsError()
        {
            Assert.IsFalse(new PlanParser().Parse("", TestManifest()).Success);
            Assert.IsFalse(new PlanParser().Parse("   ", TestManifest()).Success);
            Assert.IsFalse(new PlanParser().Parse(null, TestManifest()).Success);
        }

        private const string RealManifestPath = "Assets/Data/Actions/ActionManifest.asset";

        private static ActionManifest RealManifest()
        {
            var m = AssetDatabase.LoadAssetAtPath<ActionManifest>(RealManifestPath);
            Assert.IsNotNull(m, "missing " + RealManifestPath);
            return m;
        }

        private static string ParseOk(string input)
        {
            var r = new PlanParser().Parse(input, RealManifest());
            Assert.IsTrue(r.Success, string.Join("; ", r.Errors.ToArray()));
            return r.PlanJson;
        }

        private const string RunPunchDodge =
            "{\"actions\":[{\"type\":\"Run\"},{\"type\":\"Punch\"},{\"type\":\"Dodge\"}]}";

        [Test]
        public void SpaceSeparated_EqualsCommaSeparated()
        {
            Assert.AreEqual(RunPunchDodge, ParseOk("corre, golpe, esquiva"));
            Assert.AreEqual(RunPunchDodge, ParseOk("corre golpe esquiva"));
        }

        [Test]
        public void MixedCommaAndSpaceSeparated()
        {
            Assert.AreEqual(RunPunchDodge, ParseOk("corre, golpe esquiva"));
        }

        [Test]
        public void SpaceSeparated_DirectionPhraseStaysWithItsAction()
        {
            Assert.AreEqual(
                "{\"actions\":[{\"type\":\"Run\"},{\"type\":\"AttackSword\",\"target\":\"right\"},{\"type\":\"Dodge\"}]}",
                ParseOk("corre ataca al de la derecha esquiva"));
        }

        [Test]
        public void SpaceSeparated_UppercaseAndMultipleSpaces()
        {
            Assert.AreEqual(RunPunchDodge, ParseOk("  CORRE    Golpe   ESQUIVA  "));
        }

        [Test]
        public void AliasAfterArticle_IsComplementNotNewAction()
        {
            // "espada" is an AttackSword alias, but here it is the object of "lanza".
            Assert.AreEqual(
                "{\"actions\":[{\"type\":\"ThrowWeapon\",\"target\":\"center\"}]}",
                ParseOk("lanza la espada al del centro"));
        }

        [Test]
        public void MultiWordAlias_WinsOverItsPrefix()
        {
            // "golpe recibido" (HitReaction) must not split into "golpe" (Punch) + "recibido".
            Assert.AreEqual(
                "{\"actions\":[{\"type\":\"HitReaction\"},{\"type\":\"Dodge\"}]}",
                ParseOk("golpe recibido esquiva"));
        }

        [Test]
        public void DirectionalBlockAlias_ResolvesBeforeDirectionIsStripped()
        {
            Assert.AreEqual("{\"actions\":[{\"type\":\"BlockLeft\"}]}", ParseOk("bloquea izquierda"));
            Assert.AreEqual("{\"actions\":[{\"type\":\"BlockCenter\"}]}", ParseOk("bloquea el centro"));
            Assert.AreEqual("{\"actions\":[{\"type\":\"BlockCenter\"}]}", ParseOk("bloquea centro"));
        }

        [Test]
        public void SpaceSeparated_MoreThanMaxPlanSize_IsError()
        {
            var r = new PlanParser().Parse(
                "salta corre esquiva salta corre esquiva salta", RealManifest());
            Assert.IsFalse(r.Success);
            Assert.AreEqual(1, r.Errors.Count);
        }
    }
}
