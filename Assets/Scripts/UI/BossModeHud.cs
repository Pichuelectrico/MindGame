using UnityEngine;
using UnityEngine.UI;
using Game.AI;

namespace Game.UI
{
    /// <summary>
    /// [F53][UI-F5] Real-time HUD indicator for active Boss Decision Mode:
    /// "BOSS: LLM" / "BOSS: Heurístico" / "BOSS: Estratega · Distance|Fighter|Normal"
    /// with suffix " · FURIA" when enraged. Polled every 0.25s.
    /// Also displays visual feedback when LLM or Strategist decisions are taken:
    /// - " · 🧠 Decidiendo…" when a query is in-flight (soft cyan pulse).
    /// - " · ✔ Decisión aplicada" when decision is applied (bright green flash for 1.5s).
    /// Mode and persona changes are logged to DecisionLogger.LogMode.
    /// </summary>
    public sealed class BossModeHud : MonoBehaviour
    {
        public enum DecisionState
        {
            Idle,
            Deciding,
            Applied
        }

        [SerializeField] private Text label;
        [SerializeField] private BotController boss;
        [SerializeField] private float pollInterval = 0.25f;
        [SerializeField] private float appliedFlashDuration = 1.5f;
        [SerializeField] private float centralInstructionsDuration = 30f;
        [SerializeField] private bool autoHideCentralInstructions = true;

        [SerializeField] private Color normalColor = new Color(1f, 1f, 1f, 0.9f);
        [SerializeField] private Color decidingColor = new Color(0.4f, 0.85f, 1f, 1f);
        [SerializeField] private Color appliedColor = new Color(0.4f, 1f, 0.4f, 1f);

        private float timer;
        private float appliedTimer;
        private float centralInstructionsTimer;
        private bool centralInstructionsHidden;
        private DecisionState currentDecisionState = DecisionState.Idle;
        private bool subscribed;

        private BotController.BossDecisionMode? lastMode;
        private BossPersona? lastPersona;
        private bool hasLoggedInitial;

        public Text Label => label;
        public BotController Boss => boss;
        public float PollInterval => pollInterval;
        public float AppliedFlashDuration { get => appliedFlashDuration; set => appliedFlashDuration = value; }
        public DecisionState CurrentDecisionState => currentDecisionState;
        public bool IsDeciding => currentDecisionState == DecisionState.Deciding;
        public bool IsAppliedFlash => currentDecisionState == DecisionState.Applied;
        public bool CentralInstructionsHidden => centralInstructionsHidden;
        public float CentralInstructionsDuration { get => centralInstructionsDuration; set => centralInstructionsDuration = value; }
        public bool AutoHideCentralInstructions { get => autoHideCentralInstructions; set => autoHideCentralInstructions = value; }

        public void SetBoss(BotController newBoss)
        {
            if (boss != newBoss)
            {
                UnsubscribeBoss();
                boss = newBoss;
                if (isActiveAndEnabled) SubscribeBoss();
            }
        }

        public void SetLabel(Text newLabel) => label = newLabel;

        // Public test hooks
        public void TriggerDecisionStarted() => HandleDecisionStarted();
        public void TriggerDecisionApplied() => HandleDecisionApplied();

        private void Awake()
        {
            if (label == null) label = GetComponent<Text>();
            if (boss == null) ResolveBoss();
        }

        private void OnEnable()
        {
            if (boss == null) ResolveBoss();
            SubscribeBoss();
            Refresh();
        }

        private void OnDisable()
        {
            UnsubscribeBoss();
        }

        private void SubscribeBoss()
        {
            if (boss != null && !subscribed)
            {
                boss.OnLlmDecisionStarted += HandleDecisionStarted;
                boss.OnLlmDecision += HandleDecisionApplied;
                subscribed = true;
            }
        }

        private void UnsubscribeBoss()
        {
            if (boss != null && subscribed)
            {
                boss.OnLlmDecisionStarted -= HandleDecisionStarted;
                boss.OnLlmDecision -= HandleDecisionApplied;
                subscribed = false;
            }
        }

        private void HandleDecisionStarted()
        {
            currentDecisionState = DecisionState.Deciding;
            Refresh();
        }

        private void HandleDecisionApplied()
        {
            currentDecisionState = DecisionState.Applied;
            appliedTimer = appliedFlashDuration;
            Refresh();
        }

        private void Update()
        {
            // Flash timer countdown
            if (currentDecisionState == DecisionState.Applied)
            {
                appliedTimer -= Time.unscaledDeltaTime;
                if (appliedTimer <= 0f)
                {
                    currentDecisionState = DecisionState.Idle;
                    Refresh();
                }
            }

            // [UI-F4] Auto-hide central instruction texts after 30s of gameplay
            if (autoHideCentralInstructions && !centralInstructionsHidden)
            {
                centralInstructionsTimer += Time.unscaledDeltaTime;
                if (centralInstructionsTimer >= centralInstructionsDuration)
                {
                    centralInstructionsHidden = true;
                    HideCentralInstructions();
                }
            }

            // Visual color feedback
            if (label != null)
            {
                if (currentDecisionState == DecisionState.Deciding)
                {
                    float t = Mathf.PingPong(Time.unscaledTime * 4f, 1f);
                    label.color = Color.Lerp(decidingColor, normalColor, t);
                }
                else if (currentDecisionState == DecisionState.Applied)
                {
                    float t = Mathf.Clamp01(appliedTimer / appliedFlashDuration);
                    label.color = Color.Lerp(normalColor, appliedColor, t);
                }
                else
                {
                    label.color = normalColor;
                }
            }

            timer -= Time.unscaledDeltaTime;
            if (timer <= 0f)
            {
                timer = pollInterval;
                Refresh();
            }
        }

        private void ResolveBoss()
        {
            var bossGo = GameObject.Find("Boss_Mutant");
            if (bossGo != null) boss = bossGo.GetComponent<BotController>();
            if (boss == null) boss = FindFirstObjectByType<BotController>();
        }

        public void Refresh()
        {
            if (boss == null)
            {
                ResolveBoss();
                if (boss == null)
                {
                    // Sin boss aún: mostrar el label con defaults (nunca dejar el
                    // texto vacío — en runtime el boss aparece luego y Refresh lo
                    // sincroniza; en tests boss-less el indicador de decisión
                    // sigue funcionando).
                    if (label != null)
                    {
                        label.text = FormatText(BotController.BossDecisionMode.Llm, BossPersona.None, false, currentDecisionState);
                    }
                    return;
                }
            }

            // Sync with boss in case decision started while disabled or before event hooked
            if (boss.IsLlmDeciding && currentDecisionState == DecisionState.Idle)
            {
                currentDecisionState = DecisionState.Deciding;
            }

            var currentMode = boss.Mode;
            var currentPersona = boss.CurrentPersona;
            bool isFuria = boss.IsFuria;

            if (label != null)
            {
                label.text = FormatText(currentMode, currentPersona, isFuria, currentDecisionState);
            }

            // Mode or persona change detection -> DecisionLogger
            if (!hasLoggedInitial || !lastMode.HasValue || lastMode.Value != currentMode || !lastPersona.HasValue || lastPersona.Value != currentPersona)
            {
                hasLoggedInitial = true;
                lastMode = currentMode;
                lastPersona = currentPersona;
                DecisionLogger.LogMode(GetModeName(currentMode), GetPersonaName(currentPersona));
            }
        }

        public static string GetModeName(BotController.BossDecisionMode mode)
        {
            switch (mode)
            {
                case BotController.BossDecisionMode.ForcedHeuristic: return "Heurístico";
                case BotController.BossDecisionMode.Strategist: return "Estratega";
                default: return "LLM";
            }
        }

        public static string GetPersonaName(BossPersona persona)
        {
            switch (persona)
            {
                case BossPersona.Distance: return "Distance";
                case BossPersona.Fighter: return "Fighter";
                default: return "Normal";
            }
        }

        public static string FormatText(BotController.BossDecisionMode mode, BossPersona persona, bool isFuria)
        {
            return FormatText(mode, persona, isFuria, DecisionState.Idle);
        }

        public static string FormatText(BotController.BossDecisionMode mode, BossPersona persona, bool isFuria, DecisionState decisionState)
        {
            string text;
            if (mode == BotController.BossDecisionMode.Strategist)
            {
                text = "BOSS: Estratega \u00B7 " + GetPersonaName(persona);
            }
            else
            {
                text = "BOSS: " + GetModeName(mode);
            }

            if (isFuria)
            {
                text += " \u00B7 FURIA";
            }

            if (decisionState == DecisionState.Deciding)
            {
                text += " \u00B7 \uD83E\uDDE0 Decidiendo\u2026";
            }
            else if (decisionState == DecisionState.Applied)
            {
                text += " \u00B7 \u2714 Decisi\u00F3n aplicada";
            }

            return text;
        }

        // [F53] Idempotent dead hint removal (1/2 keys were removed earlier).
        public static bool RemoveDeadCameraModeHint()
        {
            var existing = GameObject.Find("CameraModeHint");
            if (existing != null)
            {
                DestroyImmediate(existing);
                Debug.Log("[BossModeHud] CameraModeHint removido (teclas 1/2 retiradas del juego).");
            }
            return true;
        }

        // [UI-F4] Helper to hide central instruction texts on CommandCanvas after 30s
        public static void HideCentralInstructions()
        {
            var canvas = GameObject.Find("CommandCanvas");
            if (canvas == null) return;
            var cmdUI = canvas.GetComponent<CommandInputUI>();
            if (cmdUI != null)
            {
                cmdUI.HideInstructions();
            }
            else
            {
                var title = canvas.transform.Find("Title");
                if (title != null) title.gameObject.SetActive(false);
                var feedback = canvas.transform.Find("Feedback");
                if (feedback != null) feedback.gameObject.SetActive(false);
            }
        }
    }
}
