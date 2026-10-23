using System.Collections.Generic;
using System.Text;
using Game.Actions;
using Game.Combat;
using Game.Core;
using Game.Execution;
using Game.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Card wheel for building a plan during active pause. Text input stays as an
    // alternative. Mirrors SandboxTestDriver.RunPlan: EnterPlanning (on open) ->
    // AcceptPlan(LoadPlan(json)) (on EJECUTAR) -> OnPlanFinished by the executor.
    [DisallowMultipleComponent]
    public sealed class ActionWheel : MonoBehaviour
    {
        [SerializeField] private ActionManifest manifest;
        [SerializeField] private ActionExecutor executor;
        [SerializeField] private TimeController time;
        [SerializeField] private CommandInputUI ui;
        [SerializeField] private Game.GameMode.GameModeManager modeManager;
        [SerializeField] private GameObject wheelRoot;
        [SerializeField] private Button openButton;
        [SerializeField] private string[] wheelActionIds =
        {
            "Run", "Walk", "AttackSword", "Punch", "AttackKick", "AttackCombo",
            "AttackSlide", "ThrowWeapon", "Dodge", "Roll", "Jump", "BlockLeft", "BlockCenter"
        };
        [SerializeField] private float radius = 220f;

        private readonly List<PlannedCard> plan = new List<PlannedCard>();
        private readonly List<Button> cardButtons = new List<Button>();
        private Button intentWeaponBtn;
        private Button intentHealthBtn;
        private Button intentMeleeBtn;
        private Text chipsText;
        private Text counterText;
        private Text noticeText;

        private sealed class PlannedCard
        {
            public ActionDefinition Def;
        }

        private void Awake()
        {
            if (wheelRoot != null) wheelRoot.SetActive(false);
            if (openButton != null) openButton.onClick.AddListener(TryOpen);
            Debug.Log("[ActionWheel] Pulsa R o el botón 'Rueda [R]' (abajo-derecha) para abrir la rueda de acciones.");
        }

        private void OnDestroy()
        {
            if (openButton != null) openButton.onClick.RemoveListener(TryOpen);
        }

        private void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || !kb.rKey.wasPressedThisFrame) return;
            if (wheelRoot != null && wheelRoot.activeSelf) return;
            TryOpen();
        }

        public void Bind(GameObject root, Button open)
        {
            if (wheelRoot == null) wheelRoot = root;
            if (openButton == null)
            {
                openButton = open;
                openButton.onClick.AddListener(TryOpen);
            }
        }

        private bool InManualMode()
        {
            return modeManager != null && modeManager.CurrentMode == Game.GameMode.GameMode.Manual;
        }

        // [F12 fix] Global open-state so the camera can release cursor capture
        // while the player picks cards (needs a free cursor to click them).
        public static bool WheelOpen { get; private set; }

        public void TryOpen()
        {
            if (wheelRoot != null && wheelRoot.activeSelf) return;
            if (manifest == null || executor == null || time == null) return;
            if (InManualMode()) return;
            if (time.Current != CombatPhase.Running) return;
            if (!time.EnterPlanning()) return;
            Rebuild();
            wheelRoot.SetActive(true);
            WheelOpen = true;
            if (openButton != null) openButton.gameObject.SetActive(false);
        }

        public void Cancel()
        {
            plan.Clear();
            if (wheelRoot != null) wheelRoot.SetActive(false);
            WheelOpen = false;
            if (openButton != null) openButton.gameObject.SetActive(true);
            if (time != null) time.CancelPlanning();
            ui?.SetFeedback("plan descartado", false);
        }

        public void ClearPlan()
        {
            plan.Clear();
            RefreshPlanView();
            RefreshCards();
        }

        public void Execute()
        {
            if (plan.Count == 0)
            {
                Notice("elige al menos 1 acción");
                return;
            }
            string json = BuildJson();
            if (!executor.LoadPlan(json))
            {
                ui?.SetFeedback("plan inválido", false);
                return;
            }
            ui?.SetFeedback("plan aceptado (" + plan.Count + ")", true);
            wheelRoot.SetActive(false);
            WheelOpen = false;
            if (openButton != null) openButton.gameObject.SetActive(true);
            if (!time.AcceptPlan(executor))
                time.CancelPlanning();
        }

        private void OnCardClicked(ActionDefinition def)
        {
            if (def == null || plan.Count >= manifest.MaxPlanSize) return;
            AddToPlan(def);
        }

        private void AddToPlan(ActionDefinition def)
        {
            plan.Add(new PlannedCard { Def = def });
            Debug.Log("[ActionWheel] +" + def.ActionId
                + " (" + plan.Count + "/" + manifest.MaxPlanSize + ")");
            RefreshPlanView();
            RefreshCards();
        }

        private string BuildJson()
        {
            var sb = new StringBuilder("{\"actions\":[");
            for (int i = 0; i < plan.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"type\":\"").Append(plan[i].Def.ActionId).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private void Rebuild()
        {
            plan.Clear();
            foreach (Transform child in wheelRoot.transform)
                Object.Destroy(child.gameObject);
            cardButtons.Clear();

            var defs = new List<ActionDefinition>();
            foreach (var id in wheelActionIds)
            {
                var def = manifest.Find(id);
                if (def != null) defs.Add(def);
                else Debug.LogWarning("[ActionWheel] Unknown wheel id '" + id + "', skipped.");
            }
            for (int i = 0; i < defs.Count; i++)
            {
                float angle = i * Mathf.PI * 2f / defs.Count - Mathf.PI / 2f;
                Vector2 pos = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                MakeCard(defs[i], pos);
            }
            MakeCenter();
            MakeIntentsRow();
            MakeBottomBar();
            // Clickability: decorative texts must never eat raycasts meant for cards.
            // Only Button Images stay raycastable.
            foreach (var t in wheelRoot.GetComponentsInChildren<Text>(true))
                t.raycastTarget = false;
            RefreshPlanView();
            RefreshCards();
        }

        private void MakeCard(ActionDefinition def, Vector2 pos)
        {
            var go = new GameObject("Card_" + def.ActionId, typeof(RectTransform));
            go.transform.SetParent(wheelRoot.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(120f, 64f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.2f, 0.35f, 0.95f);
            var btn = go.AddComponent<Button>();
            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var t = label.AddComponent<Text>();
            t.text = def.DisplayName;
            t.fontSize = 14;
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.color = Color.white;
            btn.onClick.AddListener(() => OnCardClicked(def));
            cardButtons.Add(btn);
        }

        private void MakeCenter()
        {
            var go = new GameObject("Center", typeof(RectTransform));
            go.transform.SetParent(wheelRoot.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(140f, 60f);
            counterText = go.AddComponent<Text>();
            counterText.fontSize = 20;
            counterText.alignment = TextAnchor.MiddleCenter;
            counterText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            counterText.color = Color.white;
        }

        // [F11] Intent cards (go-pick-weapon / go-take-health). They bypass the
        // plan: WheelIntentRunner validates, exits Planning itself and steers
        // the player. Always visible; grayed out when not applicable (double
        // layer — the runner re-validates on click).
        private void MakeIntentsRow()
        {
            var row = new GameObject("IntentsRow", typeof(RectTransform));
            row.transform.SetParent(wheelRoot.transform, false);
            var rt = (RectTransform)row.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, -190f);
            rt.sizeDelta = new Vector2(360f, 48f);
            intentWeaponBtn = MakeIntentButton(row.transform, "Intent_GoPickupWeapon", "Ir por el arma", new Vector2(-92f, 0f), WheelIntent.GoPickupWeapon);
            intentHealthBtn = MakeIntentButton(row.transform, "Intent_GoTakeHealth", "Ir por vida", new Vector2(92f, 0f), WheelIntent.GoTakeHealth);
            intentMeleeBtn = MakeIntentButton(row.transform, "Intent_MeleeNearest", "Golpear", new Vector2(0f, 52f), WheelIntent.MeleeNearest);
            RefreshIntents();
        }

        private Button MakeIntentButton(Transform parent, string name, string label, Vector2 pos, WheelIntent intent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(168f, 44f);
            var img = go.AddComponent<Image>();
            img.color = IntentEnabledColor;
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(() => OnIntentClicked(intent));
            var lgo = new GameObject("Label", typeof(RectTransform));
            lgo.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)lgo.transform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var t = lgo.AddComponent<Text>();
            t.text = label;
            t.fontSize = 14;
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.color = Color.white;
            return btn;
        }

        private static readonly Color IntentEnabledColor = new Color(0.2f, 0.65f, 0.35f, 1f);
        private static readonly Color IntentDisabledColor = new Color(0.25f, 0.25f, 0.25f, 0.6f);

        private void OnIntentClicked(WheelIntent intent)
        {
            var runner = executor != null ? executor.GetComponent<WheelIntentRunner>() : null;
            if (runner == null)
            {
                Notice("intents no disponibles (falta WheelIntentRunner en el jugador)");
                Debug.LogError("[ActionWheel] WheelIntentRunner missing on the player — run Build Sandbox Scene (it wires the runner).");
                return;
            }
            if (runner.RunFromWheel(intent, time != null ? time.Machine : null))
            {
                wheelRoot.SetActive(false);
                if (openButton != null) openButton.gameObject.SetActive(true);
            }
            else
            {
                Notice("no se puede: " + (intent == WheelIntent.GoPickupWeapon ? "ya tienes el arma" : intent == WheelIntent.MeleeNearest ? "sin enemigo en rango" : "vida llena o sin pickups"));
            }
        }

        private void RefreshIntents()
        {
            if (intentMeleeBtn != null)
            {
                bool usable = executor != null && executor.GetComponent<HealthSystem>() != null
                    && !executor.GetComponent<HealthSystem>().IsDead;
                intentMeleeBtn.interactable = usable;
                var img = intentMeleeBtn.GetComponent<Image>();
                if (img != null) img.color = usable ? IntentEnabledColor : IntentDisabledColor;
            }
            if (intentWeaponBtn != null)
            {
                bool usable = executor != null && !executor.HasWeapon;
                intentWeaponBtn.interactable = usable;
                var img = intentWeaponBtn.GetComponent<Image>();
                if (img != null) img.color = usable ? IntentEnabledColor : IntentDisabledColor;
            }
            if (intentHealthBtn != null)
            {
                var hp = executor != null ? executor.GetComponent<HealthSystem>() : null;
                bool usable = hp != null && hp.CurrentHealth < hp.MaxHealth && HealthPickup.Active.Count > 0;
                intentHealthBtn.interactable = usable;
                var img = intentHealthBtn.GetComponent<Image>();
                if (img != null) img.color = usable ? IntentEnabledColor : IntentDisabledColor;
            }
        }

        private void MakeMiniButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Dir_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(110f, 44f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.45f, 0.9f, 1f);
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(onClick);
            var lgo = new GameObject("Label", typeof(RectTransform));
            lgo.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)lgo.transform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var t = lgo.AddComponent<Text>();
            t.text = label;
            t.fontSize = 14;
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.color = Color.white;
        }

        private void MakeBottomBar()
        {
            var bar = new GameObject("BottomBar", typeof(RectTransform));
            bar.transform.SetParent(wheelRoot.transform, false);
            var rt = (RectTransform)bar.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, -300f);
            rt.sizeDelta = new Vector2(560f, 100f);

            chipsText = bar.AddComponent<Text>();
            var crt = (RectTransform)chipsText.transform;
            crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
            crt.offsetMin = new Vector2(0f, 44f); crt.offsetMax = Vector2.zero;
            chipsText.fontSize = 15;
            chipsText.alignment = TextAnchor.MiddleCenter;
            chipsText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            chipsText.color = Color.white;

            noticeText = new GameObject("Notice", typeof(RectTransform)).AddComponent<Text>();
            noticeText.transform.SetParent(bar.transform, false);
            var nrt = (RectTransform)noticeText.transform;
            nrt.anchorMin = nrt.anchorMax = nrt.pivot = new Vector2(0.5f, 0.5f);
            nrt.anchoredPosition = new Vector2(0f, 22f);
            nrt.sizeDelta = new Vector2(540f, 24f);
            noticeText.fontSize = 13;
            noticeText.alignment = TextAnchor.MiddleCenter;
            noticeText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            noticeText.color = Color.yellow;

            MakeBarButton(bar.transform, "Limpiar", new Vector2(-190f, -22f), ClearPlan);
            MakeBarButton(bar.transform, "Cancelar", new Vector2(0f, -22f), Cancel);
            MakeBarButton(bar.transform, "EJECUTAR", new Vector2(190f, -22f), Execute);
        }

        private void MakeBarButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            MakeMiniButton(parent, label, pos, onClick);
        }

        private void RefreshPlanView()
        {
            if (counterText != null)
                counterText.text = "Plan: " + plan.Count + "/" + manifest.MaxPlanSize;
            if (chipsText == null) return;
            if (plan.Count == 0) { chipsText.text = "toca cartas para armar el plan"; return; }
            var sb = new StringBuilder();
            for (int i = 0; i < plan.Count; i++)
            {
                if (i > 0) sb.Append(" → ");
                sb.Append(plan[i].Def.ActionId);
            }
            chipsText.text = sb.ToString();
        }

        private void RefreshCards()
        {
            bool full = plan.Count >= manifest.MaxPlanSize;
            foreach (var btn in cardButtons)
            {
                if (btn == null) continue;
                string id = btn.name.Substring("Card_".Length);
                var def = manifest.Find(id);
                bool usable = !full && def != null
                    && def.IsAvailable(executor.HasWeapon, ActionRole.Any, executor.Stamina);
                btn.interactable = usable;
                var img = btn.GetComponent<Image>();
                if (img != null) img.color = usable
                    ? new Color(0.15f, 0.2f, 0.35f, 0.95f)
                    : new Color(0.25f, 0.25f, 0.25f, 0.6f);
            }
            if (full) Notice("plan lleno (" + manifest.MaxPlanSize + ")");
        }

        private void Notice(string msg)
        {
            if (noticeText != null) noticeText.text = msg;
        }
    }
}
