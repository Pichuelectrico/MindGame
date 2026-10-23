using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MindGame.EditorTools
{
    // [Integration] One-shot batchmode hook: opens Sandbox, runs the idempotent
    // builder menus (scene wiring + assets), saves. Designed for CLI runs with
    // the editor CLOSED (UnityLockfile free) — harmless if run in the UI too,
    // since every builder is idempotent.
    public static class BatchIntegration
    {
        public static void RunIntegration()
        {
            const string scenePath = "Assets/Scenes/Sandbox.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Debug.Log("[BatchIntegration] scene opened: " + scene.path);

            RunMenu("MindGame/Setup/Build Sandbox Scene");
            // Wire Sandbox must run AFTER Build: it attaches the runtime wiring the
            // scene can lose to hand-edits (ActionExecutors, modeManager, LlmConfig,
            // CharacterCollisionGate on player+boss). Idempotent — safe to re-run.
            RunMenu("MindGame/Setup/Wire Sandbox");
            RunMenu("MindGame/Setup/Build Attack Telegraphs");
            RunMenu("MindGame/Setup/Build Attack Sword Slash");
            RunMenu("MindGame/Setup/Build Boss Power Up");

            bool saved = EditorSceneManager.SaveOpenScenes();
            Debug.Log("[BatchIntegration] saved=" + saved);
        }

        private static void RunMenu(string menuPath)
        {
            bool ok = EditorApplication.ExecuteMenuItem(menuPath);
            Debug.Log("[BatchIntegration] menu '" + menuPath + "' -> " + (ok ? "OK" : "NOT FOUND"));
        }
    }
}
