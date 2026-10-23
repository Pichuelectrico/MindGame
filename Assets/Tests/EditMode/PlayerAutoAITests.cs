using Game.Actions;
using Game.Combat;
using Game.Core;
using Game.Execution;
using Game.GameMode;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Inside Game.Tests the bare identifier "GameMode" resolves to the sibling
// NAMESPACE Game.GameMode (namespace beats using), so the enum needs an alias.
using GameModeEnum = Game.GameMode.GameMode;

namespace Game.Tests
{
    // [Auto-NPC] (playtest 2026-10-14): in Commands (auto) mode the player
    // avatar is a basic NPC; wheel instructions (R) are additional instructions
    // that must never be interrupted. Manual mode must leave PlayerAutoAI fully
    // passive. EditMode note: Update/OnEnable don't run here, so tests drive
    // PlayerAutoAI.Tick(dt) directly and let it self-resolve its refs.
    public sealed class PlayerAutoAITests
    {
        private static ActionDefinition Def(string id, float duration, float stamina,
            float range, bool heavy, string fallback)
        {
            var def = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new SerializedObject(def);
            so.FindProperty("actionId").stringValue = id;
            so.FindProperty("duration").floatValue = duration;
            so.FindProperty("staminaCost").floatValue = stamina;
            so.FindProperty("range").floatValue = range;
            so.FindProperty("fallbackActionId").stringValue = fallback;
            so.FindProperty("isHeavy").boolValue = heavy;
            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static ActionManifest Manifest()
        {
            var m = ScriptableObject.CreateInstance<ActionManifest>();
            var defs = new[]
            {
                Def("Idle", 1f, 0f, 0f, false, "Idle"),
                Def("Punch", 1.1f, 0f, 3f, false, "Idle"),
                Def("AttackKick", 2.7f, 0f, 3f, true, "Idle"),
                Def("IdleFight", 1f, 0f, 0f, false, "Idle"),
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

        private sealed class Rig
        {
            public GameObject Player;
            public ActionExecutor Exec;
            public HealthSystem Hp;
            public PlayerAutoAI AI;
            public GameObject Enemy;
            public TimeController Time;
            public GameModeManager Modes;
        }

        private static Rig MakeRig(ActionManifest m)
        {
            var rig = new Rig();

            rig.Player = new GameObject("Player_YBot");
            rig.Player.transform.position = new Vector3(400f, 0f, 400f); // rig lejos del origen: la escena real (boss a 0.18m del origen) no debe interferir
            rig.Exec = rig.Player.AddComponent<ActionExecutor>();
            rig.Exec.BindDriver(new StubDriver());
            rig.Exec.BindManifest(m);
            rig.Exec.Stamina = 100f;
            rig.Hp = rig.Player.AddComponent<HealthSystem>();
            rig.AI = rig.Player.AddComponent<PlayerAutoAI>();

            // Enemy in Punch range (2 <= 3) with living HealthSystem: BotBrain
            // should pick Punch for the player (PlayerOnly role).
            rig.Enemy = new GameObject("Grunt");
            rig.Enemy.transform.position = new Vector3(402f, 0f, 400f);
            rig.Enemy.AddComponent<HealthSystem>();

            rig.Time = rig.Player.AddComponent<TimeController>();
            rig.Modes = rig.Player.AddComponent<GameModeManager>();

            // SerializedObject wiring mirrors SandboxBuilder (manifest asset +
            // tuning); executor/health/time/modes resolve in Tick via refs.
            var so = new SerializedObject(rig.AI);
            so.FindProperty("manifest").objectReferenceValue = m;
            so.FindProperty("decisionInterval").floatValue = 0.5f;
            so.FindProperty("resumeGraceTicks").intValue = 2;
            so.ApplyModifiedPropertiesWithoutUndo();

            return rig;
        }

        private static void Teardown(Rig rig)
        {
            Object.DestroyImmediate(rig.Player);
            Object.DestroyImmediate(rig.Enemy);
        }

        [Test]
        public void IdleInCommandsMode_LoadsPlan()
        {
            var m = Manifest();
            var rig = MakeRig(m);
            Assert.AreEqual(GameModeEnum.Commands, rig.Modes.CurrentMode);

            rig.AI.Tick(0.6f); // crosses the 0.5s decision cadence

            Assert.IsTrue(rig.Exec.IsRunning, "AI should have loaded and started a plan on the idle executor");
            Assert.IsNotNull(rig.Exec.Current, "Executor should be running the AI-decided action");
            Teardown(rig);
        }

        [Test]
        public void RunningPlan_IsNeverInterrupted()
        {
            var m = Manifest();
            var rig = MakeRig(m);

            // Wheel-sim: user launches a plan from the R-wheel (same executor).
            Assert.IsTrue(rig.Exec.LoadPlan("{\"actions\":[{\"type\":\"Punch\"},{\"type\":\"Punch\"}]}"));
            rig.Exec.StartPlan();
            var wheelInstance = rig.Exec.Current;
            Assert.IsNotNull(wheelInstance);

            rig.AI.Tick(0.6f); // AI sees a running plan: must not touch it
            rig.AI.Tick(0.6f);

            Assert.IsTrue(rig.Exec.IsRunning, "Wheel plan must still be running");
            Assert.AreSame(wheelInstance, rig.Exec.Current,
                "AI must not replace the wheel's queued action while it runs");
            Teardown(rig);
        }

        [Test]
        public void ManualMode_StaysFullyPassive()
        {
            var m = Manifest();
            var rig = MakeRig(m);
            rig.Modes.SetMode(GameModeEnum.Manual);

            rig.AI.Tick(0.6f);
            rig.AI.Tick(0.6f);

            Assert.IsFalse(rig.Exec.IsRunning, "Manual mode: AI must not load plans");
            Assert.IsNull(rig.Exec.Current, "Manual mode: executor must stay idle (manual controller rules)");
            Teardown(rig);
        }

        // [F8] GruntSpawner keeps a static boss registry (bossRoot/instance/
        // PlayerRoot). Tests drive it via reflection — field OR property,
        // PlayerRoot is a public static auto-property with a private setter —
        // and always clean it up in finally, even when setup asserts fail
        // (a leaked rig + GameModeManager cascaded into ManualMode once).
        private static void SetGruntSpawnerStatic(string name, object value)
        {
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static;
            var field = typeof(GruntSpawner).GetField(name, Flags);
            if (field != null)
            {
                field.SetValue(null, value);
                return;
            }
            var prop = typeof(GruntSpawner).GetProperty(name, Flags);
            Assert.IsNotNull(prop, "GruntSpawner." + name + " must exist (static field or property)");
            prop.SetValue(null, value, null); // private setters are reachable via reflection
        }

        [Test]
        public void DormantBoss_ExcludedFromSnapshot_AwakenBoss_Included()
        {
            var m = Manifest();
            var rig = MakeRig(m);
            Object.DestroyImmediate(rig.Enemy); // arena with ONLY the boss besides the player

            var bossGo = new GameObject("Boss_Mutant");
            bossGo.transform.position = new Vector3(402f, 0f, 400f); // inside Punch range once awake
            bossGo.AddComponent<HealthSystem>();

            var spawner = rig.Player.AddComponent<GruntSpawner>(); // EditMode: AddComponent does NOT run Awake

            // Static wiring lives INSIDE the try: a failing assert during
            // setup must never leak the rig, the boss or the GruntSpawner
            // statics into the next test (alphabetical ordering runs this
            // fixture's tests in sequence).
            try
            {
                SetGruntSpawnerStatic("instance", spawner); // IsDormantBoss needs the singleton gate
                SetGruntSpawnerStatic("bossRoot", bossGo);
                SetGruntSpawnerStatic("PlayerRoot", rig.Player.transform); // PlayerRoot is a Transform, rig.Player is its GameObject

                // DORMANT: the boss is not a target -> no threat -> IdleFight only.
                rig.AI.Tick(0.6f);
                Assert.IsNotNull(rig.Exec.Current, "AI should still act (IdleFight) in a dormant-only arena");
                Assert.AreEqual("IdleFight", rig.Exec.Current.Action.Definition.ActionId,
                    "Auto-player must NOT target the dormant boss");
                rig.Exec.Tick(1.5f); // let the IdleFight plan finish (1s duration)
                Assert.IsFalse(rig.Exec.IsRunning);

                // AWAKEN: WakeBoss flips the gate -> the boss becomes a valid target.
                spawner.WakeBoss();
                rig.AI.Tick(0.6f);
                Assert.IsNotNull(rig.Exec.Current, "AI should attack the awakened boss");
                // Contract is "strikes it", not which melee: Punch (light) and
                // AttackKick (heavy) are both in range and affordable, so the
                // BotBrain's weighted draw may pick either — don't pin the RNG.
                var pickedId = rig.Exec.Current.Action.Definition.ActionId;
                Assert.IsTrue(pickedId == "Punch" || pickedId == "AttackKick",
                    "With the boss awake and in melee range, the AI should strike it, but picked: " + pickedId);
            }
            finally
            {
                SetGruntSpawnerStatic("bossRoot", null);
                SetGruntSpawnerStatic("instance", null);
                SetGruntSpawnerStatic("PlayerRoot", null);
                Object.DestroyImmediate(bossGo); // the boss was leaking into later tests
                Teardown(rig);
            }
        }

        [Test]
        public void RecoveryUntilTime_Future_SkipsDecision_ThenResumes()
        {
            var m = Manifest();
            var rig = MakeRig(m);

            rig.AI.RecoveryUntilTimeReader = _ => 999f; // simulate an active recovery window
            rig.AI.Tick(0.6f);
            Assert.IsNull(rig.Exec.Current, "During recovery the AI must not load plans");

            rig.AI.RecoveryUntilTimeReader = _ => 0f; // recovery cleared
            rig.AI.Tick(0.6f);
            Assert.IsNotNull(rig.Exec.Current, "After recovery the AI resumes deciding");

            Teardown(rig);
        }
    }
}
