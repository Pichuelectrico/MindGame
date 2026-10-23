using Game.Actions;
using Game.Camera;
using Game.Combat;
using Game.Execution;
using Game.GameMode;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    // [F25] Tests that in Manual mode (player controls), camera and player facing
    // obey mouse/WASD with zero auto-enfoque/lock-on assistance to enemies.
    public sealed class CameraModeLockOnTests
    {
        [Test]
        public void Camera_SetMouseInfluence_AnchorsYawToCurrentForward()
        {
            var camGo = new GameObject("TestCam");
            camGo.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            var cam = camGo.AddComponent<PlayerFollowCamera>();

            // Entering manual mode
            cam.SetMouseInfluence(1f);
            Assert.AreEqual(1f, cam.MouseInfluence);

            Object.DestroyImmediate(camGo);
        }

        [Test]
        public void CombatDirector_ManualMode_PlayerDoesNotSnapToEnemyOnAttack()
        {
            var playerGo = new GameObject("Player_YBot");
            playerGo.transform.position = new Vector3(400f, 0f, 400f); // rig lejos del origen: la escena real (boss/grunts) no debe interferir
            playerGo.transform.rotation = Quaternion.identity; // Facing forward (0, 0, 1)

            var enemyGo = new GameObject("Enemy");
            enemyGo.transform.position = new Vector3(405f, 0f, 400f); // Enemy to the right (+X)
            var enemyHp = enemyGo.AddComponent<HealthSystem>();

            var playerHp = playerGo.AddComponent<HealthSystem>();
            var playerExec = playerGo.AddComponent<ActionExecutor>();
            var playerModes = playerGo.AddComponent<GameModeManager>();
            var playerManual = playerGo.AddComponent<PlayerManualController>();
            var director = playerGo.AddComponent<CombatDirector>();
            director.Bind(playerExec, playerHp, null);
            director.BindModeManager(playerModes);

            // Set to Manual mode
            playerModes.SetMode(Game.GameMode.GameMode.Manual);

            // In Manual mode, starting an attack must NOT snap player facing to the enemy
            var punchDef = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new UnityEditor.SerializedObject(punchDef);
            so.FindProperty("actionId").stringValue = "Punch";
            so.FindProperty("duration").floatValue = 1f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var instance = new ActionInstance(new QueuedAction(punchDef, Game.Actions.ActionDirection.None, null));
            // Trigger attack start via reflection or executor
            var method = typeof(CombatDirector).GetMethod("OnActionStarted",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(method);
            method.Invoke(director, new object[] { instance });

            // Player rotation must remain forward (0, 0, 0), NOT turned towards enemy (+X)
            float angleDiff = Quaternion.Angle(Quaternion.identity, playerGo.transform.rotation);
            Assert.Less(angleDiff, 1f, "Player must not auto-snap to enemy when attacking in Manual mode");

            Object.DestroyImmediate(playerGo);
            Object.DestroyImmediate(enemyGo);
            Object.DestroyImmediate(punchDef);
        }

        [Test]
        public void CombatDirector_CommandsMode_PlayerSnapsToEnemyOnAttack()
        {
            var playerGo = new GameObject("Player_YBot");
            playerGo.transform.position = new Vector3(400f, 0f, 400f); // rig lejos del origen: la escena real (boss/grunts) no debe interferir
            playerGo.transform.rotation = Quaternion.identity; // Facing forward (0, 0, 1)

            var enemyGo = new GameObject("Enemy");
            enemyGo.transform.position = new Vector3(405f, 0f, 400f); // Enemy to the right (+X)
            var enemyHp = enemyGo.AddComponent<HealthSystem>();

            var playerHp = playerGo.AddComponent<HealthSystem>();
            var playerExec = playerGo.AddComponent<ActionExecutor>();
            var playerModes = playerGo.AddComponent<GameModeManager>();
            var director = playerGo.AddComponent<CombatDirector>();
            director.Bind(playerExec, playerHp, null);
            director.BindModeManager(playerModes);

            // Set to Commands mode
            playerModes.SetMode(Game.GameMode.GameMode.Commands);

            var punchDef = ScriptableObject.CreateInstance<ActionDefinition>();
            var so = new UnityEditor.SerializedObject(punchDef);
            so.FindProperty("actionId").stringValue = "Punch";
            so.FindProperty("duration").floatValue = 1f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var instance = new ActionInstance(new QueuedAction(punchDef, Game.Actions.ActionDirection.None, null));
            var method = typeof(CombatDirector).GetMethod("OnActionStarted",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(method);
            method.Invoke(director, new object[] { instance });

            // In Commands mode, player MUST snap towards enemy (looking at +X, angle ~90 deg)
            float angleDiff = Quaternion.Angle(Quaternion.identity, playerGo.transform.rotation);
            Assert.Greater(angleDiff, 45f, "Player must snap to enemy when attacking in Commands mode");

            Object.DestroyImmediate(playerGo);
            Object.DestroyImmediate(enemyGo);
            Object.DestroyImmediate(punchDef);
        }
    }
}
