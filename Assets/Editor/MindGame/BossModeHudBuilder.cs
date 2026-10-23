using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Game.AI;
using Game.UI;

namespace MindGame.Editor
{
    public static class BossModeHudBuilder
    {
        [MenuItem("MindGame/Setup/Build Boss Mode HUD")]
        public static void Build()
        {
            var canvas = GameObject.Find("CommandCanvas");
            if (canvas == null || canvas.GetComponent<Canvas>() == null)
            {
                Debug.LogError("[BossModeHudBuilder] CommandCanvas missing. Run Build Command UI first.");
                return;
            }

            // [F53] Clean up dead camera mode hint if present
            SandboxBuilder.RemoveDeadCameraModeHint();

            var bossGo = GameObject.Find("Boss_Mutant");
            var boss = bossGo != null ? bossGo.GetComponent<BotController>() : null;
            if (boss == null)
            {
                boss = Object.FindAnyObjectByType<BotController>();
            }

            var existing = GameObject.Find("BossModeHud");
            var go = existing != null ? existing : new GameObject("BossModeHud", typeof(RectTransform));
            if (go.transform.parent != canvas.transform)
            {
                go.transform.SetParent(canvas.transform, false);
            }

            var rect = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-24f, -80f);
            rect.sizeDelta = new Vector2(480f, 32f);

            var text = go.GetComponent<Text>() ?? go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 18;
            text.alignment = TextAnchor.UpperRight;
            text.color = new Color(1f, 1f, 1f, 0.9f);
            text.raycastTarget = false;
            text.text = boss != null ? BossModeHud.FormatText(boss.Mode, boss.CurrentPersona, boss.IsFuria) : "BOSS: LLM";

            var hud = go.GetComponent<BossModeHud>() ?? go.AddComponent<BossModeHud>();
            var so = new SerializedObject(hud);
            so.FindProperty("label").objectReferenceValue = text;
            so.FindProperty("boss").objectReferenceValue = boss;
            so.FindProperty("pollInterval").floatValue = 0.25f;
            so.ApplyModifiedPropertiesWithoutUndo();

            // [UI-F4] Wire central instruction texts on CommandCanvas to auto-hide after 30s
            var cmdUI = canvas.GetComponent<CommandInputUI>();
            if (cmdUI != null)
            {
                var cmdSo = new SerializedObject(cmdUI);
                var titleGo = canvas.transform.Find("Title");
                if (titleGo != null)
                {
                    var prop = cmdSo.FindProperty("titleText");
                    if (prop != null) prop.objectReferenceValue = titleGo.GetComponent<Text>();
                }
                var feedbackGo = canvas.transform.Find("Feedback");
                if (feedbackGo != null)
                {
                    var prop = cmdSo.FindProperty("feedbackText");
                    if (prop != null) prop.objectReferenceValue = feedbackGo.GetComponent<Text>();
                }
                var durProp = cmdSo.FindProperty("instructionsDuration");
                if (durProp != null) durProp.floatValue = 30f;
                var autoProp = cmdSo.FindProperty("autoHideInstructions");
                if (autoProp != null) autoProp.boolValue = true;
                cmdSo.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[BossModeHudBuilder] DONE. Boss Mode HUD ready on CommandCanvas (idempotent).");
        }
    }
}
