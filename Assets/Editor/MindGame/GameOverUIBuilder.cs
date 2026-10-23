using Game.Combat;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MindGame.Editor
{
    // [F37] Idempotent editor builder for GameOverUI overlay and GameOverManager wiring.
    // Kept in its own file (pattern of TelegraphBuilder) so concurrent tasks do not collide.
    public static class GameOverUIBuilder
    {
        [MenuItem("MindGame/Setup/Build Game Over UI")]
        public static void BuildGameOverUI()
        {
            var canvas = GameObject.Find("CommandCanvas");
            if (canvas == null)
            {
                Debug.LogError("[GameOverUIBuilder] CommandCanvas missing. Run Build Command UI first.");
                return;
            }

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // 1. Overlay Fullscreen Panel
            var overlay = EnsureChild(canvas.transform, "GameOverOverlay");
            var overlayRt = overlay.GetComponent<RectTransform>() ?? overlay.AddComponent<RectTransform>();
            overlayRt.anchorMin = Vector2.zero;
            overlayRt.anchorMax = Vector2.one;
            overlayRt.offsetMin = Vector2.zero;
            overlayRt.offsetMax = Vector2.zero;
            var overlayImg = overlay.GetComponent<Image>() ?? overlay.AddComponent<Image>();
            overlayImg.color = new Color(0.04f, 0.05f, 0.07f, 0.88f);
            overlayImg.raycastTarget = true;

            // 2. Title Text ("¡VICTORIA!" / "¡DERROTA!")
            var titleGo = EnsureChild(overlay.transform, "TitleText");
            var titleRt = titleGo.GetComponent<RectTransform>() ?? titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0.5f, 0.5f);
            titleRt.anchorMax = new Vector2(0.5f, 0.5f);
            titleRt.pivot = new Vector2(0.5f, 0.5f);
            titleRt.anchoredPosition = new Vector2(0f, 95f);
            titleRt.sizeDelta = new Vector2(900f, 100f);
            var titleText = titleGo.GetComponent<Text>() ?? titleGo.AddComponent<Text>();
            titleText.font = font;
            titleText.fontSize = 62;
            titleText.fontStyle = FontStyle.Bold;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            titleText.verticalOverflow = VerticalWrapMode.Overflow;
            titleText.color = new Color(0.2f, 0.95f, 0.35f, 1f);
            titleText.text = "¡VICTORIA!";
            titleText.raycastTarget = false;

            // 3. Message Text ("¡HAS MATADO AL MUTANTE!" / "HAS CAÍDO EN COMBATE")
            var msgGo = EnsureChild(overlay.transform, "MessageText");
            var msgRt = msgGo.GetComponent<RectTransform>() ?? msgGo.AddComponent<RectTransform>();
            msgRt.anchorMin = new Vector2(0.5f, 0.5f);
            msgRt.anchorMax = new Vector2(0.5f, 0.5f);
            msgRt.pivot = new Vector2(0.5f, 0.5f);
            msgRt.anchoredPosition = new Vector2(0f, 25f);
            msgRt.sizeDelta = new Vector2(900f, 60f);
            var msgText = msgGo.GetComponent<Text>() ?? msgGo.AddComponent<Text>();
            msgText.font = font;
            msgText.fontSize = 28;
            msgText.alignment = TextAnchor.MiddleCenter;
            msgText.horizontalOverflow = HorizontalWrapMode.Overflow;
            msgText.verticalOverflow = VerticalWrapMode.Overflow;
            msgText.color = new Color(0.92f, 0.92f, 0.96f, 1f);
            msgText.text = "¡HAS MATADO AL MUTANTE!";
            msgText.raycastTarget = false;

            // 4. Hint Text
            var hintGo = EnsureChild(overlay.transform, "HintText");
            var hintRt = hintGo.GetComponent<RectTransform>() ?? hintGo.AddComponent<RectTransform>();
            hintRt.anchorMin = new Vector2(0.5f, 0.5f);
            hintRt.anchorMax = new Vector2(0.5f, 0.5f);
            hintRt.pivot = new Vector2(0.5f, 0.5f);
            hintRt.anchoredPosition = new Vector2(0f, -35f);
            hintRt.sizeDelta = new Vector2(900f, 40f);
            var hintText = hintGo.GetComponent<Text>() ?? hintGo.AddComponent<Text>();
            hintText.font = font;
            hintText.fontSize = 18;
            hintText.alignment = TextAnchor.MiddleCenter;
            hintText.horizontalOverflow = HorizontalWrapMode.Overflow;
            hintText.verticalOverflow = VerticalWrapMode.Overflow;
            hintText.color = new Color(0.72f, 0.72f, 0.78f, 1f);
            hintText.text = "Pulsa ENTER o haz clic en Reintentar";
            hintText.raycastTarget = false;

            // 5. Restart Button
            var btnGo = EnsureChild(overlay.transform, "RestartButton");
            var btnRt = btnGo.GetComponent<RectTransform>() ?? btnGo.AddComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(0.5f, 0.5f);
            btnRt.anchorMax = new Vector2(0.5f, 0.5f);
            btnRt.pivot = new Vector2(0.5f, 0.5f);
            btnRt.anchoredPosition = new Vector2(0f, -105f);
            btnRt.sizeDelta = new Vector2(240f, 52f);
            var btnImg = btnGo.GetComponent<Image>() ?? btnGo.AddComponent<Image>();
            btnImg.color = new Color(0.18f, 0.28f, 0.45f, 1f);
            btnImg.raycastTarget = true;
            var button = btnGo.GetComponent<Button>() ?? btnGo.AddComponent<Button>();
            button.targetGraphic = btnImg;

            var btnTextGo = EnsureChild(btnGo.transform, "Text");
            var btnTextRt = btnTextGo.GetComponent<RectTransform>() ?? btnTextGo.AddComponent<RectTransform>();
            btnTextRt.anchorMin = Vector2.zero;
            btnTextRt.anchorMax = Vector2.one;
            btnTextRt.offsetMin = Vector2.zero;
            btnTextRt.offsetMax = Vector2.zero;
            var btnText = btnTextGo.GetComponent<Text>() ?? btnTextGo.AddComponent<Text>();
            btnText.font = font;
            btnText.fontSize = 22;
            btnText.fontStyle = FontStyle.Bold;
            btnText.alignment = TextAnchor.MiddleCenter;
            btnText.horizontalOverflow = HorizontalWrapMode.Overflow;
            btnText.verticalOverflow = VerticalWrapMode.Overflow;
            btnText.color = Color.white;
            btnText.text = "REINTENTAR";
            btnText.raycastTarget = false;

            // 6. GameOverUI Component
            var ui = overlay.GetComponent<GameOverUI>() ?? overlay.AddComponent<GameOverUI>();
            var uiso = new SerializedObject(ui);
            uiso.FindProperty("overlayPanel").objectReferenceValue = overlay;
            uiso.FindProperty("titleText").objectReferenceValue = titleText;
            uiso.FindProperty("messageText").objectReferenceValue = msgText;
            uiso.FindProperty("hintText").objectReferenceValue = hintText;
            uiso.FindProperty("restartButton").objectReferenceValue = button;

            // 7. Wire to GameOverManager on WaveDirector
            var wave = GameObject.Find("WaveDirector");
            var player = GameObject.Find("Player_YBot");
            var boss = GameObject.Find("Boss_Mutant");

            if (wave != null)
            {
                var manager = wave.GetComponent<GameOverManager>() ?? wave.AddComponent<GameOverManager>();
                var mso = new SerializedObject(manager);
                if (boss != null)
                {
                    var bh = boss.GetComponent<HealthSystem>();
                    if (bh != null) mso.FindProperty("bossHealth").objectReferenceValue = bh;
                }
                if (player != null)
                {
                    var ph = player.GetComponent<HealthSystem>();
                    if (ph != null) mso.FindProperty("playerHealth").objectReferenceValue = ph;
                    var tc = player.GetComponent<TimeController>();
                    if (tc != null) mso.FindProperty("time").objectReferenceValue = tc;
                }
                var sp = wave.GetComponent<GruntSpawner>();
                if (sp != null) mso.FindProperty("spawner").objectReferenceValue = sp;

                mso.FindProperty("gameOverUI").objectReferenceValue = ui;
                mso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(manager);

                uiso.FindProperty("manager").objectReferenceValue = manager;
            }

            uiso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(ui);

            // Hide overlay by default
            overlay.SetActive(false);

            var scene = SceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log("[GameOverUIBuilder] DONE. GameOverUI overlay built and wired successfully (hidden by default).");
        }

        private static GameObject EnsureChild(Transform parent, string name)
        {
            var child = parent.Find(name);
            if (child != null) return child.gameObject;
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
