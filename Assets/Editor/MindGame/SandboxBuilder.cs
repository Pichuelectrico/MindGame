using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MindGame.Editor
{
    // Builds executor-driven AnimatorControllers (state name == actionId, no
    // auto-transitions: ActionExecutor drives via CrossFade) and the Sandbox scene.
    // Idempotent: safe to re-run from the MindGame/Setup menu.
    public static class SandboxBuilder
    {
        private const string ActionsFolder = "Assets/Data/Actions";
        private const string PlayerControllerPath = "Assets/Animations/PlayerController.controller";
        private const string BossControllerPath = "Assets/Animations/BossController.controller";
        private const string SandboxPath = "Assets/Scenes/Sandbox.unity";
        private const string MutantFbx = "Assets/Art/Characters/Mutant.fbx";
        private const string YBotFbx = "Assets/Art/Characters/Y Bot.fbx";

        [MenuItem("MindGame/Setup/Build Animator Controller")]
        public static void BuildAnimatorControllers()
        {
            var defs = LoadActionDefinitions();
            if (defs.Count == 0)
            {
                Debug.LogError("[SandboxBuilder] No ActionDefinition assets found in " + ActionsFolder);
                return;
            }
            int p = BuildController(PlayerControllerPath, defs, includeBossOnly: false);
            int b = BuildController(BossControllerPath, defs, includeBossOnly: true);
            AssetDatabase.SaveAssets();
            Debug.Log(string.Format("[SandboxBuilder] DONE. PlayerController states={0}, BossController states={1} (idempotent)", p, b));
        }

        [MenuItem("MindGame/Setup/Build Sandbox Scene")]
        public static void BuildSandboxScene()
        {
            var playerCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(PlayerControllerPath);
            var bossCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(BossControllerPath);
            if (playerCtrl == null || bossCtrl == null)
            {
                Debug.LogError("[SandboxBuilder] Controllers missing. Run MindGame/Setup/Build Animator Controller first.");
                return;
            }
            var mutantPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MutantFbx);
            var ybotPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(YBotFbx);
            if (mutantPrefab == null || ybotPrefab == null)
            {
                Debug.LogError("[SandboxBuilder] Character FBX missing: " + MutantFbx + " / " + YBotFbx);
                return;
            }

            Scene scene;
            try
            {
                scene = EditorSceneManager.OpenScene(SandboxPath, OpenSceneMode.Single);
                Debug.Log("[SandboxBuilder] Opened existing Sandbox scene.");
            }
            catch (System.Exception)
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                Debug.Log("[SandboxBuilder] Created new Sandbox scene.");
            }

            var ground = EnsureObject("Ground", () =>
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
                g.name = "Ground";
                g.transform.position = Vector3.zero;
                return g;
            });
            // 40x40 arena (default plane is 10x10).
            ground.transform.localScale = new Vector3(4f, 1f, 4f);
            EnsureObject("Directional Light", () =>
            {
                var light = new GameObject("Directional Light");
                var l = light.AddComponent<Light>();
                l.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                return light;
            });

            var bossGo = SetupCharacter(scene, "Boss_Mutant", mutantPrefab, bossCtrl, new Vector3(-3f, 0f, 0f));
            var playerGo = SetupCharacter(scene, "Player_YBot", ybotPrefab, playerCtrl, new Vector3(3f, 0f, 2f));
            // Fighters at 1.4 scale (only bump if still at 1: never shrink user tweaks).
            if (bossGo.transform.localScale == Vector3.one)
                bossGo.transform.localScale = Vector3.one * 1.4f;
            if (playerGo.transform.localScale == Vector3.one)
                playerGo.transform.localScale = Vector3.one * 1.4f;

            // Main Camera follows the player, always (command mode + game mode).
            EnsureActionCamera(playerGo.transform);

            EditorSceneManager.SaveScene(scene, SandboxPath);
            Debug.Log("[SandboxBuilder] DONE. Sandbox saved at " + SandboxPath + " (idempotent: no duplicates on re-run)");
        }

        // Shared by Build Sandbox Scene and Wire Sandbox: guarantees a camera with
        // PlayerFollowCamera bound to the player, creating it if missing.
        // Idempotent: never duplicates an existing follow camera.
        private static void EnsureActionCamera(Transform player)
        {
            GameObject cam = null;
            foreach (var c in Object.FindObjectsOfType<Camera>())
            {
                if (c.GetComponent<Game.Camera.PlayerFollowCamera>() != null) { cam = c.gameObject; break; }
            }
            if (cam == null)
                cam = Camera.main != null ? Camera.main.gameObject : GameObject.Find("Main Camera");
            if (cam == null)
            {
                cam = new GameObject("Main Camera");
                var c = cam.AddComponent<Camera>();
                c.tag = "MainCamera";
                Debug.Log("[SandboxBuilder] Created Main Camera.");
            }
            var follow = cam.GetComponent<Game.Camera.PlayerFollowCamera>();
            if (follow == null) follow = cam.AddComponent<Game.Camera.PlayerFollowCamera>();
            var fso = new SerializedObject(follow);
            fso.FindProperty("target").objectReferenceValue = player;
            fso.ApplyModifiedPropertiesWithoutUndo();
            WireCameraModes(follow, cam.GetComponent<Camera>()); // [F10] two-mode camera fields (append-only)
            EnsureWheelIntentRunner(player); // [F11] intents runner on the player (append-only)
            EnsureHealthPickups(); // [F11] 3 health pickups in a triangle (append-only)
            EnsureArenaEnvironment(); // [F13.1] auto-decorate on rebuild — SKIPS if the user's map is installed
        }

        [MenuItem("MindGame/Setup/Build Command UI")]
        public static void BuildCommandUI()
        {
            var canvas = EnsureObject("CommandCanvas", () =>
            {
                var go = new GameObject("CommandCanvas", typeof(RectTransform));
                var c = go.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                go.AddComponent<CanvasScaler>();
                go.AddComponent<GraphicRaycaster>();
                return go;
            });
            // A canvas born without RectTransform (older run) breaks UI children:
            // rebuild it cleanly rather than reusing it.
            var brokenCanvas = GameObject.Find("CommandCanvas");
            if (brokenCanvas != null && brokenCanvas.GetComponent<RectTransform>() == null)
            {
                Debug.LogWarning("[SandboxBuilder] Removing broken CommandCanvas without RectTransform.");
                Object.DestroyImmediate(brokenCanvas);
                var go = new GameObject("CommandCanvas", typeof(RectTransform));
                var c = go.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                go.AddComponent<CanvasScaler>();
                go.AddComponent<GraphicRaycaster>();
                canvas = go;
            }
            EnsureObject("EventSystem", () =>
            {
                var go = new GameObject("EventSystem");
                go.AddComponent<EventSystem>();
                // New Input System: StandaloneInputModule uses legacy input and
                // breaks UI interaction in this project.
                go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                return go;
            });
            // Repair path: an existing EventSystem with the legacy module (from an
            // older menu run) must be migrated, or UI input stays dead.
            var existingEs = GameObject.Find("EventSystem");
            if (existingEs != null)
            {
                var legacy = existingEs.GetComponent<StandaloneInputModule>();
                if (legacy != null)
                {
                    Debug.LogWarning("[SandboxBuilder] Replacing legacy StandaloneInputModule with InputSystemUIInputModule.");
                    Object.DestroyImmediate(legacy);
                }
                if (existingEs.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() == null)
                    existingEs.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            }

            var title = EnsureChildText(canvas.transform, "Title", "MindGame", 24, new Vector2(0, -30), new Vector2(400, 40));
            // Text input removed (F5): the wheel is the only input. Destroy leftovers
            // from previous menu runs (backward idempotency), keep Title + Feedback.
            DestroyChildIfPresent(canvas.transform, "CommandInput");
            DestroyChildIfPresent(canvas.transform, "ExecuteButton");
            var feedback = EnsureChildText(canvas.transform, "Feedback", string.Empty, 16, new Vector2(0, -130), new Vector2(560, 30));

            var shell = canvas.GetComponent<Game.UI.CommandInputUI>();
            if (shell == null) shell = canvas.AddComponent<Game.UI.CommandInputUI>();
            var sso = new SerializedObject(shell);
            var titleProp = sso.FindProperty("titleText");
            if (titleProp != null) titleProp.objectReferenceValue = title;
            var feedbackProp = sso.FindProperty("feedbackText");
            if (feedbackProp != null) feedbackProp.objectReferenceValue = feedback;
            var durationProp = sso.FindProperty("instructionsDuration");
            if (durationProp != null) durationProp.floatValue = 30f;
            var autoHideProp = sso.FindProperty("autoHideInstructions");
            if (autoHideProp != null) autoHideProp.boolValue = true;
            sso.ApplyModifiedPropertiesWithoutUndo();

            if (Application.isPlaying == false)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                // Persist immediately: an editor close without save must never
                // discard builder work (F6 lesson — the whole wire was lost).
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            }
            Debug.Log("[SandboxBuilder] DONE. Command UI (feedback only) ready on CommandCanvas. Use the action wheel [R].");
        }

        private static void DestroyChildIfPresent(Transform parent, string name)
        {
            var found = parent.Find(name);
            if (found != null)
            {
                Debug.Log("[SandboxBuilder] Removing legacy text-input object: " + name);
                Object.DestroyImmediate(found.gameObject);
            }
        }

        // A RectTransform cannot be AddComponent'ed onto a plain-Transform object,
        // so UI children are born with one. If a previous failed run left a broken
        // child (plain Transform), destroy it and recreate — never reuse it.
        private static GameObject EnsureChildRect(Transform parent, string name)
        {
            var found = parent.Find(name);
            if (found != null && found.GetComponent<RectTransform>() == null)
            {
                Debug.LogWarning("[SandboxBuilder] Removing broken UI child without RectTransform: " + name);
                Object.DestroyImmediate(found.gameObject);
                found = null;
            }
            var go = found != null ? found.gameObject : new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Text EnsureChildText(Transform parent, string name, string content, int size, Vector2 anchoredPos, Vector2 sizeDelta)
        {
            var go = EnsureChildRect(parent, name);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
            var t = go.GetComponent<Text>() ?? go.AddComponent<Text>();
            t.text = content;
            t.fontSize = size;
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return t;
        }

        private static InputField EnsureChildInput(Transform parent)
        {
            var go = EnsureChildRect(parent, "CommandInput");
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -130);
            rt.sizeDelta = new Vector2(500, 44);
            var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.12f);
            var input = go.GetComponent<InputField>() ?? go.AddComponent<InputField>();
            var placeholderGo = EnsureChildRect(go.transform, "Placeholder");
            var placeholder = placeholderGo.GetComponent<Text>() ?? placeholderGo.AddComponent<Text>();
            placeholder.text = "esquiva, ataca al de la derecha...";
            placeholder.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            placeholder.color = new Color(1f, 1f, 1f, 0.4f);
            var textGo = EnsureChildRect(go.transform, "Text");
            var text = textGo.GetComponent<Text>() ?? textGo.AddComponent<Text>();
            text.transform.SetParent(go.transform, false);
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = Color.white;
            input.textComponent = text;
            input.placeholder = placeholder;
            return input;
        }

        private static GameObject EnsureChildButton(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 sizeDelta)
        {
            var go = EnsureChildRect(parent, name);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
            var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.5f, 1f, 1f);
            var btn = go.GetComponent<Button>() ?? go.AddComponent<Button>();
            var labelGo = EnsureChildRect(go.transform, "Label");
            var labelText = labelGo.GetComponent<Text>() ?? labelGo.AddComponent<Text>();
            labelText.transform.SetParent(go.transform, false);
            var lrt = labelText.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            labelText.text = label;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelText.color = Color.white;
            return go;
        }

        [MenuItem("MindGame/Setup/Wire Sandbox")]
        public static void WireSandbox()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>(
                "Assets/Data/Actions/ActionManifest.asset");
            if (manifest == null)
            {
                Debug.LogError("[SandboxBuilder] ActionManifest.asset missing. Cannot wire.");
                return;
            }
            var player = GameObject.Find("Player_YBot");
            var boss = GameObject.Find("Boss_Mutant");
            var canvas = GameObject.Find("CommandCanvas");
            if (player == null || boss == null || canvas == null)
            {
                Debug.LogError("[SandboxBuilder] Sandbox objects missing. Run Build Sandbox Scene + Build Command UI first.");
                return;
            }
            var ui = canvas.GetComponent<Game.UI.CommandInputUI>();
            if (ui == null)
            {
                Debug.LogError("[SandboxBuilder] CommandInputUI missing. Run Build Command UI first.");
                return;
            }

            var playerExec = EnsureComponent<Game.Execution.ActionExecutor>(player);
            SetExecutorFields(playerExec, manifest, role: 0, stamina: 100f, hasWeapon: true);
            var bossExec = EnsureComponent<Game.Execution.ActionExecutor>(boss);
            SetExecutorFields(bossExec, manifest, role: 1, stamina: 200f, hasWeapon: false);

            var time = EnsureComponent<Game.Core.TimeController>(player);
            var driver = EnsureComponent<Game.Debugging.SandboxTestDriver>(player);
            var dso = new SerializedObject(driver);
            dso.FindProperty("manifest").objectReferenceValue = manifest;
            dso.FindProperty("ui").objectReferenceValue = ui;
            dso.FindProperty("time").objectReferenceValue = time;
            dso.FindProperty("player").objectReferenceValue = playerExec;
            dso.FindProperty("boss").objectReferenceValue = bossExec;
            dso.ApplyModifiedPropertiesWithoutUndo();

            WireCombatant(player, playerExec, time);
            WireCombatant(boss, bossExec, time);
            EnsureComponent<Game.Combat.CharacterCollisionGate>(player);
            EnsureComponent<Game.Combat.CharacterCollisionGate>(boss);

            var bossHealth = boss.GetComponent<Game.Combat.HealthSystem>();
            var bot = EnsureComponent<Game.AI.BotController>(boss);
            var bso = new SerializedObject(bot);
            bso.FindProperty("manifest").objectReferenceValue = manifest;
            bso.FindProperty("executor").objectReferenceValue = bossExec;
            bso.FindProperty("health").objectReferenceValue = bossHealth;
            bso.FindProperty("playerExecutor").objectReferenceValue = playerExec;
            bso.FindProperty("target").objectReferenceValue = player.transform;
            bso.FindProperty("targetExecutor").objectReferenceValue = playerExec;
            bso.FindProperty("time").objectReferenceValue = time;
            // Local config is git-ignored and may contain the user's API key.
            // Assigning it with an empty endpoint intentionally keeps the boss
            // on the heuristic path until the endpoint is supplied.
            var localLlmConfig = AssetDatabase.LoadAssetAtPath<Game.AI.LlmConfig>("Assets/Config/LlmConfig.asset");
            if (localLlmConfig != null)
                bso.FindProperty("llmConfig").objectReferenceValue = localLlmConfig;
            else if (bso.FindProperty("llmConfig").objectReferenceValue == null)
                Debug.Log("[SandboxBuilder] No local LlmConfig; boss stays heuristic.");
            bso.ApplyModifiedPropertiesWithoutUndo();

            // Structural: the follow camera must not depend on Build Sandbox Scene
            // having been re-run. Wire Sandbox alone guarantees it.
            EnsureActionCamera(player.transform);

            WireGruntSpawner(player, playerExec, manifest, time, boss, bossExec);

            // K = Roll in Game Mode (user feedback): migrate any stale serialized
            // "Dodge" value on the scene's PlayerManualController.
            // Q = BlockCenter (F33).
            var manual = player.GetComponent<Game.GameMode.PlayerManualController>();
            if (manual != null)
            {
                var mso = new SerializedObject(manual);
                var prop = mso.FindProperty("dodgeActionId");
                if (prop != null && prop.stringValue != "Roll")
                {
                    prop.stringValue = "Roll";
                    mso.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("[SandboxBuilder] PlayerManualController K action migrated Dodge -> Roll.");
                }
                var bprop = mso.FindProperty("blockActionId");
                if (bprop != null && bprop.stringValue != "BlockCenter")
                {
                    bprop.stringValue = "BlockCenter";
                    mso.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("[SandboxBuilder] PlayerManualController Q action wired to BlockCenter.");
                }
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Sandbox wired: UI -> parser -> executor -> TimeController. Press Play, type a command, Enter. Space = demo chain.");
        }

        private static void WireCombatant(GameObject go, Game.Execution.ActionExecutor exec, Game.Core.TimeController time)
        {
            var hp = EnsureComponent<Game.Combat.HealthSystem>(go);
            var director = EnsureComponent<Game.Combat.CombatDirector>(go);
            var so = new SerializedObject(director);
            so.FindProperty("executor").objectReferenceValue = exec;
            so.FindProperty("health").objectReferenceValue = hp;
            so.FindProperty("time").objectReferenceValue = time;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireGruntSpawner(GameObject player, Game.Execution.ActionExecutor playerExec,
            Game.Actions.ActionManifest manifest, Game.Core.TimeController time,
            GameObject boss, Game.Execution.ActionExecutor bossExec)
        {
            var host = EnsureObject("WaveDirector", () => new GameObject("WaveDirector"));
            var spawner = EnsureComponent<Game.Combat.GruntSpawner>(host);
            var bot = boss.GetComponent<Game.AI.BotController>();
            var director = boss.GetComponent<Game.Combat.CombatDirector>();
            var so = new SerializedObject(spawner);
            so.FindProperty("gruntTemplate").objectReferenceValue = player;
            so.FindProperty("player").objectReferenceValue = player.transform;
            so.FindProperty("playerExecutor").objectReferenceValue = playerExec;
            so.FindProperty("manifest").objectReferenceValue = manifest;
            so.FindProperty("time").objectReferenceValue = time;
            so.FindProperty("boss").objectReferenceValue = boss;
            so.FindProperty("bossExecutor").objectReferenceValue = bossExec;
            if (bot != null) so.FindProperty("bossController").objectReferenceValue = bot;
            if (director != null) so.FindProperty("bossDirector").objectReferenceValue = director;
            // [F18] Playtest pacing: 5 NPCs before the boss enters,
            // killsToWakeBoss = 5, waves of 15s with double spawn chance. Post-boss waves stay sustained.
            so.FindProperty("killsToWakeBoss").intValue = 5;
            so.FindProperty("maxConcurrent").intValue = 3; // [F26] at most 3 bots alive at once
            so.FindProperty("preBossSpawnLimit").intValue = 8;
            so.FindProperty("firstSpawnDelay").floatValue = 5f;
            so.FindProperty("spawnInterval").floatValue = 15f;
            so.FindProperty("bossSpawnInterval").floatValue = 45f; // [F30] boss fight: 1 NPC / 45s
            var doubleProp = so.FindProperty("doubleSpawnChance");
            if (doubleProp != null) doubleProp.floatValue = 0.4f;
            so.ApplyModifiedPropertiesWithoutUndo();

            // [F7] Boss fight tuning: real-fight HP + damage feel. Idempotent —
            // only raises HP (never refills or lowers an already-tuned boss) and
            // only writes the damage fields. Numbers live here in the wire.
            var bossHealth = boss.GetComponent<Game.Combat.HealthSystem>();
            if (bossHealth != null && bossHealth.MaxHealth < 30)
            {
                bossHealth.SetMaxHealth(35);
                Debug.Log("[SandboxBuilder] Boss HP -> 35 (real-fight tuning).");
            }
            if (director != null)
            {
                var dso = new SerializedObject(director);
                dso.FindProperty("lightHitDamage").intValue = 2;
                dso.FindProperty("heavyHitDamage").intValue = 4;
                dso.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("[SandboxBuilder] Boss damage tuned: light 2 / heavy 4.");
            }

            // [F43] Threatening boss: 0.8s decision cadence (scene had 1.2
            // serialized), Run/Walk x1.4 (player/grunts stay 1) and a reaction
            // breather of at most 1.2s (was 0-5s random -> long stalls).
            // Idempotent — reapplied by Wire Sandbox every build.
            // [F45] enableFuria=true ONLY here (boss bot); grunts keep the
            // default false. Furia latches at <30% HP: cadence /1.5 (~0.53s),
            // speed composes from this 1.4 base -> 1.82.
            if (bot != null)
            {
                var cso = new SerializedObject(bot);
                cso.FindProperty("decisionInterval").floatValue = 0.8f;
                cso.FindProperty("enableFuria").boolValue = true;
                cso.ApplyModifiedPropertiesWithoutUndo();
            }

            // [F57] Wire CRÍTICO: el GO del boss debe TENER el componente
            // ANTES de play — el executor dispara ActionStarted y sin listener
            // no hay beam ni daño (en el playtest el laser existía solo en el
            // menú de preview). Idempotente: no-op si ya está wireado.
            if (Game.Combat.BossLaserBeam.EnsureOn(boss))
                Debug.Log("[SandboxBuilder] +BossLaserBeam (faltaba: sin esto el laser no corre en play)");
            if (bossExec == null && boss != null)
                bossExec = boss.GetComponent<Game.Execution.ActionExecutor>();
            if (bossExec != null)
            {
                var eso = new SerializedObject(bossExec);
                eso.FindProperty("locomotionSpeedMultiplier").floatValue = 1.4f;
                eso.FindProperty("reactionRecoverySeconds").vector2Value = new Vector2(0f, 1.2f);
                eso.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("[SandboxBuilder] Boss threat tuning: decisionInterval 0.8s, Run/Walk x1.4, recovery <=1.2s.");
            }

            // The gate: the boss starts DORMANT — controller and director disabled,
            // looping MilitarySignaling_PreCombatIdle at the arena edge until 5
            // grunts are defeated (GruntSpawner.WakeBoss enables both again and
            // swaps the idle for the BossPowerUp entry animation).
            if (bot != null && bot.enabled)
            {
                bot.enabled = false;
                Debug.Log("[SandboxBuilder] Boss BotController disabled (dormant until 5 kills).");
            }
            if (director != null && director.enabled)
            {
                director.enabled = false;
                Debug.Log("[SandboxBuilder] Boss CombatDirector disabled (no combat before the gate).");
            }

            var sword = EnsureComponent<Game.Combat.SwordManager>(host);
            if (bot != null)
            {
                var bso = new SerializedObject(bot);
                bso.FindProperty("sword").objectReferenceValue = sword;
                bso.ApplyModifiedPropertiesWithoutUndo();
            }

            // [F18] Power-up entry: ensure the BossPowerUp action asset + the
            // matching BossController state exist so WakeBoss can play the entry
            // animation (idempotent; also available as a standalone menu).
            EnsureBossPowerUpAction();
            EnsureAttackSwordSlash();
            BuildAnimatorControllers();
        }

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c == null)
            {
                c = go.AddComponent<T>();
                Debug.Log("[SandboxBuilder] +component " + go.name + "::" + typeof(T).Name);
            }
            return c;
        }

        private static void SetExecutorFields(Game.Execution.ActionExecutor exec,
            Game.Actions.ActionManifest manifest, int role, float stamina, bool hasWeapon)
        {
            // Only the manifest reference is serialized; entity state (role/stamina/
            // weapon) is pushed at runtime by SandboxTestDriver.Start, since the
            // executor exposes those as non-serialized properties.
            var so = new SerializedObject(exec);
            so.FindProperty("manifest").objectReferenceValue = manifest;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---- internals ----

        private static List<Game.Actions.ActionDefinition> LoadActionDefinitions()
        {
            var list = new List<Game.Actions.ActionDefinition>();
            foreach (var guid in AssetDatabase.FindAssets("t:ActionDefinition", new[] { ActionsFolder }))
            {
                var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (def != null) list.Add(def);
            }
            return list;
        }

        // roleRestriction lives in Game.Actions.ActionRole (Any=0, BossOnly=1).
        // Compared by int so this editor script never breaks if the enum moves.
        private static int BuildController(string path, List<Game.Actions.ActionDefinition> defs, bool includeBossOnly)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(path);
                Debug.Log("[SandboxBuilder] Created controller: " + path);
            }
            var root = controller.layers[0].stateMachine;
            var existing = new Dictionary<string, AnimatorState>();
            foreach (var s in root.states)
                existing[s.state.name] = s.state;

            if (!existing.ContainsKey("Idle"))
            {
                var idle = root.AddState("Idle");
                idle.motion = FindClip("Idle");
                root.defaultState = idle;
            }

            int count = 0;
            foreach (var def in defs)
            {
                bool bossOnly = (int)def.RoleRestriction == 1;
                if (bossOnly && !includeBossOnly) continue;
                AnimatorState state;
                if (!existing.TryGetValue(def.ActionId, out state))
                {
                    state = root.AddState(def.ActionId);
                    existing[def.ActionId] = state;
                    Debug.Log("[SandboxBuilder] +state " + path + "::" + def.ActionId);
                }
                var clip = FindClip(def.ClipName);
                if (clip != null)
                {
                    state.motion = clip;
                    // Locomotion clips must loop: otherwise the fighter freezes on the
                    // last frame while programmatic displacement keeps sliding it.
                    if (def.ActionId == "Run" || def.ActionId == "Walk")
                        SetClipLoop(clip, true);
                }
                else Debug.LogWarning("[SandboxBuilder] Clip not found for action " + def.ActionId
                    + " (clipName=" + def.ClipName + "). Run Extract Mixamo Clips first.");
                count++;
            }
            if (controller.parameters.Length == 0)
                controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            EditorUtility.SetDirty(controller);
            return count;
        }

        private static void SetClipLoop(AnimationClip clip, bool loop)
        {
            var settings = UnityEditor.AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.loopTime == loop) return;
            settings.loopTime = loop;
            UnityEditor.AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            Debug.Log("[SandboxBuilder] Loop=" + loop + " on clip " + clip.name);
        }

        private static AnimationClip FindClip(string clipName)
        {
            foreach (var guid in AssetDatabase.FindAssets(clipName + " t:AnimationClip", new[] { "Assets/Animations" }))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guid));
                if (clip != null && clip.name == clipName) return clip;
            }
            return null;
        }

        private static GameObject EnsureObject(string name, System.Func<GameObject> create)
        {
            var go = GameObject.Find(name);
            if (go != null) return go;
            go = create();
            Debug.Log("[SandboxBuilder] Created scene object: " + name);
            return go;
        }

        private static GameObject SetupCharacter(Scene scene, string name, GameObject prefab,
            RuntimeAnimatorController controller, Vector3 pos)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                go.name = name;
                Debug.Log("[SandboxBuilder] Instanced character: " + name);
            }
            go.transform.position = pos;
            go.transform.rotation = Quaternion.identity;
            var animator = go.GetComponent<Animator>();
            if (animator == null) animator = go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(prefab)) as ModelImporter;
            if (importer != null && importer.animationType == ModelImporterAnimationType.Human)
            {
                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(AssetDatabase.GetAssetPath(prefab));
                if (avatar != null) animator.avatar = avatar;
                else Debug.LogWarning("[SandboxBuilder] No Avatar found on " + name + "; check Rig in importer.");
            }
            else Debug.LogWarning("[SandboxBuilder] " + name + " importer is not Humanoid.");
            EditorUtility.SetDirty(go);
            return go;
        }

        [MenuItem("MindGame/Setup/Build Combat HUD")]
        public static void BuildCombatHUD()
        {
            var canvas = GameObject.Find("CommandCanvas");
            if (canvas == null || canvas.GetComponent<Canvas>() == null)
            {
                Debug.LogError("[SandboxBuilder] CommandCanvas missing. Run Build Command UI first.");
                return;
            }
            BuildHealthLabel(canvas.transform, "PlayerHealth", "Jugador: ", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -24f), "Player_YBot");
            BuildHealthLabel(canvas.transform, "BossHealth", "Mutante: ", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -24f), "Boss_Mutant");
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Combat HUD ready (idempotent).");
        }

        private static void BuildHealthLabel(Transform parent, string name, string prefix, Vector2 anchor, Vector2 pivot, Vector2 position, string actorName)
        {
            var actor = GameObject.Find(actorName);
            if (actor == null)
            {
                Debug.LogError("[SandboxBuilder] Cannot wire HUD: missing " + actorName + ".");
                return;
            }
            var health = actor.GetComponent<Game.Combat.HealthSystem>();
            if (health == null)
            {
                Debug.LogError("[SandboxBuilder] Cannot wire HUD: " + actorName + " has no HealthSystem.");
                return;
            }
            var go = EnsureChildRect(parent, name);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = position;
            rt.sizeDelta = new Vector2(260f, 52f);
            var text = go.GetComponent<Text>() ?? go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 30;
            text.alignment = anchor.x < 0.5f ? TextAnchor.UpperLeft : TextAnchor.UpperRight;
            text.text = prefix + health.CurrentHealth;
            var hud = go.GetComponent<Game.UI.HealthHUD>() ?? go.AddComponent<Game.UI.HealthHUD>();
            var so = new SerializedObject(hud);
            so.FindProperty("label").objectReferenceValue = text;
            so.FindProperty("health").objectReferenceValue = health;
            so.FindProperty("labelPrefix").stringValue = prefix;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("MindGame/Setup/Build Game Mode Toggle")]
        public static void BuildGameModeToggle()
        {
            var canvas = GameObject.Find("CommandCanvas");
            if (canvas == null || canvas.GetComponent<Canvas>() == null)
            {
                Debug.LogError("[SandboxBuilder] CommandCanvas missing. Run Build Command UI first.");
                return;
            }
            var player = GameObject.Find("Player_YBot");
            if (player == null)
            {
                Debug.LogError("[SandboxBuilder] Player_YBot missing. Run Build Sandbox Scene first.");
                return;
            }
            var ui = canvas.GetComponent<Game.UI.CommandInputUI>();
            if (ui == null)
            {
                Debug.LogError("[SandboxBuilder] CommandInputUI missing. Run Build Command UI first.");
                return;
            }

            var btnGo = EnsureChildRect(canvas.transform, "GameModeToggleButton");
            var rt = btnGo.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(24f, 24f);
            rt.sizeDelta = new Vector2(190f, 44f);
            var img = btnGo.GetComponent<Image>() ?? btnGo.AddComponent<Image>();
            img.color = new Color(0.15f, 0.45f, 0.85f, 1f);
            var btn = btnGo.GetComponent<Button>() ?? btnGo.AddComponent<Button>();

            var labelGo = EnsureChildRect(btnGo.transform, "Label");
            var labelText = labelGo.GetComponent<Text>() ?? labelGo.AddComponent<Text>();
            labelText.transform.SetParent(btnGo.transform, false);
            var lrt = labelText.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            labelText.text = "Modo: Comandos [Tab]";
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelText.fontSize = 14;
            labelText.color = Color.white;

            var manager = EnsureComponent<Game.GameMode.GameModeManager>(player);
            var mso = new SerializedObject(manager);
            mso.FindProperty("commandUI").objectReferenceValue = ui;
            mso.FindProperty("toggleButton").objectReferenceValue = btn;
            mso.FindProperty("toggleButtonText").objectReferenceValue = labelText;
            mso.ApplyModifiedPropertiesWithoutUndo();

            var manualCtrl = EnsureComponent<Game.GameMode.PlayerManualController>(player);
            var exec = player.GetComponent<Game.Execution.ActionExecutor>();
            var time = player.GetComponent<Game.Core.TimeController>();
            var cam = Camera.main;
            var pcso = new SerializedObject(manualCtrl);
            pcso.FindProperty("modeManager").objectReferenceValue = manager;
            pcso.FindProperty("time").objectReferenceValue = time;
            pcso.FindProperty("executor").objectReferenceValue = exec;
            if (cam != null)
            {
                pcso.FindProperty("targetCamera").objectReferenceValue = cam;
                var follow = cam.GetComponent<Game.Camera.PlayerFollowCamera>();
                if (follow != null)
                    pcso.FindProperty("followCamera").objectReferenceValue = follow;
            }
            var dodgeProp = pcso.FindProperty("dodgeActionId");
            if (dodgeProp != null) dodgeProp.stringValue = "Roll";
            var blockProp = pcso.FindProperty("blockActionId");
            if (blockProp != null) blockProp.stringValue = "BlockCenter";
            pcso.ApplyModifiedPropertiesWithoutUndo();

            // [F25 fix] Wire modeManager to CombatDirector and PlayerFollowCamera
            var cd = player.GetComponent<Game.Combat.CombatDirector>();
            if (cd != null)
            {
                var cdso = new SerializedObject(cd);
                var mp = cdso.FindProperty("modeManager");
                if (mp != null) { mp.objectReferenceValue = manager; cdso.ApplyModifiedPropertiesWithoutUndo(); }
            }
            if (cam != null)
            {
                var follow = cam.GetComponent<Game.Camera.PlayerFollowCamera>();
                if (follow != null)
                {
                    var fso = new SerializedObject(follow);
                    var mp = fso.FindProperty("modeManager");
                    if (mp != null) { mp.objectReferenceValue = manager; fso.ApplyModifiedPropertiesWithoutUndo(); }
                }
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Game Mode Toggle ready on CommandCanvas and wired to Player_YBot (idempotent).");
        }

        // F5 Action Wheel (append-only: do not reorder this file, parallel agents append below).
        [MenuItem("MindGame/Setup/Build Action Wheel")]
        public static void BuildActionWheel()
        {
            var canvas = GameObject.Find("CommandCanvas");
            var player = GameObject.Find("Player_YBot");
            if (canvas == null || player == null)
            {
                Debug.LogError("[SandboxBuilder] CommandCanvas/Player_YBot missing. Run Build Sandbox Scene + Build Command UI first.");
                return;
            }
            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>(
                "Assets/Data/Actions/ActionManifest.asset");
            var exec = player.GetComponent<Game.Execution.ActionExecutor>();
            var time = player.GetComponent<Game.Core.TimeController>();
            var ui = canvas.GetComponent<Game.UI.CommandInputUI>();
            if (manifest == null || exec == null || time == null || ui == null)
            {
                Debug.LogError("[SandboxBuilder] Manifest/executor/time/UI missing. Run Wire Sandbox first.");
                return;
            }

            var root = EnsureChildRect(canvas.transform, "ActionWheelRoot");
            var rrt = root.GetComponent<RectTransform>();
            rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one;
            rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;
            root.SetActive(false);

            var openGo = EnsureChildRect(canvas.transform, "WheelOpenButton");
            var ort = openGo.GetComponent<RectTransform>();
            ort.anchorMin = ort.anchorMax = ort.pivot = new Vector2(1f, 0f);
            ort.anchoredPosition = new Vector2(-10f, -10f);
            ort.sizeDelta = new Vector2(110f, 36f);
            var oimg = openGo.GetComponent<Image>() ?? openGo.AddComponent<Image>();
            oimg.color = new Color(0.2f, 0.45f, 0.9f, 1f);
            var openBtn = openGo.GetComponent<Button>() ?? openGo.AddComponent<Button>();
            var otgo = EnsureChildRect(openGo.transform, "Label");
            var otxt = otgo.GetComponent<Text>() ?? otgo.AddComponent<Text>();
            otxt.text = "Rueda [R]";
            otxt.fontSize = 14;
            otxt.alignment = TextAnchor.MiddleCenter;
            otxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            otxt.color = Color.white;

            var wheel = canvas.GetComponent<Game.UI.ActionWheel>();
            if (wheel == null) wheel = canvas.AddComponent<Game.UI.ActionWheel>();
            var modeManager = Object.FindObjectOfType<Game.GameMode.GameModeManager>();
            var wso = new SerializedObject(wheel);
            wso.FindProperty("manifest").objectReferenceValue = manifest;
            wso.FindProperty("executor").objectReferenceValue = exec;
            wso.FindProperty("time").objectReferenceValue = time;
            wso.FindProperty("ui").objectReferenceValue = ui;
            if (modeManager != null)
                wso.FindProperty("modeManager").objectReferenceValue = modeManager;
            wso.FindProperty("wheelRoot").objectReferenceValue = root;
            wso.FindProperty("openButton").objectReferenceValue = openBtn;
            wso.ApplyModifiedPropertiesWithoutUndo();
            wheel.Bind(root, openBtn);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Action Wheel ready: R or Rueda [R] opens it in Commands mode (idempotent).");
        }

        // Diagnostics (append-only): reports baked hips translation per clip so
        // locomotion/continuity decisions stay data-driven, never guessed.
        [MenuItem("MindGame/Diagnostics/Report Root Motion")]
        public static void ReportRootMotion()
        {
            string[] names =
            {
                "Run", "CrouchedWalk", "QuickRollToRun", "Dodge", "Jump",
                "Punch", "SwordShield_Attack", "Mutant_JumpAttack", "FrontTwistFlip", "Idle"
            };
            var sb = new System.Text.StringBuilder("[RootMotion] hips XZ drift per clip (detrended, expect ~0):");
            foreach (var n in names)
            {
                AnimationClip clip = null;
                foreach (var guid in AssetDatabase.FindAssets(n + " t:AnimationClip", new[] { "Assets/Animations" }))
                {
                    var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guid));
                    if (c != null && c.name == n) { clip = c; break; }
                }
                if (clip == null) { sb.Append("\n  ").Append(n).Append(": MISSING"); continue; }
                Vector3 first = Vector3.zero, last = Vector3.zero;
                bool found = false;
                foreach (var b in UnityEditor.AnimationUtility.GetCurveBindings(clip))
                {
                    if (!b.path.EndsWith("Hips") || !b.propertyName.StartsWith("m_LocalPosition")) continue;
                    var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null || curve.length == 0) continue;
                    if (b.propertyName.EndsWith(".x")) { first.x = curve[0].value; last.x = curve[curve.length - 1].value; found = true; }
                    if (b.propertyName.EndsWith(".z")) { first.z = curve[0].value; last.z = curve[curve.length - 1].value; found = true; }
                }
                sb.Append("\n  ").Append(n).Append(found
                    ? string.Format(": d=({0:0.00}, {1:0.00})", last.x - first.x, last.z - first.z)
                    : ": no hips curves");
            }
            sb.Append("\n[RootMotion] policy: clips play in place; transform carries motion ")
              .Append("(Run/Walk speeds, travelDistance on one-shots); applyRootMotion forced off.");
            Debug.Log(sb.ToString());
        }

        // Kill counter HUD (append-only, lead): "NPCs: n/5" bottom-center label
        // wired to the WaveDirector's GruntSpawner; flashes the mutant-entry
        // notice when the boss gate opens. Idempotent like the other builders.
        [MenuItem("MindGame/Setup/Build Kill Counter")]
        public static void BuildKillCounter()
        {
            var canvas = GameObject.Find("CommandCanvas");
            if (canvas == null || canvas.GetComponent<Canvas>() == null)
            {
                Debug.LogError("[SandboxBuilder] CommandCanvas missing. Run Build Command UI first.");
                return;
            }
            var waveDirector = GameObject.Find("WaveDirector");
            var spawner = waveDirector != null ? waveDirector.GetComponent<Game.Combat.GruntSpawner>() : null;
            if (spawner == null)
            {
                Debug.LogError("[SandboxBuilder] WaveDirector/GruntSpawner missing. Run Wire Sandbox first.");
                return;
            }

            var existing = GameObject.Find("KillCounterHUD");
            var textGo = existing != null ? existing : new GameObject("KillCounterHUD", typeof(RectTransform));
            if (existing == null)
            {
                textGo.transform.SetParent(canvas.transform, false);
                var text = textGo.AddComponent<UnityEngine.UI.Text>();
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = 18;
                text.alignment = TextAnchor.LowerCenter;
                text.color = Color.white;
                text.raycastTarget = false; // never eat wheel/card clicks
                var rect = textGo.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0f);
                rect.anchorMax = new Vector2(0.5f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = new Vector2(0f, 16f);
                rect.sizeDelta = new Vector2(340f, 28f);
            }

            var hud = textGo.GetComponent<Game.UI.KillCounterHUD>();
            if (hud == null) hud = textGo.AddComponent<Game.UI.KillCounterHUD>();
            var so = new SerializedObject(hud);
            so.FindProperty("label").objectReferenceValue = textGo.GetComponent<UnityEngine.UI.Text>();
            so.FindProperty("spawner").objectReferenceValue = spawner;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Kill Counter ready on CommandCanvas, wired to WaveDirector (idempotent).");
        }

        [MenuItem("MindGame/Setup/Fix Character Rigs (Generic)")]
        public static void FixHumanoidRigs()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[SandboxBuilder] Editor is in PLAY mode. Stop play and re-run Fix Humanoid Rigs (scene changes made during play are discarded on stop).");
                return;
            }

            // REVERT + canonic state (playtest 2026-10-14). History: Y Bot was
            // briefly converted to Humanoid to fix "[Sword] Equip failed: Avatar
            // is null" — that REGRESSED the whole player: this project's 34
            // Mixamo clips are GENERIC-PATH transform curves (F5 detrend made
            // them so on purpose), and a Humanoid avatar makes the Animator
            // process the skeleton in muscle space → clips stop animating and
            // the hips re-anchor sinks the model under the floor. Mutant can
            // never be Humanoid anyway (Rig Error: no LeftHand bone).
            // CANON: BOTH rigs Generic, scene avatars null. Sword hand-attach
            // does NOT need an avatar: SwordManager.Equip finds the hand bone
            // transform by deep name search, with the body-root fallback behind.
            var plan = new (string sceneName, string fbxPath, ModelImporterAnimationType rig)[]
            {
                ("Player_YBot", "Assets/Art/Characters/Y Bot.fbx", ModelImporterAnimationType.Generic),
                ("Boss_Mutant", "Assets/Art/Characters/Mutant.fbx", ModelImporterAnimationType.Generic),
            };

            bool anyChange = false;
            foreach (var (sceneName, fbxPath, rig) in plan)
            {
                var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (importer == null)
                {
                    Debug.LogError("[SandboxBuilder] No ModelImporter for " + fbxPath);
                    continue;
                }
                if (importer.animationType != rig)
                {
                    importer.animationType = rig;
                    importer.SaveAndReimport();
                    Debug.Log("[SandboxBuilder] Set " + fbxPath + " rig to " + rig + " and reimported.");
                    anyChange = true;
                }

                var actor = GameObject.Find(sceneName);
                if (actor == null)
                {
                    Debug.LogWarning("[SandboxBuilder] Scene object not found: " + sceneName);
                    continue;
                }
                var animator = actor.GetComponent<Animator>();
                if (animator == null) animator = actor.AddComponent<Animator>();

                if (animator.avatar != null)
                {
                    animator.avatar = null;
                    EditorUtility.SetDirty(animator);
                    Debug.Log("[SandboxBuilder] Cleared avatar on " + sceneName + " (Generic rig + generic-path clips).");
                    anyChange = true;
                }
            }

            if (anyChange)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            }
            Debug.Log("[SandboxBuilder] DONE. Fix Humanoid Rigs " + (anyChange ? "applied and scene saved." : "nothing to do (idempotent)."));
        }

        // [Auto-NPC] (playtest 2026-10-14): in auto (Commands) mode the player
        // avatar acts as a basic NPC. Idempotent wiring of PlayerAutoAI next to
        // the player's existing ActionExecutor; shares the same manifest asset.
        // The AI never touches the R-wheel's running plans (idle filler only).
        [MenuItem("MindGame/Setup/Build Player Auto AI")]
        public static void WirePlayerAutoAI()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>(
                "Assets/Data/Actions/ActionManifest.asset");
            if (manifest == null)
            {
                Debug.LogError("[SandboxBuilder] ActionManifest.asset missing. Cannot wire PlayerAutoAI.");
                return;
            }
            var player = GameObject.Find("Player_YBot");
            if (player == null)
            {
                Debug.LogError("[SandboxBuilder] Player_YBot missing. Run Build Sandbox Scene first.");
                return;
            }

            var ai = EnsureComponent<Game.Combat.PlayerAutoAI>(player);
            var so = new SerializedObject(ai);
            so.FindProperty("manifest").objectReferenceValue = manifest;
            so.FindProperty("decisionInterval").floatValue = 0.5f;
            so.FindProperty("resumeGraceTicks").intValue = 2;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. PlayerAutoAI wired on Player_YBot (Commands-mode NPC brain; R-wheel keeps priority).");
        }

        // F7 game-over closure (append-only).
        [MenuItem("MindGame/Setup/Build Game Over HUD")]
        public static void BuildGameOverHUD()
        {
            var canvas = GameObject.Find("CommandCanvas");
            var player = GameObject.Find("Player_YBot");
            var boss = GameObject.Find("Boss_Mutant");
            var wave = GameObject.Find("WaveDirector");
            if (canvas == null || player == null || boss == null || wave == null)
            {
                Debug.LogError("[SandboxBuilder] CommandCanvas/Player_YBot/Boss_Mutant/WaveDirector missing. Run Scene + UI + Wire first.");
                return;
            }
            var victory = EnsureChildRect(canvas.transform, "VictoryText");
            SetupCenterLabel(victory, 64);
            var defeat = EnsureChildRect(canvas.transform, "DefeatText");
            SetupCenterLabel(defeat, 64);
            victory.SetActive(false);
            defeat.SetActive(false);

            var hud = canvas.GetComponent<Game.UI.GameOverHUD>();
            if (hud == null) hud = canvas.AddComponent<Game.UI.GameOverHUD>();
            var hso = new SerializedObject(hud);
            hso.FindProperty("victoryText").objectReferenceValue = victory.GetComponent<Text>();
            hso.FindProperty("defeatText").objectReferenceValue = defeat.GetComponent<Text>();
            hso.ApplyModifiedPropertiesWithoutUndo();

            var manager = EnsureComponent<Game.Core.GameOverManager>(wave);
            var mso = new SerializedObject(manager);
            mso.FindProperty("bossHealth").objectReferenceValue = boss.GetComponent<Game.Combat.HealthSystem>();
            mso.FindProperty("playerHealth").objectReferenceValue = player.GetComponent<Game.Combat.HealthSystem>();
            mso.FindProperty("spawner").objectReferenceValue = wave.GetComponent<Game.Combat.GruntSpawner>();
            mso.FindProperty("hud").objectReferenceValue = hud;
            mso.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Game Over HUD ready (hidden until Victory/Defeat).");
        }

        private static void SetupCenterLabel(GameObject go, int fontSize)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var t = go.GetComponent<Text>() ?? go.AddComponent<Text>();
            t.fontSize = fontSize;
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.raycastTarget = false;
        }

        [MenuItem("MindGame/Debug/Wake Boss Now")]
        public static void WakeBossNow()
        {
            var wave = GameObject.Find("WaveDirector");
            var spawner = wave != null ? wave.GetComponent<Game.Combat.GruntSpawner>() : null;
            if (spawner == null)
            {
                Debug.LogError("[SandboxBuilder] No GruntSpawner on WaveDirector. Run Wire Sandbox first.");
                return;
            }
            spawner.WakeBoss();
            Debug.Log("[Debug] Boss despertado manualmente (gate forzado).");
        }

        // ===== F10 (Aion1, append-only): two-mode camera wire + HUD hint =====

        // Sets the two-mode camera fields (OpenCode's PlayerFollowCamera
        // rewrite): defaultPitch/chaseDistance explicit + overheadCenter/
        // overheadHeight computed from the REAL arena bounds so mode "2"
        // frames the whole map. Per-field FindProperty guards: if the rewrite
        // is not imported yet, each missing field logs a warning and is
        // skipped — the wire never throws and is safe to run any time.
        private static void WireCameraModes(Game.Camera.PlayerFollowCamera follow, Camera cam)
        {
            if (follow == null) return;
            var so = new SerializedObject(follow);
            bool touched = false;

            var p = so.FindProperty("defaultPitch");
            if (p != null) { p.floatValue = 45f; touched = true; } // F10.1: over-the-shoulder 45°
            else Debug.LogWarning("[SandboxBuilder] PlayerFollowCamera.defaultPitch missing (F10 camera rewrite not imported?).");

            p = so.FindProperty("shoulderOffset");
            if (p != null) { p.floatValue = 0.6f; touched = true; } // F10.1: over-the-shoulder lateral shift

            p = so.FindProperty("chaseDistance");
            if (p != null) { p.floatValue = 5.5f; touched = true; }
            else Debug.LogWarning("[SandboxBuilder] PlayerFollowCamera.chaseDistance missing (F10 camera rewrite not imported?).");

            // [F12 fix] Sensible shooter-grade sensitivity; scene instances that
            // predate the field hold the old 0.15 default.
            p = so.FindProperty("mouseSensitivity");
            if (p != null) { p.floatValue = 0.25f; touched = true; }

            // [F17 fix] Robust default: mouseInfluence = 1f serialized so ChaseBack locks cursor & responds to mouse immediately on start
            p = so.FindProperty("mouseInfluence");
            if (p != null) { p.floatValue = 1f; touched = true; }

            // [F25 fix] Wire modeManager so camera modes know if player is in Manual vs Commands mode
            var modeManager = Object.FindFirstObjectByType<Game.GameMode.GameModeManager>();
            if (modeManager != null)
            {
                p = so.FindProperty("modeManager");
                if (p != null) { p.objectReferenceValue = modeManager; touched = true; }
            }

            if (TryComputeOverheadFromBounds(cam, out var center, out var height))
            {
                p = so.FindProperty("overheadCenter");
                if (p != null) { p.vector3Value = center; touched = true; }
                else Debug.LogWarning("[SandboxBuilder] PlayerFollowCamera.overheadCenter missing (F10 camera rewrite not imported?).");

                p = so.FindProperty("overheadHeight");
                if (p != null) { p.floatValue = height; touched = true; }
                else Debug.LogWarning("[SandboxBuilder] PlayerFollowCamera.overheadHeight missing (F10 camera rewrite not imported?).");
            }

            if (touched) so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Finds the arena floor and derives the fixed top-down framing from its
        // real renderer bounds: expand by a 15% margin, then the height that
        // fits the whole XZ extent inside the camera's vertical FOV.
        // Multiplier 0.85 (~26% closer frame vs old 1.15) for tighter tactical view.
        // No ground -> clear error log and no overhead wire (existing builder pattern).
        private static bool TryComputeOverheadFromBounds(Camera cam, out Vector3 center, out float height)
        {
            center = Vector3.zero;
            height = 25f;
            var groundGo = GameObject.Find("Ground") ?? GameObject.Find("Arena") ?? GameObject.Find("Floor");
            var ground = groundGo != null ? groundGo.GetComponent<Renderer>() : null;
            if (ground == null)
            {
                Debug.LogError("[SandboxBuilder] No Ground/Arena/Floor renderer in scene — overheadCenter/overheadHeight NOT wired. Run Build Sandbox Scene first.");
                return false;
            }

            var b = ground.bounds;
            b.Expand(b.size * 0.15f); // 15% margin so edge spawns stay in frame
            float fov = cam != null ? cam.fieldOfView : 60f;
            float fovRad = fov * Mathf.Deg2Rad;
            float maxExtent = Mathf.Max(b.size.x, b.size.z) * 0.5f;
            height = Mathf.Max(10f, maxExtent / Mathf.Tan(fovRad * 0.5f) * 0.85f);
            center = b.center;
            Debug.Log("[SandboxBuilder] Camera overhead framing: center=(" + center.x.ToString("0.0") + ", " + center.y.ToString("0.0") + ", " + center.z.ToString("0.0")
                + "), height=" + height.ToString("0.0") + " (ground " + b.size.x.ToString("0") + "x" + b.size.z.ToString("0") + ", fov " + fov.ToString("0") + ")");
            return true;
        }

        // One-stop menu: wires the two-mode camera from the real arena bounds
        // AND adds the discreet bottom-left mode hint. Re-running never
        // duplicates (same find-by-name idempotency as the other builders).
        [MenuItem("MindGame/Setup/Build Camera Modes")]
        public static void BuildCameraModes()
        {
            // Works without a scene rebuild (Unity 6 API, no CS0618 warning).
            var follow = Object.FindFirstObjectByType<Game.Camera.PlayerFollowCamera>();
            GameObject camGo = follow != null
                ? follow.gameObject
                : (Camera.main != null ? Camera.main.gameObject : GameObject.Find("Main Camera"));
            if (follow == null)
            {
                Debug.LogError("[SandboxBuilder] No PlayerFollowCamera found. Run Build Sandbox Scene first.");
                return;
            }

            WireCameraModes(follow, camGo.GetComponent<Camera>());
            bool hintReady = EnsureCameraModeHint();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Camera modes wired (chase action camera)"
                + " (idempotent).");
        }

        // [UX-F3] Self-heal: update overheadHeight/overheadCenter on existing
        // PlayerFollowCamera in scene (e.g., after multiplier change in
        // TryComputeOverheadFromBounds). Idempotent: only touches fields if
        // values differ. Can be called standalone or after WireCameraModes.
        [MenuItem("MindGame/Setup/Self-Heal Camera Overhead")]
        public static void SelfHealCameraOverhead()
        {
            var follow = Object.FindFirstObjectByType<Game.Camera.PlayerFollowCamera>();
            if (follow == null)
            {
                Debug.LogError("[SandboxBuilder] No PlayerFollowCamera found in scene.");
                return;
            }
            var cam = follow.GetComponent<Camera>();
            if (cam == null) cam = Camera.main;

            var so = new SerializedObject(follow);
            bool touched = false;

            if (TryComputeOverheadFromBounds(cam, out var center, out var height))
            {
                var p = so.FindProperty("overheadCenter");
                if (p != null && p.vector3Value != center)
                {
                    p.vector3Value = center;
                    touched = true;
                }
                p = so.FindProperty("overheadHeight");
                if (p != null && !Mathf.Approximately(p.floatValue, height))
                {
                    p.floatValue = height;
                    touched = true;
                }
                if (touched)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("[SandboxBuilder] Self-heal camera overhead: center=" + center + ", height=" + height.ToString("0.0"));
                }
                else
                {
                    Debug.Log("[SandboxBuilder] Camera overhead already up to date.");
                }
            }
            else
            {
                Debug.LogError("[SandboxBuilder] Self-heal failed: could not compute overhead from bounds.");
            }
        }

        // [F53] Dead camera mode hint removal (1/2 keys were removed earlier).
        public static bool RemoveDeadCameraModeHint()
        {
            return Game.UI.BossModeHud.RemoveDeadCameraModeHint();
        }

        private static bool EnsureCameraModeHint()
        {
            return RemoveDeadCameraModeHint();
        }

        // ===== F11 (Aion1, append-only): intents runner wire + health pickups =====

        // Adds the WheelIntentRunner to the player if missing and wires its
        // serialized refs (executor + health). Idempotent: existing component
        // is re-wired, never duplicated.
        private static void EnsureWheelIntentRunner(Transform player)
        {
            if (player == null)
            {
                Debug.LogError("[SandboxBuilder] No player transform — WheelIntentRunner NOT wired.");
                return;
            }
            var runner = player.GetComponent<Game.Execution.WheelIntentRunner>()
                ?? player.gameObject.AddComponent<Game.Execution.WheelIntentRunner>();
            var exec = player.GetComponent<Game.Execution.ActionExecutor>();
            var health = player.GetComponent<Game.Combat.HealthSystem>();
            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>("Assets/Data/Actions/ActionManifest.asset");
            var so = new SerializedObject(runner);
            var p = so.FindProperty("executor");
            if (p != null) p.objectReferenceValue = exec; else Debug.LogWarning("[SandboxBuilder] WheelIntentRunner.executor property missing.");
            p = so.FindProperty("health");
            if (p != null) p.objectReferenceValue = health; else Debug.LogWarning("[SandboxBuilder] WheelIntentRunner.health property missing.");
            p = so.FindProperty("manifest");
            if (p != null) p.objectReferenceValue = manifest;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[SandboxBuilder] WheelIntentRunner ready on '" + player.name + "' (executor=" + (exec != null) + ", health=" + (health != null) + ")");
        }

        // One-stop menu: player intents runner + the 3 health pickups.
        [MenuItem("MindGame/Setup/Build Health Pickups")]
        public static void BuildHealthPickups()
        {
            var player = GameObject.Find("Player_YBot");
            if (player == null)
                player = GameObject.Find("Player");
            EnsureWheelIntentRunner(player != null ? player.transform : null);
            EnsureHealthPickups();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] DONE. Health pickups + intents runner ready (idempotent).");
        }

        // 3 health pickups in a symmetric triangle (radius 12 from origin,
        // y 0.5, angles 90/210/330). Empty parent + HealthPickup + a Visual
        // child sphere WITHOUT collider: HealthPickup heals by proximity in
        // Update (radius 1.5), and its own placeholder only builds at
        // Play/Awake when childCount == 0 — so a green visual child keeps the
        // pickups visible in the EDITOR too without duplicating anything.
        // Idempotent by name.
        private static void EnsureHealthPickups()
        {
            const float radius = 12f;
            const float y = 0.5f;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogError("[SandboxBuilder] No Lit shader found — health pickups NOT created.");
                return;
            }
            var green = new Color(0.2f, 0.9f, 0.3f, 1f);
            var material = new Material(shader);
            material.color = green;
            material.SetColor("_BaseColor", green); // URP naming
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", green * 0.6f);

            int created = 0;
            for (int i = 1; i <= 3; i++)
            {
                var name = "HealthPickup_" + i;
                var existing = GameObject.Find(name);
                if (existing != null)
                {
                    // [F50 lead self-heal] Pre-existing orbs carry healAmount=25 in
                    // scene YAML — the new default (5) would never apply to them.
                    // Force the +5 economy the same idempotent way as the create path.
                    var existingPickup = existing.GetComponent<Game.Combat.HealthPickup>();
                    if (existingPickup != null)
                    {
                        var so = new UnityEditor.SerializedObject(existingPickup);
                        var healProp = so.FindProperty("healAmount");
                        if (healProp != null && healProp.intValue != 5)
                        {
                            healProp.intValue = 5;
                            so.ApplyModifiedProperties();
                            UnityEditor.EditorUtility.SetDirty(existingPickup);
                            Debug.Log("[SandboxBuilder] HealthPickup healAmount self-healed → 5 (" + name + ").");
                        }
                    }
                    continue; // idempotent
                }

                float angle = (90f + (i - 1) * 120f) * Mathf.Deg2Rad; // symmetric triangle
                var go = new GameObject(name);
                go.transform.position = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);

                var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                visual.name = "Visual";
                var col = visual.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col); // edit-mode safe; proximity needs no collider
                visual.transform.SetParent(go.transform, false);
                visual.transform.localScale = Vector3.one * 0.4f;
                visual.GetComponent<Renderer>().sharedMaterial = material;

                go.AddComponent<Game.Combat.HealthPickup>();
                created++;
            }
            Debug.Log("[SandboxBuilder] Health pickups ready (created=" + created + ", existing=" + (3 - created) + ").");
        }

        // ===== F13 (Aion1, append-only): playable arena placeholder + map holder =====

        // Decorates the fight space WITHOUT touching the Ground (the F10
        // overhead framing and the pickups depend on its 40x40 bounds):
        // low perimeter walls (2m, inside the ground edge) with a 6m gate per
        // side, 4 corner pillars and a decorative central podium, all under an
        // empty 'ArenaMapHolder' ready to receive the user's future FBX/GLB
        // arena (drag it under the holder, scale to 40x40).
        //
        // SPAWN SAFETY (verified before closing walls): GruntSpawner spawns on
        // a ring at arenaRadius = 14 around the center (GruntSpawner.cs:29,
        // PickSpawnPoint) — well INSIDE the wall line, so walls never bury a
        // spawn; the four gates keep the corridors open regardless.
        [MenuItem("MindGame/Setup/Build Arena")]
        public static void BuildArena()
        {
            EnsureArenaEnvironment();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[SandboxBuilder] Arena ready. Drop the user's arena FBX/GLB under 'ArenaMapHolder' and scale it to 40x40.");
        }

        // Shared by the menu AND the Build Sandbox Scene hook. GUARD: if a
        // user map is already installed under ArenaMapHolder, placeholders are
        // NOT regenerated (rebuilds must never stomp the real map).
        private static void EnsureArenaEnvironment()
        {
            var holder = EnsureArenaMapHolder();
            if (IsUserMapInstalled(holder.transform))
            {
                Debug.Log("[SandboxBuilder] Arena: user map detected under 'ArenaMapHolder' — placeholders NOT regenerated.");
                return;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogError("[SandboxBuilder] No Lit shader found — arena NOT created.");
                return;
            }
            var stone = new Color(0.35f, 0.36f, 0.38f, 1f); // dark gray stone tint
            var material = new Material(shader);
            material.color = stone;
            material.SetColor("_BaseColor", stone); // URP naming

            int walls = 0, pillars = 0;

            // Wall center line at 19.4, thickness 0.5 -> outer face 19.65 < 20:
            // nothing exceeds the 40x40 frame. Segment length 16.4 + 6m gate.
            const float half = 19.4f;
            const float gateHalf = 3f;
            const float segHalf = half - gateHalf; // 16.4 total segment length
            float segCenter = gateHalf + segHalf / 2f; // 11.2
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_N1", new Vector3(-segCenter, 1f, half), new Vector3(segHalf * 2f, 2f, 0.5f), material);
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_N2", new Vector3(segCenter, 1f, half), new Vector3(segHalf * 2f, 2f, 0.5f), material);
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_S1", new Vector3(-segCenter, 1f, -half), new Vector3(segHalf * 2f, 2f, 0.5f), material);
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_S2", new Vector3(segCenter, 1f, -half), new Vector3(segHalf * 2f, 2f, 0.5f), material);
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_E1", new Vector3(half, 1f, segCenter), new Vector3(0.5f, 2f, segHalf * 2f), material);
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_E2", new Vector3(half, 1f, -segCenter), new Vector3(0.5f, 2f, segHalf * 2f), material);
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_W1", new Vector3(-half, 1f, segCenter), new Vector3(0.5f, 2f, segHalf * 2f), material);
            walls += EnsureArenaPiece(holder.transform, "Arena_Wall_W2", new Vector3(-half, 1f, -segCenter), new Vector3(0.5f, 2f, segHalf * 2f), material);

            // Corner pillars (static cubes, out of the main fight lanes).
            pillars += EnsureArenaPiece(holder.transform, "Arena_Pillar_1", new Vector3(16.5f, 2.5f, 16.5f), new Vector3(1.2f, 5f, 1.2f), material);
            pillars += EnsureArenaPiece(holder.transform, "Arena_Pillar_2", new Vector3(-16.5f, 2.5f, 16.5f), new Vector3(1.2f, 5f, 1.2f), material);
            pillars += EnsureArenaPiece(holder.transform, "Arena_Pillar_3", new Vector3(16.5f, 2.5f, -16.5f), new Vector3(1.2f, 5f, 1.2f), material);
            pillars += EnsureArenaPiece(holder.transform, "Arena_Pillar_4", new Vector3(-16.5f, 2.5f, -16.5f), new Vector3(1.2f, 5f, 1.2f), material);

            // Central podium — decorative only (collider removed: bots navigate
            // in straight lines, a solid disc at the center would trap them).
            int podium = EnsureArenaPodium(holder.transform, "Arena_Podium", material);

            Debug.Log("[SandboxBuilder] Arena ready (walls=" + walls + ", pillars=" + pillars + ", podium=" + podium + ", spawn gates open). Drop the user's arena FBX/GLB under 'ArenaMapHolder' and scale it to 40x40.");
        }

        // User-map detection: OUR placeholders are the EXACT names below with
        // primitive meshes; ANY other child under the holder (a dragged FBX/GLB,
        // a renamed mesh, or even an empty marker GO like 'userMapInstalled')
        // counts as the user's map and blocks placeholder regeneration.
        private static bool IsUserMapInstalled(Transform holder)
        {
            var placeholderNames = new HashSet<string>
            {
                "Arena_Wall_N1", "Arena_Wall_N2", "Arena_Wall_S1", "Arena_Wall_S2",
                "Arena_Wall_E1", "Arena_Wall_E2", "Arena_Wall_W1", "Arena_Wall_W2",
                "Arena_Pillar_1", "Arena_Pillar_2", "Arena_Pillar_3", "Arena_Pillar_4",
                "Arena_Podium"
            };
            var primitiveMeshes = new HashSet<string> { "Cube", "Sphere", "Cylinder", "Plane", "Quad", "Capsule" };
            foreach (Transform child in holder)
            {
                if (!placeholderNames.Contains(child.name)) return true;
                var mf = child.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && !primitiveMeshes.Contains(mf.sharedMesh.name)) return true;
            }
            return false;
        }

        private static GameObject EnsureArenaMapHolder()
        {
            var holder = GameObject.Find("ArenaMapHolder");
            if (holder == null)
            {
                holder = new GameObject("ArenaMapHolder");
                Debug.Log("[SandboxBuilder] 'ArenaMapHolder' created — drop the user's arena FBX/GLB under it and scale to 40x40.");
            }
            return holder;
        }

        private static int EnsureArenaPiece(Transform parent, string name, Vector3 pos, Vector3 size, Material material)
        {
            if (parent.Find(name) != null) return 0; // idempotent per piece
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material; // BoxCollider included — nobody leaves the ring
            return 1;
        }

        private static int EnsureArenaPodium(Transform parent, string name, Material material)
        {
            if (parent.Find(name) != null) return 0; // idempotent
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(0f, 0.1f, 0f); // cylinder primitive: spans y 0..0.2 at scaleY 0.1
            go.transform.localScale = new Vector3(8f, 0.1f, 8f); // radius 4, height 0.2
            go.GetComponent<Renderer>().sharedMaterial = material;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col); // walk-through decoration
            return 1;
        }

        [MenuItem("MindGame/Setup/Build SuperPunch")]
        public static void BuildSuperPunch()
        {
            const string assetPath = ActionsFolder + "/SuperPunch.asset";
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(assetPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
                var so = new SerializedObject(def);
                so.FindProperty("actionId").stringValue = "SuperPunch";
                so.FindProperty("displayName").stringValue = "Súper Puñetazo";
                so.FindProperty("clipName").stringValue = "ComboPunch";
                so.FindProperty("duration").floatValue = 1.2f;
                so.FindProperty("range").floatValue = 3.0f;
                so.FindProperty("staminaCost").floatValue = 25f;
                so.FindProperty("fallbackActionId").stringValue = "Punch";
                so.FindProperty("isHeavy").boolValue = true;
                so.FindProperty("needsTarget").boolValue = true;
                var ap = so.FindProperty("aliases");
                ap.arraySize = 4;
                ap.GetArrayElementAtIndex(0).stringValue = "super punch";
                ap.GetArrayElementAtIndex(1).stringValue = "superpuñetazo";
                ap.GetArrayElementAtIndex(2).stringValue = "super golpe";
                ap.GetArrayElementAtIndex(3).stringValue = "falcon punch";
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(def, assetPath);
                Debug.Log("[SandboxBuilder] Created SuperPunch.asset");
            }

            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>("Assets/Data/Actions/ActionManifest.asset");
            if (manifest != null && def != null)
            {
                var mso = new SerializedObject(manifest);
                var actionsProp = mso.FindProperty("actions");
                bool found = false;
                for (int i = 0; i < actionsProp.arraySize; i++)
                {
                    if (actionsProp.GetArrayElementAtIndex(i).objectReferenceValue == def)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    int idx = actionsProp.arraySize;
                    actionsProp.InsertArrayElementAtIndex(idx);
                    actionsProp.GetArrayElementAtIndex(idx).objectReferenceValue = def;
                    mso.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(manifest);
                    Debug.Log("[SandboxBuilder] Added SuperPunch to ActionManifest");
                }
            }

            BuildAnimatorControllers();
            AssetDatabase.SaveAssets();
            Debug.Log("[SandboxBuilder] DONE. SuperPunch built and verified (idempotent).");
        }

        // [F18] Entrada del boss con animación de power up. El proyecto no tiene
        // un clip "power up" propio del mutante; reutilizamos SwordShield_PowerUp
        // (pack del jugador, esqueleto Mixamo Genérico — mismo esqueleto
        // mixamorig, retarget directo al rig del boss). Si se quiere un power up
        // específico del mutante: descargar de Mixamo un clip "Power Up"/"Roar"
        // con rig GENÉRICO (nunca Humanoid) y apuntar clipName a ese clip.
        [MenuItem("MindGame/Setup/Build Boss Power Up")]
        public static void BuildBossPowerUp()
        {
            EnsureBossPowerUpAction();
            BuildAnimatorControllers();
            AssetDatabase.SaveAssets();
            Debug.Log("[SandboxBuilder] DONE. BossPowerUp ready (idempotent).");
        }

        // [F18] El golpe base armado ahora usa el clip Slash (pedido del
        // usuario: "el slash de espada como golpe base"). SwordShield_Slash
        // (2.43s) vs SwordShield_Attack (2.33s): duración casi idéntica, mismo
        // pack Mixamo GENÉRICO → retarget directo; duration del asset (1.3s,
        // tuning del lead) y travelDistance (3.48, calibrado F5) intactos.
        // Idempotente: se corrige a sí mismo si el asset se revierte.
        [MenuItem("MindGame/Setup/Build Attack Sword Slash")]
        public static void BuildAttackSwordSlash()
        {
            EnsureAttackSwordSlash();
            BuildAnimatorControllers(); // re-asigna state.motion en cada run (BuildController:541)
            AssetDatabase.SaveAssets();
            Debug.Log("[SandboxBuilder] DONE. AttackSword usa SwordShield_Slash (idempotente).");
        }

        private static void EnsureAttackSwordSlash()
        {
            var sword = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(ActionsFolder + "/AttackSword.asset");
            if (sword == null)
            {
                Debug.LogWarning("[SandboxBuilder] AttackSword.asset no encontrado — no se puede aplicar el clip Slash.");
                return;
            }
            if (sword.ClipName == "SwordShield_Slash") return; // ya aplicado
            var so = new SerializedObject(sword);
            so.FindProperty("clipName").stringValue = "SwordShield_Slash";
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(sword);
            Debug.Log("[SandboxBuilder] AttackSword clipName -> SwordShield_Slash (Slash2 disponible para variedad futura).");
        }

        private static void EnsureBossPowerUpAction()
        {
            const string assetPath = ActionsFolder + "/BossPowerUp.asset";
            var def = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionDefinition>(assetPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<Game.Actions.ActionDefinition>();
                var so = new SerializedObject(def);
                so.FindProperty("actionId").stringValue = "BossPowerUp";
                so.FindProperty("displayName").stringValue = "Power Up (entrada del boss)";
                so.FindProperty("clipName").stringValue = "SwordShield_PowerUp";
                float duration = 2.5f;
                var clip = FindClip("SwordShield_PowerUp");
                if (clip != null) duration = Mathf.Max(0.5f, clip.length);
                else Debug.LogWarning("[SandboxBuilder] Clip SwordShield_PowerUp no encontrado; duración por defecto 2.5s.");
                so.FindProperty("duration").floatValue = duration;
                so.FindProperty("range").floatValue = 0f; // no ataca: es solo la animación de entrada
                so.FindProperty("staminaCost").floatValue = 0f;
                so.FindProperty("fallbackActionId").stringValue = "Idle";
                so.FindProperty("roleRestriction").intValue = 1; // ActionRole.BossOnly — estado solo en el BossController
                so.FindProperty("needsTarget").boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(def, assetPath);
                Debug.Log("[SandboxBuilder] Created BossPowerUp.asset (dur=" + duration.ToString("0.00") + "s).");
            }

            var manifest = AssetDatabase.LoadAssetAtPath<Game.Actions.ActionManifest>("Assets/Data/Actions/ActionManifest.asset");
            if (manifest != null && def != null)
            {
                var mso = new SerializedObject(manifest);
                var actionsProp = mso.FindProperty("actions");
                bool found = false;
                for (int i = 0; i < actionsProp.arraySize; i++)
                {
                    if (actionsProp.GetArrayElementAtIndex(i).objectReferenceValue == def)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    int idx = actionsProp.arraySize;
                    actionsProp.InsertArrayElementAtIndex(idx);
                    actionsProp.GetArrayElementAtIndex(idx).objectReferenceValue = def;
                    mso.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(manifest);
                    Debug.Log("[SandboxBuilder] Added BossPowerUp to ActionManifest");
                }
            }
        }
    }
}
