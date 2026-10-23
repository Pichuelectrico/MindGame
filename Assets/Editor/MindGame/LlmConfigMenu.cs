using UnityEditor;
using UnityEngine;
using Game.AI;

namespace MindGame.Editor
{
    /// <summary>Creates the local, git-ignored LLM endpoint configuration asset.</summary>
    public static class LlmConfigMenu
    {
        private const string ConfigFolder = "Assets/Config";
        private const string ConfigPath = ConfigFolder + "/LlmConfig.asset";

        [MenuItem("MindGame/Setup/Create LLM Config")]
        public static void CreateLlmConfig()
        {
            EnsureConfigFolder();
            var config = AssetDatabase.LoadAssetAtPath<LlmConfig>(ConfigPath);
            if (config != null)
            {
                Selection.activeObject = config;
                EditorGUIUtility.PingObject(config);
                Debug.LogWarning("[LlmConfigMenu] LlmConfig.asset already exists; selected it instead of duplicating.");
                return;
            }

            config = ScriptableObject.CreateInstance<LlmConfig>();
            AssetDatabase.CreateAsset(config, ConfigPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
            EditorUtility.DisplayDialog(
                "LLM Config created",
                "Edita endpointUrl/modelName/apiKey en el Inspector; el asset va en Assets/Config/ — NO lo subas a Git con tu key.",
                "OK");
            Debug.Log("[LlmConfigMenu] Created " + ConfigPath + ".");
        }

        private static void EnsureConfigFolder()
        {
            if (!AssetDatabase.IsValidFolder(ConfigFolder))
                AssetDatabase.CreateFolder("Assets", "Config");

            var ignorePath = ConfigFolder + "/.gitignore";
            if (!System.IO.File.Exists(ignorePath))
            {
                System.IO.File.WriteAllText(ignorePath,
                    "# Local LLM configuration may contain API keys; keep all assets private.\n*.asset\n");
                AssetDatabase.Refresh();
            }
        }
    }
}
