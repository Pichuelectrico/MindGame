using System.Collections.Generic;
using Game.Execution;
using Game.VFX;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MindGame.Editor
{
    // Idempotent wiring for AttackTelegraph on every ActionExecutor in the open scene.
    // Separate from SandboxBuilder so concurrent agents do not collide on that file.
    public static class TelegraphBuilder
    {
        [MenuItem("MindGame/Setup/Build Attack Telegraphs")]
        public static void BuildAttackTelegraphs()
        {
            var executors = CollectExecutors();
            int wired = 0;
            int created = 0;

            foreach (var exec in executors)
            {
                if (exec == null) continue;
                var go = exec.gameObject;
                var telegraph = go.GetComponent<AttackTelegraph>();
                if (telegraph == null)
                {
                    telegraph = Undo.AddComponent<AttackTelegraph>(go);
                    created++;
                }

                var so = new SerializedObject(telegraph);
                var prop = so.FindProperty("executor");
                if (prop != null && prop.objectReferenceValue != exec)
                {
                    prop.objectReferenceValue = exec;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(telegraph);
                }

                EditorUtility.SetDirty(go);
                wired++;
            }

            if (wired > 0)
            {
                var scene = SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.MarkSceneDirty(scene);
            }

            Debug.Log("[TelegraphBuilder] wired " + wired + " telegraphs (red=melee, yellow=throw)" +
                      (created > 0 ? " (created " + created + " new)" : " (all already present)"));
        }

        // Prefer known hierarchy names + GruntSpawner host, then full scene scan
        // (EditMode / pre-Awake: static registries are empty — same pattern as HealthPickup.NearestTo).
        private static List<ActionExecutor> CollectExecutors()
        {
            var set = new HashSet<ActionExecutor>();

            TryAddFromNamed("Player_YBot", set);
            TryAddFromNamed("Boss_Mutant", set);
            TryAddFromNamed("WaveDirector", set);

            // Live grunts (play mode): walk children of any GruntSpawner host if present.
            foreach (var spawner in Object.FindObjectsByType<Game.Combat.GruntSpawner>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (spawner == null) continue;
                var execs = spawner.GetComponentsInChildren<ActionExecutor>(true);
                for (int i = 0; i < execs.Length; i++)
                    if (execs[i] != null) set.Add(execs[i]);
            }

            // Scene-wide fallback (EditMode registries empty; also catches templates).
            foreach (var exec in Object.FindObjectsByType<ActionExecutor>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (exec != null) set.Add(exec);
            }

            return new List<ActionExecutor>(set);
        }

        private static void TryAddFromNamed(string name, HashSet<ActionExecutor> set)
        {
            var go = GameObject.Find(name);
            if (go == null) return;
            var exec = go.GetComponent<ActionExecutor>();
            if (exec != null) set.Add(exec);
            var nested = go.GetComponentsInChildren<ActionExecutor>(true);
            for (int i = 0; i < nested.Length; i++)
                if (nested[i] != null) set.Add(nested[i]);
        }
    }
}
