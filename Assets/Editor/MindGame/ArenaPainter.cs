using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MindGame.EditorTools
{
    /// <summary>
    /// Paints the arena (floor "Ground" + everything under ArenaMapHolder:
    /// walls, pillars, podium, and any future user-imported map meshes) black.
    /// The builder shares ONE material across arena pieces, so this is cheap,
    /// and re-runs are idempotent. Use "Paint Arena Default" to restore the
    /// original stone gray.
    /// </summary>
    public static class ArenaPainter
    {
        [MenuItem("MindGame/Setup/Paint Arena Black")]
        public static void PaintArenaBlack()
        {
            Paint(new Color(0f, 0f, 0f, 1f), "black");
        }

        [MenuItem("MindGame/Setup/Paint Arena Default (stone gray)")]
        public static void PaintArenaDefault()
        {
            Paint(new Color(0.35f, 0.36f, 0.38f, 1f), "stone gray");
        }

        private static void Paint(Color color, string label)
        {
            int count = 0;
            count += PaintTree(GameObject.Find("ArenaMapHolder"), color);
            count += PaintTree(GameObject.Find("Ground"), color);
            if (count == 0)
            {
                Debug.LogError("[ArenaPainter] No ArenaMapHolder/Ground found — run Build Sandbox Scene first.");
                return;
            }
            if (Application.isPlaying)
            {
                // Material edits are visible live in play mode, but they revert on
                // exiting play. Re-run this menu in edit mode to persist.
                Debug.LogWarning("[ArenaPainter] Painted " + count + " renderer(s) " + label +
                    " — PLAY MODE: visual only, re-run in edit mode + File/Save to persist.");
                return;
            }
            EditorSceneManager.MarkAllScenesDirty();
            Debug.Log("[ArenaPainter] Painted " + count + " renderer(s) " + label +
                " (floor, walls, pillars, podium, map meshes). Save the scene to persist.");
        }

        private static int PaintTree(GameObject root, Color color)
        {
            if (root == null) return 0;
            int count = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var material = renderer.sharedMaterial;
                if (material == null) continue;
                material.color = color;
                material.SetColor("_BaseColor", color); // URP Lit naming
                EditorUtility.SetDirty(material);
                count++;
            }
            return count;
        }
    }
}
